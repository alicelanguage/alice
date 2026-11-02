using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Auto-vectorization (RyuJIT never vectorizes loops). A fused pipeline over a numeric array whose blocks are
/// simple arithmetic/comparisons becomes a Vector512/256/128 loop: map = lane arithmetic, select = lane masks,
/// sum/count/max/min = vector accumulators, map-to-array = vector stores. A scalar loop finishes the tail.
/// Floating-point sums are reassociated (like -ffast-math reductions); everything else is bit-identical.
/// The output is AIR vector nodes (Mir/MExpr.Vector.cs), no C# text.
/// </summary>
public sealed partial class Lowerer
{
    const string V = AirVector.Numerics;
    const string MM = "global::System.Runtime.InteropServices.MemoryMarshal";
    static readonly HashSet<string> VecElems = new() { "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double" };

    static readonly AirType IntT = new AirType.Prim(PrimKind.Int);
    static readonly AirType BoolT = new AirType.Prim(PrimKind.Bool);

    /// <summary>
    /// Returns the vector prologue (to run before the scalar loop) and the index the scalar loop resumes at.
    /// Explicit width tiers: Vector512 when accelerated and a full vector fits, else Vector256, else Vector128;
    /// `IsHardwareAccelerated` folds at JIT/AOT time (WASM keeps Vector128 or scalar).
    /// </summary>
    (List<MStmt> Stmts, string Index)? TryVectorize(Syntax.Expr src, MExpr srcExpr, AType elem, List<Stage> stages, string sink, LambdaExpr? sb, string res, string? count, bool prealloc)
    {
        if (src.Type.Kind != "array" || !VecElems.Contains(elem.Cs)) return null;
        if (sink is not ("sum" or "count" or "size" or "length" or "max" or "min" or "map")) return null;
        if (sink == "map" && !prealloc) return null;
        string idx = Temp("vi");
        MExpr N() => boundHi != null ? LRef(boundHi, IntT) : new MMember { Target = srcExpr, Name = "Length", Type = IntT };
        var saved = vecNs;
        try
        {
            MIf? chain = null, last = null;
            foreach (var ns in Tiers)
            {
                vecNs = ns;
                var tier = Tier(ns);
                if (tier == null) return null;
                if (chain == null) chain = tier;
                else last!.Else = new List<MStmt> { tier };
                last = tier;
            }
            return (new List<MStmt> { new MDecl { Name = idx, TypeCs = "int", Init = boundLo != null ? LRef(boundLo, IntT) : AirConst.Int(0) }, chain! }, idx);
        }
        finally { vecNs = saved; }

        MIf? Tier(string ns)
        {
            string T = elem.Cs;
            var lane = elem.ToAir();
            var vt = AirVector.Of(AirVector.Prim(T), ns);
            MExpr Idx() => LRef(idx, IntT);
            MExpr Vt(string name) => LRef(name, vt);
            var body = new List<MStmt>();
            string cur = Temp("v");
            body.Add(new MDecl { Name = cur, Init = new AirVectorLoad { Vec = vt, Array = srcExpr, Index = Idx(), Type = vt } });
            string? mask = null;
            foreach (var s in stages)
            {
                if (s.Block.Body.Count != 1 || !s.Block.ParamSyms[0].Type.Same(elem)) return null;
                var e = ((ExprStmt)s.Block.Body[0]).Expr;
                var env = new Dictionary<LocalSym, string> { [s.Block.ParamSyms[0]] = cur };
                if (s.Kind == "map")
                {
                    if (!e.Type.Same(elem)) return null;
                    var v = VecE(e, env, T, false);
                    if (v == null) return null;
                    string next = Temp("v");
                    body.Add(new MDecl { Name = next, Init = v });
                    cur = next;
                }
                else
                {
                    var m = VecE(e, env, T, true);
                    if (m == null) return null;
                    if (s.Kind == "reject") m = VOp(AirVecOp.OnesComplement, vt, m);
                    string mk = Temp("mk");
                    body.Add(new MDecl { Name = mk, Init = mask == null ? m : VOp(AirVecOp.BitwiseAnd, vt, Vt(mask), m) });
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
                        MExpr value = Vt(cur);
                        if (sb != null)
                        {
                            if (sb.Body.Count != 1 || !sb.ParamSyms[0].Type.Same(elem) || !sb.Result.Same(elem)) return null;
                            var v = VecE(((ExprStmt)sb.Body[0]).Expr, new() { [sb.ParamSyms[0]] = cur }, T, false);
                            if (v == null) return null;
                            value = v;
                        }
                        if (mask != null) value = VOp(AirVecOp.ConditionalSelect, vt, Vt(mask), value, VOp(AirVecOp.Zero, vt));
                        init.Add(new MDecl { Name = acc, Init = VOp(AirVecOp.Zero, vt) });
                        body.Add(new MAssign { Target = Vt(acc), Op = "+=", Value = value });
                        fin.Add(new MAssign { Target = LRef(res, lane), Op = "+=", Value = new AirVectorReduce { Op = AirVecReduce.Sum, Vec = vt, Arg = Vt(acc), Type = lane } });
                        break;
                    }
                case "count":
                case "size":
                case "length":
                    {
                        MExpr? m = mask != null ? Vt(mask) : null;
                        if (sb != null)
                        {
                            if (sb.Body.Count != 1 || !sb.ParamSyms[0].Type.Same(elem)) return null;
                            var bm = VecE(((ExprStmt)sb.Body[0]).Expr, new() { [sb.ParamSyms[0]] = cur }, T, true);
                            if (bm == null) return null;
                            m = m == null ? bm : VOp(AirVecOp.BitwiseAnd, vt, m, bm);
                        }
                        if (m == null) return null;
                        init.Add(new MDecl { Name = acc, Init = VOp(AirVecOp.Zero, vt) });
                        body.Add(new MAssign { Target = Vt(acc), Op = "+=", Value = VOp(AirVecOp.ConditionalSelect, vt, m, VOp(AirVecOp.One, vt), VOp(AirVecOp.Zero, vt)) });
                        fin.Add(new MAssign { Target = LRef(res, IntT), Op = "+=", Value = new AirVectorReduce { Op = AirVecReduce.Sum, Vec = vt, Arg = Vt(acc), CastTo = IntT, Type = IntT } });
                        break;
                    }
                case "max":
                case "min":
                    {
                        if (mask != null || sb != null) return null;
                        string fn = sink == "max" ? "Max" : "Min";
                        string seeded = acc + "_seeded";
                        // the first vector seeds the accumulator through the stage expressions on lane values
                        init.Add(new MDecl { Name = acc, TypeCs = vt.ToCs(), Init = new AirDefault(null) });
                        init.Add(new MDecl { Name = seeded, TypeCs = "bool", Init = AirConst.Bool(false) });
                        body.Add(new MIf
                        {
                            Cond = LRef(seeded, BoolT),
                            Then = { new MAssign { Target = Vt(acc), Value = VOp(sink == "max" ? AirVecOp.Max : AirVecOp.Min, vt, Vt(acc), Vt(cur)) } },
                            Else = new List<MStmt> { new MAssign { Target = Vt(acc), Value = Vt(cur) }, new MAssign { Target = LRef(seeded, BoolT), Value = AirConst.Bool(true) } },
                        });
                        string k = Temp("lane");
                        var lanewise = new MCall { Target = AirTypeRef.Named("global::System.Math"), Name = fn, Args = { LRef(res, lane), new MIndex { Target = Vt(acc), Args = { LRef(k, IntT) }, Type = lane } }, Type = lane };
                        fin.Add(new MIf
                        {
                            Cond = LRef(seeded, BoolT),
                            Then =
                            {
                                new MAssign { Target = LRef(res, lane), Value = new MIndex { Target = Vt(acc), Args = { AirConst.Int(0) }, Type = lane } },
                                new MFor { VarType = "int", Var = k, Start = AirConst.Int(1), Cmp = "<", End = VOp(AirVecOp.Count, vt), Body = { new MAssign { Target = LRef(res, lane), Value = lanewise } } },
                                new MAssign { Target = LRef(count!, BoolT), Value = AirConst.Bool(true) },
                            },
                        });
                        break;
                    }
                case "map":
                    if (mask != null) return null;
                    if (sb != null)
                    {
                        if (sb.Body.Count != 1 || !sb.ParamSyms[0].Type.Same(elem) || !sb.Result.Same(elem)) return null;
                        var mv = VecE(((ExprStmt)sb.Body[0]).Expr, new() { [sb.ParamSyms[0]] = cur }, T, false);
                        if (mv == null) return null;
                        string mapped = Temp("v");
                        body.Add(new MDecl { Name = mapped, Init = mv });
                        cur = mapped;
                    }
                    body.Add(new MExprStmt { E = new AirVectorStore { Vec = vt, Value = Vt(cur), Array = LRef(res, new AirType.Array(lane)), Index = Idx(), Type = new AirType.Prim(PrimKind.Void) } });
                    fin.Add(new MAssign { Target = LRef(count!, IntT), Value = Idx() });
                    break;
            }
            // max/min/map re-reading lanes is idempotent: finish with one overlapping final vector, no scalar tail
            bool overlap = sink is "max" or "min" or "map";
            if (overlap)
                body.Insert(0, new MIf
                {
                    Cond = Fits(AirFitsForm.Overruns, vt, Idx(), N()),
                    Then = { new MAssign { Target = Idx(), Value = new MBinary { Op = AirBinOp.Sub, L = N(), R = VOp(AirVecOp.Count, vt), Type = IntT } } },
                });
            body.Add(new MAssign { Target = Idx(), Op = "+=", Value = VOp(AirVecOp.Count, vt) });
            var block = new List<MStmt>();
            block.AddRange(init);
            block.Add(new MWhile { Cond = overlap ? new MBinary { Op = AirBinOp.Lt, L = Idx(), R = N(), Type = BoolT } : Fits(AirFitsForm.Fits, vt, Idx(), N()), Body = body });
            block.AddRange(fin);
            return new MIf { Cond = Fits(AirFitsForm.Guard, vt, Idx(), N()), Then = block };
        }
    }

