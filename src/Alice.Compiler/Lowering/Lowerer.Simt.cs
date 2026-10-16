using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// SIMT vectorization (GPU-style execution on CPU SIMD lanes). A counted loop whose iterations contain a
/// data-dependent `while` runs Vector&lt;double&gt;.Count iterations at once: every lane keeps its own locals,
/// the while runs until all lanes are done, and updates are masked so finished lanes keep their values.
/// Lanes do exactly the scalar arithmetic in the same order, so results are identical. Integer locals are
/// carried in double lanes (exact for these magnitudes); integer division and Float32 keep the scalar loop.
/// </summary>
public sealed partial class Lowerer
{
    bool simtStrict;

    MStmt? TrySimtFor(ForStmt f)
    {
        if (f.Iter is not RangeExpr r || f.Locals.Count != 1 || f.Locals[0].Type.Cs != "int") return null;
        if (!f.Body.Any(s => s is WhileStmt) || noHoist > 0) return null;
        const string T = "double";
        string vt = $"{V}<double>";
        var env = new Dictionary<LocalSym, string>();
        var accs = new Dictionary<LocalSym, string>();
        string px = Temp("px");
        string vpx = Temp("vpx");
        env[f.Locals[0]] = vpx;
        var vbody = new List<MStmt> { new MDecl { Name = vpx, Init = new MCode($"new {vt}((double){px}) + {vt}.Indices") } };
        simtStrict = true;
        bool ok = SimtStmts(f.Body, env, vbody, null, accs, T);
        simtStrict = false;
        if (!ok) return null;

        var start = Expr(r.A);
        var end = Expr(r.B);
        string endExcl = r.Excl ? Backend.Printer.E(end) : $"({Backend.Printer.E(end)}) + 1";
        vbody.Add(new MAssign { Target = new MCode(px), Op = "+=", Value = new MCode($"{vt}.Count") });
        var vec = new List<MStmt>();
        foreach (var (_, va) in accs) vec.Add(new MDecl { Name = va, Init = new MCode($"{vt}.Zero") });
        vec.Add(new MWhile { Cond = new MCode($"{px} <= {endExcl} - {vt}.Count"), Body = vbody });
        foreach (var (acc, va) in accs)
            vec.Add(new MAssign { Target = new MCode(acc.CsName), Op = "+=", Value = new MCode($"({TypeCs(acc.Type)}){V}.Sum({va})") });

        // scalar tail: the ordinary body for the remaining iterations
        ctx.Add("loop");
        var tail = new MFor { Var = f.Locals[0].CsName, Start = new MCode(px), Cmp = r.Excl ? "<" : "<=", End = end, Body = Stmts(f.Body, Tail.None, null) };
        ctx.RemoveAt(ctx.Count - 1);
        return new MBlock
        {
            Body =
            {
                new MDecl { Name = px, TypeCs = "int", Init = start },
                new MIf { Cond = new MCode($"{V}.IsHardwareAccelerated"), Then = vec },
                tail,
            },
        };
    }

    static bool LaneType(AType t) => t.Cs is "int" or "long" or "double" or "short" or "sbyte" or "byte";

    bool SimtStmts(List<Stmt> body, Dictionary<LocalSym, string> env, List<MStmt> o, string? active, Dictionary<LocalSym, string> accs, string T)
    {
        string vt = $"{V}<{T}>";
        foreach (var s in body)
        {
            switch (s)
            {
                case Assign { Declares: { } d } a:
                    {
                        if (!LaneType(d.Type) || Vec(a.Value, env, T, false) is not string v) return false;
                        string name = Temp("l");
                        o.Add(new MDecl { Name = name, Init = new MCode(v) });
                        env[d] = name;
                        break;
                    }
                case Assign { Target: NameExpr { Sym: LocalSym l } } a when env.TryGetValue(l, out var cur):
                    {
                        if (Vec(a.Value, env, T, false) is not string v) return false;
                        if (a.Op != "=")
                        {
                            if (a.Op is not ("+=" or "-=" or "*=") && !(a.Op == "/=" && l.Type.Cs == "double")) return false;
                            v = $"({cur} {a.Op[0]} {v})";
                        }
                        if (active != null) v = $"{V}.ConditionalSelect({active}, {v}, {cur})";
                        o.Add(new MAssign { Target = new MCode(cur), Value = new MCode(v) });
                        break;
                    }
                case Assign { Target: NameExpr { Sym: LocalSym acc }, Op: "+=" or "-=" } a when active == null && acc.Type.IsNumeric && LaneType(acc.Type):
                    {
                        if (Vec(a.Value, env, T, false) is not string v) return false;
                        string va = accs.TryGetValue(acc, out var x) ? x : accs[acc] = Temp("vacc");
                        o.Add(new MAssign { Target = new MCode(va), Op = a.Op, Value = new MCode(v) });
                        break;
                    }
                case IfStmt { Conds.Count: 1, Else: null } i when active == null && i.Bodies[0] is [Assign { Target: NameExpr { Sym: LocalSym acc }, Op: "+=" or "-=" } a] && !env.ContainsKey(acc) && LaneType(acc.Type):
                    {
                        if (Vec(i.Conds[0], env, T, true) is not string m || Vec(a.Value, env, T, false) is not string v) return false;
                        string va = accs.TryGetValue(acc, out var x) ? x : accs[acc] = Temp("vacc");
                        o.Add(new MAssign { Target = new MCode(va), Op = a.Op, Value = new MCode($"{V}.ConditionalSelect({m}, {v}, {vt}.Zero)") });
                        break;
                    }
                case WhileStmt w when active == null:
                    {
                        if (Vec(w.Cond, env, T, true) is not string m) return false;
                        string act = Temp("act");
                        o.Add(new MDecl { Name = act, Init = new MCode(m) });
                        var inner = new List<MStmt>();
                        if (!SimtStmts(w.Body, env, inner, act, accs, T)) return false;
                        if (Vec(w.Cond, env, T, true) is not string m2) return false;
                        inner.Add(new MAssign { Target = new MCode(act), Value = new MCode($"{V}.BitwiseAnd({act}, {m2})") });
                        o.Add(new MWhile { Cond = new MCode($"!{V}.EqualsAll({V}.AsVectorInt64({act}), {V}<long>.Zero)"), Body = inner });
                        break;
                    }
                default:
                    return false;
            }
        }
        return true;
    }
}
