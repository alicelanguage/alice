using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>Block parameter bound to row `Idx` of an SoA store: `p.f` lowers to `Store.f[Idx]` (no struct gather).</summary>
public sealed record SoaCtx(HashSet<LocalSym> Ps, string Store, string Idx, Dictionary<string, string>? Regs = null)
{
    public SoaCtx(LocalSym p, string store, string idx) : this(new HashSet<LocalSym> { p }, store, idx) { }

    /// <summary>The leaf naming the store (a local by default; the simple leaf the source lowered to when reused).
    /// Shared by every use: leaves are never mutated.</summary>
    public MCode StoreLeaf { get; init; } = new AirLocalRef(Store);

    public AirLocalRef IdxRef() => new(Idx, new AirType.Prim(PrimKind.Int));
}

/// <summary>
/// Loops over `SoA(T)` stores. `ps.each { |p| p.x += p.v * dt }` touches only the x and v columns; when every
/// statement is a numeric column update the loop is vectorized (Vector&lt;T&gt; load/compute/store per column).
/// </summary>
public sealed partial class Lowerer
{
    SoaCtx? soa;

    MExpr? SoaPipeline(CallExpr sink, string sinkName, Syntax.Expr src, List<Stage> stages)
    {
        var sb = sink.Block;
        if (stages.Count > 0 || sb == null || sb.ParamSyms.Count != 1) return null;
        if (sinkName is not ("each" or "sum" or "count")) return null;
        if (sinkName != "each" && (!Pure(sb) || !HasValueTail(sb))) return null;
        var p = sb.ParamSyms[0];
        var storeExpr = Expr(src);
        string store;
        MCode storeLeaf;
        if (storeExpr is MCode sc && IsSimple(sc.Text)) (store, storeLeaf) = (sc.Text, sc);
        else
        {
            store = Temp("soa");
            pre.Add(new MDecl { Name = store, Init = storeExpr });
            storeLeaf = new AirLocalRef(store, src.Type.ToAir());
        }
        string res = Temp("r");
        AType? val = sinkName == "sum" ? sb.Result : sinkName == "count" ? AType.Int : null;
        var resT = val?.ToAir();
        if (val != null) pre.Add(new MDecl { Name = res, TypeCs = TypeCs(val), Init = new AirDefault(CsAir(val)) });

        string vi = Temp("vi");
        pre.Add(new MDecl { Name = vi, TypeCs = "int", Init = boundLo != null ? LRef(boundLo, IntT) : AirConst.Int(0) });
        if (VectorizeSoa(sinkName, sb, p, store, storeLeaf, vi, res, resT) is { } vec) pre.Add(vec);

        string idx = Temp("i");
        var saved = soa;
        soa = new SoaCtx(p, store, idx) { StoreLeaf = storeLeaf };
        var body = new List<MStmt>();
        ctx.Add("loop");
        if (sinkName == "each") body.AddRange(Stmts(sb.Body, Tail.None, null));
        else
        {
            var (stmts, v) = BlockValue(sb);
            body.AddRange(stmts);
            if (sinkName == "sum") body.Add(new MAssign { Target = LRef(res, resT), Op = "+=", Value = v });
            else body.Add(new MIf { Cond = v, Then = { new MExprStmt { E = new MUnary { Op = AirUnOp.Inc, E = LRef(res, resT), Postfix = true } } } });
        }
        ctx.RemoveAt(ctx.Count - 1);
        soa = saved;
        pre.Add(new MFor { VarType = "int", Var = idx, Start = LRef(vi, IntT), Cmp = "<", End = boundHi != null ? LRef(boundHi, IntT) : StoreCount(storeLeaf), Body = body });
        return sinkName == "each" ? MCode.Empty : LRef(res, resT);
    }

    static MMember StoreCount(MCode store) => new() { Target = store, Name = "Count", Type = IntT };

    /// <summary>The AIR type spelled as <see cref="TypeCs(AType)"/> spells <paramref name="t"/> (`dynamic` for unknown).</summary>
    static AirType CsAir(AType t)
    {
        var air = t.ToAir();
        return air.ToCs() == TypeCs(t) ? air : AirTypeRef.Named(TypeCs(t)).Ref;
    }