    static readonly string[] Tiers = [AirVector.Api(512), AirVector.Api(256), AirVector.Api(128)];

    /// <summary>Vector API namespace used by <see cref="Vec"/>: null = Vector&lt;T&gt; (SoA/SIMT paths), else an explicit tier.</summary>
    string? vecNs;

    static string Printer(MExpr e) => Backend.Printer.E(e);

    static AirLocalRef LRef(string name, AirType? type = null) => new(name, type);

    static AirVectorOp VOp(AirVecOp op, AirType vt, params MExpr[] args) => new()
    {
        Op = op,
        Vec = vt,
        Args = args.ToList(),
        Type = op switch { AirVecOp.Count => IntT, AirVecOp.IsAccelerated or AirVecOp.EqualsAll => BoolT, _ => vt },
    };

    static AirVectorFits Fits(AirFitsForm form, AirType vt, MExpr index, MExpr end, bool inclusive = false) =>
        new() { Form = form, Vec = vt, Index = index, End = end, EndInclusive = inclusive, Type = BoolT };

    /// <summary>C# text of <see cref="VecE"/>, for the SoA lowerings that still build strings (converted in D0.7).</summary>
    string? Vec(Syntax.Expr e, Dictionary<LocalSym, string> env, string T, bool wantMask, SoaCtx? sc = null) =>
        VecE(e, env, T, wantMask, sc) is { } v ? Printer(v) : null;

