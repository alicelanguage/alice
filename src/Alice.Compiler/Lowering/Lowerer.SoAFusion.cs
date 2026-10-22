using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>Consecutive row-local `each` passes over one SoA store (optionally inside a counted loop) fused into one sweep.</summary>
internal sealed class FusedSoaStmt : Stmt
{
    public LocalSym Store = null!;
    public List<LambdaExpr> Blocks = new();
    public ForStmt? Outer;          // interchanged loop: runs per element, inside the sweep
    public CallExpr? Reduce;        // `store.sum { |p| ... }` from the next statement, computed during the sweep
    public string ResultTemp = "";
}

/// <summary>
/// Pass fusion + loop interchange for SoA stores. Every block writes only its own row and reads only its own row
/// and loop-invariant locals, so rows are independent: `for s { each A; each B }` == `each { for s { A; B } }`.
/// The fused sweep loads each touched column into a vector register once, runs all passes/steps on registers,
/// and stores once (1 memory sweep instead of passes x steps); large stores also run in parallel.
/// </summary>
public sealed partial class Lowerer
{
    List<Stmt> FuseSoa(List<Stmt> body)
    {
        var outp = new List<Stmt>();
        for (int i = 0; i < body.Count; i++)
        {
            int before = outp.Count;
            i = FuseAt(body, i, outp);
            if (outp.Count > before && outp[^1] is FusedSoaStmt fs && i + 1 < body.Count) AttachReduce(fs, body[i + 1]);
        }
        return outp;
    }

    /// <summary>A following `store.sum { row-pure }` (anywhere in the next statement) is folded into the sweep.</summary>
    void AttachReduce(FusedSoaStmt fs, Stmt next)
    {
        var roots = next switch { ExprStmt es => new[] { es.Expr }, Assign a => new[] { a.Value }, ReturnStmt { Value: { } v } => new[] { v }, LocalDecl { Value: { } dv } => new[] { dv }, _ => Array.Empty<Syntax.Expr>() };
        foreach (var e in roots.SelectMany(WalkExpr))
            if (e is CallExpr { Name: "sum", Res: "ext", Recv: NameExpr { Sym: LocalSym s }, Block: { ParamSyms.Count: 1 } b } c && s == fs.Store
                && PureBlock(b, null) && HasValueTail(b) && b.Body.Count == 1 && b.Result.IsNumeric)
            {
                fs.Reduce = c;
                fs.ResultTemp = Temp("fsum");
                c.Res = "fused";
                c.Sym = fs.ResultTemp;
                return;
            }
    }

    static bool HasReduceFor(LocalSym store, Stmt next)
    {
        var roots = next switch { ExprStmt es => new[] { es.Expr }, Assign a => new[] { a.Value }, ReturnStmt { Value: { } v } => new[] { v }, LocalDecl { Value: { } dv } => new[] { dv }, _ => Array.Empty<Syntax.Expr>() };
        return roots.SelectMany(WalkExpr).Any(e => e is CallExpr { Name: "sum", Res: "ext", Recv: NameExpr { Sym: LocalSym s }, Block: { ParamSyms.Count: 1 } b } && s == store && PureBlock(b, null) && b.Body.Count == 1 && b.Result.IsNumeric);
    }

    int FuseAt(List<Stmt> body, int i, List<Stmt> outp)
    {
        {
            if (body[i] is ForStmt f && f.Iter is RangeExpr && f.Locals.Count == 1 && SoaGroup(f.Body, 0, out var st, out var blocks) == f.Body.Count && blocks.Count > 0
                && !blocks.Any(b => WalkReads(b.Body).Any(e => e is NameExpr { Sym: LocalSym l } && l == st)))
            {
                outp.Add(new FusedSoaStmt { Store = st!, Blocks = blocks, Outer = f, Line = f.Line });
                return i;
            }
            int n = SoaGroup(body, i, out var st2, out var bs);
            if (n >= 2 || (n == 1 && i + 1 < body.Count && HasReduceFor(st2!, body[i + 1])))
            {
                outp.Add(new FusedSoaStmt { Store = st2!, Blocks = bs, Line = body[i].Line });
                return i + n - 1;
            }
            outp.Add(body[i]);
            return i;
        }
    }

    /// <summary>Length of the run of fusable `store.each { |p| row-local }` statements starting at `from`.</summary>
    static int SoaGroup(List<Stmt> body, int from, out LocalSym? store, out List<LambdaExpr> blocks)
    {
        store = null;
        blocks = new List<LambdaExpr>();
        int k = from;
        for (; k < body.Count; k++)
        {
            if (body[k] is not ExprStmt { Expr: CallExpr { Name: "each", Res: "ext", Recv: NameExpr { Sym: LocalSym s, Type.Sym.SoaOf: not null }, Block: { ParamSyms.Count: 1 } b } }) break;
            if (store != null && s != store) break;
            if (!PureBlock(b, b.ParamSyms[0]) || WalkReads(b.Body).Any(e => e is NameExpr { Sym: LocalSym ls } && ls == s)) break;
            store = s;
            blocks.Add(b);
        }
        return k - from;
    }