    /// <summary>Vector prologue over the columns; leaves `vi` at the first row the scalar loop must still handle.</summary>
    MStmt? VectorizeSoa(string sinkName, LambdaExpr sb, LocalSym p, string store, MCode storeLeaf, string vi, string res, AirType? resT)
    {
        var elem = p.Type.Sym!;
        var types = elem.FieldOrder.Select(f => elem.Fields[f].Type.Cs).Distinct().ToList();
        if (types.Count != 1 || !VecElems.Contains(types[0])) return null;
        string T = types[0];
        var vt = AirVector.Of(AirVector.Prim(T), V);
        var sc = new SoaCtx(p, store, vi) { StoreLeaf = storeLeaf };
        var body = new List<MStmt>();
        var init = new List<MStmt>();
        var fin = new List<MStmt>();
        string acc = Temp("vacc");
        var env = new Dictionary<LocalSym, string>();
        if (sinkName == "each")
        {
            foreach (var s in sb.Body)
            {
                if (s is not Assign { Target: CallExpr { Res: "field" } tc } a || tc.Recv is not NameExpr { Sym: LocalSym rp } || !sc.Ps.Contains(rp)) return null;
                if (a.Op is not ("=" or "+=" or "-=" or "*=") && !(a.Op == "/=" && T is "float" or "double")) return null;
                var v = VecE(a.Value, env, T, false, sc);
                if (v == null) return null;
                var f = (FieldSym)tc.Sym!;
                MMember Col() => new() { Target = storeLeaf, Name = f.CsName, Type = new AirType.Array(f.Type.ToAir()) };
                var cur = new AirVectorLoad { Vec = vt, Array = Col(), Index = LRef(vi, IntT), Type = vt };
                var nv = a.Op == "=" ? v : VOp(a.Op[0] switch { '+' => AirVecOp.Add, '-' => AirVecOp.Sub, '*' => AirVecOp.Mul, _ => AirVecOp.Div }, vt, cur, v);
                body.Add(new MExprStmt { E = new AirVectorStore { Vec = vt, Value = nv, Array = Col(), Index = LRef(vi, IntT), Type = AirLeaf.Void } });
            }
        }
        else
        {
            if (sb.Body.Count != 1) return null;
            var e = ((ExprStmt)sb.Body[0]).Expr;
            if (sinkName == "sum")
            {
                if (sb.Result.Cs != T) return null;
                var v = VecE(e, env, T, false, sc);
                if (v == null) return null;
                init.Add(new MDecl { Name = acc, Init = VOp(AirVecOp.Zero, vt) });
                body.Add(new MAssign { Target = LRef(acc, vt), Op = "+=", Value = v });
                fin.Add(new MAssign { Target = LRef(res, resT), Op = "+=", Value = new AirVectorReduce { Op = AirVecReduce.Sum, Vec = vt, Arg = LRef(acc, vt), Type = resT } });
            }
            else
            {
                var m = VecE(e, env, T, true, sc);
                if (m == null) return null;
                init.Add(new MDecl { Name = acc, Init = VOp(AirVecOp.Zero, vt) });
                body.Add(new MAssign { Target = LRef(acc, vt), Op = "+=", Value = VOp(AirVecOp.ConditionalSelect, vt, m, VOp(AirVecOp.One, vt), VOp(AirVecOp.Zero, vt)) });
                fin.Add(new MAssign { Target = LRef(res, resT), Op = "+=", Value = new AirVectorReduce { Op = AirVecReduce.Sum, Vec = vt, Arg = LRef(acc, vt), CastTo = IntT, Type = IntT } });
            }
        }
        body.Add(new MAssign { Target = LRef(vi, IntT), Op = "+=", Value = VOp(AirVecOp.Count, vt) });
        var end = boundHi != null ? (MExpr)LRef(boundHi, IntT) : StoreCount(storeLeaf);
        var block = new List<MStmt>(init) { new MWhile { Cond = Fits(AirFitsForm.Fits, vt, LRef(vi, IntT), end), Body = body } };
        block.AddRange(fin);
        return new MIf { Cond = VOp(AirVecOp.IsAccelerated, vt), Then = block };
    }
}
