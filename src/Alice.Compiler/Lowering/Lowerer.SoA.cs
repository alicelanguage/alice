using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>Block parameter bound to row `Idx` of an SoA store: `p.f` lowers to `Store.f[Idx]` (no struct gather).</summary>
public sealed record SoaCtx(HashSet<LocalSym> Ps, string Store, string Idx, Dictionary<string, string>? Regs = null)
{
    public SoaCtx(LocalSym p, string store, string idx) : this(new HashSet<LocalSym> { p }, store, idx) { }
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
        if (storeExpr is MCode sc && IsSimple(sc.Text)) store = sc.Text;
        else
        {
            store = Temp("soa");
            pre.Add(new MDecl { Name = store, Init = storeExpr });
        }
        string res = Temp("r");
        AType? val = sinkName == "sum" ? sb.Result : sinkName == "count" ? AType.Int : null;
        if (val != null) pre.Add(new MDecl { Name = res, TypeCs = TypeCs(val), Init = new MCode($"default({TypeCs(val)})") });

        string vi = Temp("vi");
        pre.Add(new MDecl { Name = vi, TypeCs = "int", Init = new MCode(boundLo ?? "0") });
        if (VectorizeSoa(sinkName, sb, p, store, vi, res) is { } vec) pre.Add(vec);

        string idx = Temp("i");
        var saved = soa;
        soa = new SoaCtx(p, store, idx);
        var body = new List<MStmt>();
        ctx.Add("loop");
        if (sinkName == "each") body.AddRange(Stmts(sb.Body, Tail.None, null));
        else
        {
            var (stmts, v) = BlockValue(sb);
            body.AddRange(stmts);
            if (sinkName == "sum") body.Add(new MAssign { Target = new MCode(res), Op = "+=", Value = v });
            else body.Add(new MIf { Cond = v, Then = { new MExprStmt { E = new MUnary { Op = "++", E = new MCode(res), Postfix = true } } } });
        }
        ctx.RemoveAt(ctx.Count - 1);
        soa = saved;
        pre.Add(new MFor { Var = idx, Start = new MCode(vi), Cmp = "<", End = boundHi != null ? new MCode(boundHi) : new MMember { Target = new MCode(store), Name = "Count" }, Body = body });
        return sinkName == "each" ? MCode.Empty : new MCode(res);
    }

    /// <summary>Vector prologue over the columns; leaves `vi` at the first row the scalar loop must still handle.</summary>
    MStmt? VectorizeSoa(string sinkName, LambdaExpr sb, LocalSym p, string store, string vi, string res)
    {
        var elem = p.Type.Sym!;
        var types = elem.FieldOrder.Select(f => elem.Fields[f].Type.Cs).Distinct().ToList();
        if (types.Count != 1 || !VecElems.Contains(types[0])) return null;
        string T = types[0];
        string vt = $"{V}<{T}>";
        var sc = new SoaCtx(p, store, vi);
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
                var v = Vec(a.Value, env, T, false, sc);
                if (v == null) return null;
                string col = $"{store}.{((FieldSym)tc.Sym!).CsName}";
                string cur = $"{V}.LoadUnsafe(ref {MM}.GetArrayDataReference({col}), (nuint){vi})";
                string nv = a.Op == "=" ? v : $"({cur} {a.Op[0]} {v})";
                body.Add(new MExprStmt { E = new MCode($"{V}.StoreUnsafe({nv}, ref {MM}.GetArrayDataReference({col}), (nuint){vi})") });
            }
        }
        else
        {
            if (sb.Body.Count != 1) return null;
            var e = ((ExprStmt)sb.Body[0]).Expr;
            if (sinkName == "sum")
            {
                if (sb.Result.Cs != T) return null;
                var v = Vec(e, env, T, false, sc);
                if (v == null) return null;
                init.Add(new MDecl { Name = acc, Init = new MCode($"{vt}.Zero") });
                body.Add(new MAssign { Target = new MCode(acc), Op = "+=", Value = new MCode(v) });
                fin.Add(new MAssign { Target = new MCode(res), Op = "+=", Value = new MCode($"{V}.Sum({acc})") });
            }
            else
            {
                var m = Vec(e, env, T, true, sc);
                if (m == null) return null;
                init.Add(new MDecl { Name = acc, Init = new MCode($"{vt}.Zero") });
                body.Add(new MAssign { Target = new MCode(acc), Op = "+=", Value = new MCode($"{V}.ConditionalSelect({m}, {vt}.One, {vt}.Zero)") });
                fin.Add(new MAssign { Target = new MCode(res), Op = "+=", Value = new MCode($"(int){V}.Sum({acc})") });
            }
        }
        body.Add(new MAssign { Target = new MCode(vi), Op = "+=", Value = new MCode($"{vt}.Count") });
        var block = new List<MStmt>(init) { new MWhile { Cond = new MCode($"{vi} <= {boundHi ?? store + ".Count"} - {vt}.Count"), Body = body } };
        block.AddRange(fin);
        return new MIf { Cond = new MCode($"{V}.IsHardwareAccelerated"), Then = block };
    }
}