    void LowerFusedSoa(List<MStmt> o, FusedSoaStmt fs)
    {
        string store = fs.Store.CsName;
        string? accT = fs.Reduce != null ? TypeCs(fs.Reduce.Block!.Result) : null;
        if (accT != null) o.Add(new MDecl { Name = fs.ResultTemp, TypeCs = accT, Init = new MCode("default") });
        var ps = new HashSet<LocalSym>(fs.Blocks.Select(b => b.ParamSyms[0]));
        string n = store + ".Count";
        var serial = new List<MStmt>();
        var savedPre = pre;
        pre = serial;
        FusedKernel(serial, fs, ps, store, null, null, fs.ResultTemp);
        string lo = Temp("lo"), hi = Temp("hi");
        var kernel = new List<MStmt>();
        pre = kernel;
        bool savedPar = inParallelGen;
        inParallelGen = true;
        string part = Temp("part");
        if (accT != null) kernel.Add(new MDecl { Name = part, TypeCs = accT, Init = new MCode("default") });
        FusedKernel(kernel, fs, ps, store, lo, hi, part);
        if (accT != null) kernel.Add(new MReturn { Value = new MCode(part) });
        inParallelGen = savedPar;
        pre = savedPre;

        var captured = new List<LocalSym> { fs.Store };
        var inner = new HashSet<LocalSym>(ps);
        if (fs.Outer != null) inner.UnionWith(fs.Outer.Locals);
        foreach (var b in fs.Blocks)
            foreach (var s in WalkStmts(b.Body))
                if (s is Assign { Declares: { } d }) inner.Add(d);
        var reads = fs.Blocks.SelectMany(b => WalkReads(b.Body));
        if (fs.Outer?.Iter is RangeExpr orr) reads = reads.Concat(WalkExpr(orr.A)).Concat(WalkExpr(orr.B));
        if (fs.Reduce != null)
        {
            inner.UnionWith(fs.Reduce.Block!.ParamSyms);
            reads = reads.Concat(WalkReads(fs.Reduce.Block.Body));
        }
        foreach (var e in reads)
            if (e is NameExpr { Res: "local", Sym: LocalSym l } && !inner.Contains(l) && !captured.Contains(l)) captured.Add(l);
        if (captured.Any(l => !l.Type.IsKnown) || inParallelGen || ctx.Contains("lambda"))
        {
            o.Add(new MBlock { Body = serial });
            return;
        }
        string helper = $"__fused_{(Method?.CsName ?? "main").TrimStart('@')}_{++helperN}";
        var hp = new Backend.Printer("", 1);
        string sig = string.Join(", ", captured.Select(l => $"{LocalTypeCs(l)} {l.CsName}"));
        string callText = $"{helper}({string.Join(", ", captured.Select(l => l.CsName))})";
        MStmt call;
        if (accT != null)
        {
            hp.Root(new MReturn { Value = new MCall { Target = new MCode("global::Alice.Runtime.Par"), Name = $"Reduce<{accT}>", Args = { new MCode(n), new MLambda { Params = { lo, hi }, Body = kernel }, new MCode("static (a, b) => a + b"), new MCode("4096") } } });
            Extra.Add($"private static {accT} {helper}({sig})\n{{\n{hp}}}\n");
            call = new MAssign { Target = new MCode(fs.ResultTemp), Value = new MCode(callText) };
        }
        else
        {
            hp.Root(new MExprStmt { E = new MCall { Target = new MCode("global::Alice.Runtime.Par"), Name = "Range", Args = { new MCode(n), new MLambda { Params = { lo, hi }, Body = kernel }, new MCode("4096") } } });
            Extra.Add($"private static void {helper}({sig})\n{{\n{hp}}}\n");
            call = new MExprStmt { E = new MCode(callText) };
        }
        o.Add(new MIf { Cond = new MCode($"{n} >= {Threshold} && global::Alice.Runtime.Par.Workers > 1"), Then = { call }, Else = new List<MStmt> { new MBlock { Body = serial } } });
    }

