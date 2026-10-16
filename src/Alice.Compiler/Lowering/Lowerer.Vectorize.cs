using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Auto-vectorization (RyuJIT never vectorizes loops). A fused pipeline over a numeric array whose blocks are
/// simple arithmetic/comparisons becomes a Vector&lt;T&gt; loop: map = lane arithmetic, select = lane masks,
/// sum/count/max/min = vector accumulators, map-to-array = vector stores. A scalar loop finishes the tail.
/// Floating-point sums are reassociated (like -ffast-math reductions); everything else is bit-identical.
/// </summary>
public sealed partial class Lowerer
{
    const string V = "global::System.Numerics.Vector";
    const string MM = "global::System.Runtime.InteropServices.MemoryMarshal";
    static readonly HashSet<string> VecElems = new() { "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double" };

    /// <summary>Returns the vector prologue (to run before the scalar loop) and the index the scalar loop resumes at.</summary>
    (List<MStmt> Stmts, string Index)? TryVectorize(Syntax.Expr src, MExpr srcExpr, AType elem, List<Stage> stages, string sink, LambdaExpr? sb, string res, string? count, bool prealloc)
    {
        if (src.Type.Kind != "array" || !VecElems.Contains(elem.Cs)) return null;
        if (sink is not ("sum" or "count" or "size" or "length" or "max" or "min" or "map")) return null;
        if (sink == "map" && !prealloc) return null;
        string T = elem.Cs;
        string vt = $"{V}<{T}>";
        var body = new List<MStmt>();
        string cur = Temp("v");
        string idx = Temp("vi");
        body.Add(new MDecl { Name = cur, Init = new MCode($"{V}.LoadUnsafe(ref {Base(srcExpr)}, (nuint){idx})") });
        string? mask = null;
        foreach (var s in stages)
        {
            if (s.Block.Body.Count != 1 || !s.Block.ParamSyms[0].Type.Same(elem)) return null;
            var e = ((ExprStmt)s.Block.Body[0]).Expr;
            var env = new Dictionary<LocalSym, string> { [s.Block.ParamSyms[0]] = cur };
            if (s.Kind == "map")
            {
                if (!e.Type.Same(elem)) return null;
                var v = Vec(e, env, T, false);
                if (v == null) return null;
                string next = Temp("v");
                body.Add(new MDecl { Name = next, Init = new MCode(v) });
                cur = next;
            }
            else
            {
                var m = Vec(e, env, T, true);
                if (m == null) return null;
                if (s.Kind == "reject") m = $"{V}.OnesComplement({m})";
                string mk = Temp("mk");
                body.Add(new MDecl { Name = mk, Init = new MCode(mask == null ? m : $"{V}.BitwiseAnd({mask}, {m})") });
                mask = mk;
            }
        }
        string acc = Temp("vacc");
        var init = new List<MStmt>();
        var fin = new List<MStmt>();
        switch (sink)
        {
            case "sum":
                {
                    string value = cur;
                    if (sb != null)
                    {
                        if (sb.Body.Count != 1 || !sb.ParamSyms[0].Type.Same(elem) || !sb.Result.Same(elem)) return null;
                        var v = Vec(((ExprStmt)sb.Body[0]).Expr, new() { [sb.ParamSyms[0]] = cur }, T, false);
                        if (v == null) return null;
                        value = v;
                    }
                    if (mask != null) value = $"{V}.ConditionalSelect({mask}, {value}, {vt}.Zero)";
                    init.Add(new MDecl { Name = acc, Init = new MCode($"{vt}.Zero") });
                    body.Add(new MAssign { Target = new MCode(acc), Op = "+=", Value = new MCode(value) });
                    fin.Add(new MAssign { Target = new MCode(res), Op = "+=", Value = new MCode($"{V}.Sum({acc})") });
                    break;
                }
            case "count":
            case "size":
            case "length":
                {
                    string? m = mask;
                    if (sb != null)
                    {
                        if (sb.Body.Count != 1 || !sb.ParamSyms[0].Type.Same(elem)) return null;
                        var bm = Vec(((ExprStmt)sb.Body[0]).Expr, new() { [sb.ParamSyms[0]] = cur }, T, true);
                        if (bm == null) return null;
                        m = m == null ? bm : $"{V}.BitwiseAnd({m}, {bm})";
                    }
                    if (m == null) return null;
                    init.Add(new MDecl { Name = acc, Init = new MCode($"{vt}.Zero") });
                    body.Add(new MAssign { Target = new MCode(acc), Op = "+=", Value = new MCode($"{V}.ConditionalSelect({m}, {vt}.One, {vt}.Zero)") });
                    fin.Add(new MAssign { Target = new MCode(res), Op = "+=", Value = new MCode($"(int){V}.Sum({acc})") });
                    break;
                }
            case "max":
            case "min":
                {
                    if (mask != null || sb != null) return null;
                    string fn = sink == "max" ? "Max" : "Min";
                    init.Add(new MDecl { Name = acc, Init = new MCode($"{V}.LoadUnsafe(ref {Base(srcExpr)})") });
                    // the first vector seeds the accumulator through the stage expressions on lane values
                    init.Clear();
                    init.Add(new MDecl { Name = acc, TypeCs = vt, Init = new MCode("default") });
                    init.Add(new MDecl { Name = acc + "_seeded", TypeCs = "bool", Init = new MCode("false") });
                    body.Add(new MIf
                    {
                        Cond = new MCode(acc + "_seeded"),
                        Then = { new MAssign { Target = new MCode(acc), Value = new MCode($"{V}.{fn}({acc}, {cur})") } },
                        Else = new List<MStmt> { new MAssign { Target = new MCode(acc), Value = new MCode(cur) }, new MAssign { Target = new MCode(acc + "_seeded"), Value = new MCode("true") } },
                    });
                    string k = Temp("lane");
                    fin.Add(new MIf
                    {
                        Cond = new MCode(acc + "_seeded"),
                        Then =
                    {
                        new MAssign { Target = new MCode(res), Value = new MCode($"{acc}[0]") },
                        new MFor { Var = k, Start = new MCode("1"), Cmp = "<", End = new MCode($"{vt}.Count"), Body = { new MAssign { Target = new MCode(res), Value = new MCode($"global::System.Math.{fn}({res}, {acc}[{k}])") } } },
                        new MAssign { Target = new MCode(count!), Value = new MCode("true") },
                    },
                    });
                    break;
                }
            case "map":
                if (mask != null) return null;
                if (sb != null)
                {
                    if (sb.Body.Count != 1 || !sb.ParamSyms[0].Type.Same(elem) || !sb.Result.Same(elem)) return null;
                    var mv = Vec(((ExprStmt)sb.Body[0]).Expr, new() { [sb.ParamSyms[0]] = cur }, T, false);
                    if (mv == null) return null;
                    string mapped = Temp("v");
                    body.Add(new MDecl { Name = mapped, Init = new MCode(mv) });
                    cur = mapped;
                }
                body.Add(new MExprStmt { E = new MCode($"{V}.StoreUnsafe({cur}, ref {MM}.GetArrayDataReference({res}), (nuint){idx})") });
                fin.Add(new MAssign { Target = new MCode(count!), Value = new MCode(idx) });
                break;
        }
        body.Add(new MAssign { Target = new MCode(idx), Op = "+=", Value = new MCode($"{vt}.Count") });
        MExpr n = boundHi != null ? new MCode(boundHi) : new MMember { Target = srcExpr, Name = "Length" };
        var block = new List<MStmt>();
        block.AddRange(init);
        block.Add(new MWhile { Cond = new MCode($"{idx} <= {Printer(n)} - {vt}.Count"), Body = body });
        block.AddRange(fin);
        var outp = new List<MStmt>
        {
            new MDecl { Name = idx, TypeCs = "int", Init = new MCode(boundLo ?? "0") },
            new MIf { Cond = new MCode($"{V}.IsHardwareAccelerated && {Printer(n)} - {idx} >= {vt}.Count"), Then = block },
        };
        return (outp, idx);
    }