    /// <summary>
    /// Translates a block expression to lane-wise vector AIR; null when it is not vectorizable. <paramref name="env"/>
    /// maps block parameters and lane locals to the vector temps that hold them.
    /// </summary>
    MExpr? VecE(Syntax.Expr e, Dictionary<LocalSym, string> env, string T, bool wantMask, SoaCtx? sc = null)
    {
        var vt = AirVector.Of(AirVector.Prim(T), vecNs ?? V);
        AirVectorOp Op(AirVecOp op, params MExpr[] args) => VOp(op, vt, args);
        bool isFloat = T is "float" or "double";
        switch (e)
        {
            case LitExpr { Kind: "int" or "float" } l when !wantMask:
                return Op(AirVecOp.Splat, new AirConst(AirConst.KindOf(l.Kind), l.Text, l.Type.ToAir()));
            case NameExpr { Res: "local" } n when !wantMask:
                {
                    var sym = (LocalSym)n.Sym!;
                    if (env.TryGetValue(sym, out var v)) return LRef(v, vt);
                    return sym.Type.IsNumeric ? Op(AirVecOp.Splat, LRef(sym.CsName, sym.Type.ToAir())) : null;
                }
            case UnaryExpr { Op: "-" } u when !wantMask:
                return VecE(u.E, env, T, false, sc) is { } x ? Op(AirVecOp.Neg, x) : null;
            case UnaryExpr { Op: "!" } u when wantMask:
                return VecE(u.E, env, T, true, sc) is { } m ? Op(AirVecOp.OnesComplement, m) : null;
            case BinaryExpr b:
                {
                    AirVecOp? fn = b.Op switch
                    {
                        "<" => AirVecOp.LessThan,
                        ">" => AirVecOp.GreaterThan,
                        "<=" => AirVecOp.LessThanOrEqual,
                        ">=" => AirVecOp.GreaterThanOrEqual,
                        "==" or "!=" => AirVecOp.Equals,
                        _ => null,
                    };
                    if (fn != null && wantMask)
                    {
                        if (!b.L.Type.IsNumeric || !b.R.Type.IsNumeric) return null;
                        var l = VecE(b.L, env, T, false, sc);
                        var r = VecE(b.R, env, T, false, sc);
                        if (l == null || r == null) return null;
                        var cmp = Op(fn.Value, l, r);
                        return b.Op == "!=" ? Op(AirVecOp.OnesComplement, cmp) : cmp;
                    }
                    if (b.Op is "&&" or "||" && wantMask)
                    {
                        var l = VecE(b.L, env, T, true, sc);
                        var r = VecE(b.R, env, T, true, sc);
                        if (l == null || r == null) return null;
                        return Op(b.Op == "&&" ? AirVecOp.BitwiseAnd : AirVecOp.BitwiseOr, l, r);
                    }
                    if (b.Op is "+" or "-" or "*" || (b.Op == "/" && isFloat))
                    {
                        if (simtStrict && b.Op == "/" && b.L.Type.IsInteger && b.R.Type.IsInteger) return null;   // integer division
                        if (simtStrict && (b.L.Type.Cs == "float" || b.R.Type.Cs == "float")) return null;
                        if (wantMask) return null;
                        var l = VecE(b.L, env, T, false, sc);
                        var r = VecE(b.R, env, T, false, sc);
                        return l != null && r != null ? Op(ArithOp(b.Op[0]), l, r) : null;
                    }
                    return null;
                }
            case TernaryExpr t when !wantMask:
                {
                    var c = VecE(t.C, env, T, true, sc);
                    var a = VecE(t.A, env, T, false, sc);
                    var bb = VecE(t.B, env, T, false, sc);
                    return c != null && a != null && bb != null ? Op(AirVecOp.ConditionalSelect, c, a, bb) : null;
                }
            case CallExpr { Res: "field" } fc when !wantMask && sc != null && fc.Recv is NameExpr { Sym: LocalSym fp } && sc.Ps.Contains(fp):
                {
                    var f = (FieldSym)fc.Sym!;
                    if (sc.Regs != null) return sc.Regs.TryGetValue(f.CsName, out var reg) ? LRef(reg, vt) : null;
                    if (f.Type.Cs != T) return null;
                    var column = new MMember { Target = sc.StoreLeaf, Name = f.CsName, Type = new AirType.Array(f.Type.ToAir()) };
                    return new AirVectorLoad { Vec = vt, Array = column, Index = LRef(sc.Idx, IntT), Type = vt };
                }
            case CallExpr c when !wantMask:
                {
                    AirVecOp? fn = null;
                    List<Syntax.Expr> args;
                    if (IsClrMath(c))
                    {
                        fn = c.Name switch { "Abs" => AirVecOp.Abs, "Max" => AirVecOp.Max, "Min" => AirVecOp.Min, "Sqrt" when isFloat => AirVecOp.Sqrt, _ => null };
                        args = c.Args.Select(a => a.Value!).ToList();
                    }
                    else if (c.Recv != null && c.Name == "abs" && c.Args.Count == 0)
                    {
                        fn = AirVecOp.Abs;
                        args = new List<Syntax.Expr> { c.Recv };
                    }
                    else return null;
                    if (fn == null || args.Count == 0) return null;
                    var vs = args.Select(a => VecE(a, env, T, false, sc)).ToList();
                    return vs.All(v => v != null) ? Op(fn.Value, vs.Select(v => v!).ToArray()) : null;
                }
        }
        return null;
    }

    static AirVecOp ArithOp(char op) => op switch { '+' => AirVecOp.Add, '-' => AirVecOp.Sub, '*' => AirVecOp.Mul, _ => AirVecOp.Div };
}