    /// <summary>Vector sweep with register-promoted columns, then a scalar tail; rows [lo, hi) or the whole store.</summary>
    void FusedKernel(List<MStmt> o, FusedSoaStmt fs, HashSet<LocalSym> ps, string store, string? lo, string? hi, string acc)
    {
        string end = hi ?? store + ".Count";
        string vi = Temp("vi");
        o.Add(new MDecl { Name = vi, TypeCs = "int", Init = new MCode(lo ?? "0") });
        var elem = fs.Blocks[0].ParamSyms[0].Type.Sym!;
        var types = elem.FieldOrder.Select(f => elem.Fields[f].Type.Cs).Distinct().ToList();
        if (types.Count == 1 && VecElems.Contains(types[0]) && VectorFused(fs, ps, store, vi, end, types[0], acc) is { } vec) o.Add(vec);

        // scalar tail (and the whole loop when lanes do not apply)
        string idx = Temp("i");
        var saved = soa;
        soa = new SoaCtx(ps, store, idx);
        ctx.Add("loop");
        var body = new List<MStmt>();
        var passes = new List<MStmt>();
        foreach (var b in fs.Blocks) passes.AddRange(Stmts(b.Body, Tail.None, null));
        if (fs.Outer != null && fs.Outer.Iter is RangeExpr r)
            body.Add(new MFor { Var = fs.Outer.Locals[0].CsName, Start = Expr(r.A), Cmp = r.Excl ? "<" : "<=", End = Expr(r.B), Body = passes });
        else body.AddRange(passes);
        if (fs.Reduce != null)
        {
            var rb = fs.Reduce.Block!;
            soa = new SoaCtx(new HashSet<LocalSym>(ps) { rb.ParamSyms[0] }, store, idx);
            var (pre2, v) = BlockValue(rb);
            body.AddRange(pre2);
            body.Add(new MAssign { Target = new MCode(acc), Op = "+=", Value = v });
        }
        ctx.RemoveAt(ctx.Count - 1);
        soa = saved;
        o.Add(new MFor { VarType = "int", Var = idx, Start = new MCode(vi), Cmp = "<", End = new MCode(end), Body = body });
    }

    MStmt? VectorFused(FusedSoaStmt fs, HashSet<LocalSym> ps, string store, string vi, string end, string T, string acc)
    {
        string vt = $"{V}<{T}>";
        var elem = fs.Blocks[0].ParamSyms[0].Type.Sym!;
        var regs = elem.FieldOrder.ToDictionary(f => elem.Fields[f].CsName, f => Temp("reg"));
        var sc = new SoaCtx(ps, store, vi, regs);
        var env = new Dictionary<LocalSym, string>();
        var passes = new List<MStmt>();
        var written = new HashSet<string>();
        foreach (var b in fs.Blocks)
            foreach (var s in b.Body)
            {
                if (s is not Assign { Target: CallExpr { Res: "field" } tc } a || tc.Recv is not NameExpr { Sym: LocalSym rp } || !ps.Contains(rp)) return null;
                if (a.Op is not ("=" or "+=" or "-=" or "*=") && !(a.Op == "/=" && T is "float" or "double")) return null;
                var v = Vec(a.Value, env, T, false, sc);
                if (v == null) return null;
                string col = ((FieldSym)tc.Sym!).CsName;
                passes.Add(new MAssign { Target = new MCode(regs[col]), Op = a.Op, Value = new MCode(v) });
                written.Add(col);
            }
        string? reduceV = null;
        string vacc = Temp("vacc");
        if (fs.Reduce != null)
        {
            var rb = fs.Reduce.Block!;
            if (rb.Result.Cs != T) return null;
            reduceV = Vec(((ExprStmt)rb.Body[0]).Expr, env, T, false, sc with { Ps = new HashSet<LocalSym>(ps) { rb.ParamSyms[0] } });
            if (reduceV == null) return null;
        }
        var body = new List<MStmt>();
        var used = regs.Keys.Where(c => passes.Any(p => Backend.Printer.E(((MAssign)p).Value).Contains(regs[c])) || written.Contains(c) || (reduceV?.Contains(regs[c]) ?? false)).ToList();
        foreach (var c in used)
            body.Add(new MDecl { Name = regs[c], Init = new MCode($"{V}.LoadUnsafe(ref {MM}.GetArrayDataReference({store}.{c}), (nuint){vi})") });
        if (fs.Outer != null && fs.Outer.Iter is RangeExpr r)
        {
            if (!PureExpr(r.A, new()) || !PureExpr(r.B, new())) return null;
            body.Add(new MFor { Var = fs.Outer.Locals[0].CsName, Start = Expr(r.A), Cmp = r.Excl ? "<" : "<=", End = Expr(r.B), Body = passes });
        }
        else body.AddRange(passes);
        foreach (var c in written)
            body.Add(new MExprStmt { E = new MCode($"{V}.StoreUnsafe({regs[c]}, ref {MM}.GetArrayDataReference({store}.{c}), (nuint){vi})") });
        if (reduceV != null) body.Add(new MAssign { Target = new MCode(vacc), Op = "+=", Value = new MCode(reduceV) });
        body.Add(new MAssign { Target = new MCode(vi), Op = "+=", Value = new MCode($"{vt}.Count") });
        var block = new MIf { Cond = new MCode($"{V}.IsHardwareAccelerated") };
        if (reduceV != null) block.Then.Add(new MDecl { Name = vacc, Init = new MCode($"{vt}.Zero") });
        block.Then.Add(new MWhile { Cond = new MCode($"{vi} <= {end} - {vt}.Count"), Body = body });
        if (reduceV != null) block.Then.Add(new MAssign { Target = new MCode(acc), Op = "+=", Value = new MCode($"{V}.Sum({vacc})") });
        return block;
    }
}