    static string Printer(MExpr e) => Backend.Printer.E(e);

    static string Base(MExpr src) => $"{MM}.GetArrayDataReference({Printer(src)})";

    /// <summary>Translates a block expression to lane-wise vector code; null when it is not vectorizable.</summary>
    string? Vec(Syntax.Expr e, Dictionary<LocalSym, string> env, string T, bool wantMask, SoaCtx? sc = null)
    {
        string vt = $"{V}<{T}>";
        bool isFloat = T is "float" or "double";
        switch (e)
        {
            case LitExpr { Kind: "int" or "float" } l when !wantMask:
                return $"new {vt}(({T})({l.Text}))";
            case NameExpr { Res: "local" } n when !wantMask:
                {
                    var sym = (LocalSym)n.Sym!;
                    if (env.TryGetValue(sym, out var v)) return v;
                    return sym.Type.IsNumeric ? $"new {vt}(({T}){sym.CsName})" : null;
                }
            case UnaryExpr { Op: "-" } u when !wantMask:
                return Vec(u.E, env, T, false, sc) is string x ? $"-({x})" : null;
            case UnaryExpr { Op: "!" } u when wantMask:
                return Vec(u.E, env, T, true, sc) is string m ? $"{V}.OnesComplement({m})" : null;
            case BinaryExpr b:
                {
                    string? fn = b.Op switch { "<" => "LessThan", ">" => "GreaterThan", "<=" => "LessThanOrEqual", ">=" => "GreaterThanOrEqual", "==" => "Equals", "!=" => "Equals", _ => null };
                    if (fn != null && wantMask)
                    {
                        if (!b.L.Type.IsNumeric || !b.R.Type.IsNumeric) return null;
                        var l = Vec(b.L, env, T, false, sc);
                        var r = Vec(b.R, env, T, false, sc);
                        if (l == null || r == null) return null;
                        string cmp = $"{V}.{fn}({l}, {r})";
                        return b.Op == "!=" ? $"{V}.OnesComplement({cmp})" : cmp;
                    }
                    if (b.Op is "&&" or "||" && wantMask)
                    {
                        var l = Vec(b.L, env, T, true, sc);
                        var r = Vec(b.R, env, T, true, sc);
                        if (l == null || r == null) return null;
                        return $"{V}.{(b.Op == "&&" ? "BitwiseAnd" : "BitwiseOr")}({l}, {r})";
                    }
                    if (b.Op is "+" or "-" or "*" || (b.Op == "/" && isFloat))
                    {
                        if (simtStrict && b.Op == "/" && b.L.Type.IsInteger && b.R.Type.IsInteger) return null;   // integer division
                        if (simtStrict && (b.L.Type.Cs == "float" || b.R.Type.Cs == "float")) return null;
                        if (wantMask) return null;
                        var l = Vec(b.L, env, T, false, sc);
                        var r = Vec(b.R, env, T, false, sc);
                        return l != null && r != null ? $"({l} {b.Op} {r})" : null;
                    }
                    return null;
                }
            case TernaryExpr t when !wantMask:
                {
                    var c = Vec(t.C, env, T, true, sc);
                    var a = Vec(t.A, env, T, false, sc);
                    var bb = Vec(t.B, env, T, false, sc);
                    return c != null && a != null && bb != null ? $"{V}.ConditionalSelect({c}, {a}, {bb})" : null;
                }
            case CallExpr { Res: "field" } fc when !wantMask && sc != null && fc.Recv is NameExpr { Sym: LocalSym fp } && sc.Ps.Contains(fp):
                {
                    var f = (FieldSym)fc.Sym!;
                    if (sc.Regs != null) return sc.Regs.TryGetValue(f.CsName, out var reg) ? reg : null;
                    return f.Type.Cs == T ? $"{V}.LoadUnsafe(ref {MM}.GetArrayDataReference({sc.Store}.{f.CsName}), (nuint){sc.Idx})" : null;
                }
            case CallExpr c when !wantMask:
                {
                    string? fn = null;
                    List<Syntax.Expr> args;
                    if (c.Recv is ConstExpr { Ref.Name: "Math" or "MathF" })
                    {
                        fn = c.Name switch { "Abs" => "Abs", "Max" => "Max", "Min" => "Min", "Sqrt" when isFloat => "SquareRoot", _ => null };
                        args = c.Args.Select(a => a.Value!).ToList();
                    }
                    else if (c.Recv != null && c.Name == "abs" && c.Args.Count == 0)
                    {
                        fn = "Abs";
                        args = new List<Syntax.Expr> { c.Recv };
                    }
                    else return null;
                    if (fn == null || args.Count == 0) return null;
                    var vs = args.Select(a => Vec(a, env, T, false, sc)).ToList();
                    return vs.All(v => v != null) ? $"{V}.{fn}({string.Join(", ", vs)})" : null;
                }
        }
        return null;
    }
}
