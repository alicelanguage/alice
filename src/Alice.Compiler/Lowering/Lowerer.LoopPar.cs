using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Automatic parallelization of outermost counted loops (`for i in a...b`). Proven-independent iterations
/// run in chunks across cores. Allowed effects: locals declared in the body, one numeric `acc += / -= e`
/// reduction (per-chunk partials, summed), and `arr[i] = e` at the loop index. Anything else stays serial.
/// </summary>
public sealed partial class Lowerer
{
    sealed class LoopPlan
    {
        public LocalSym LoopVar = null!;
        public LocalSym? Acc;
        public bool Heavy;               // nested loops: parallel even for small trip counts
        public readonly HashSet<LocalSym> Inner = new();
        public HashSet<LocalSym> Fresh = new();   // locals only ever bound to arrays allocated in this body
        public readonly Dictionary<LocalSym, RangeExpr> InnerRanges = new();   // counters of nested `for j in a...b`
        public readonly Dictionary<LocalSym, Syntax.Expr?> Defs = new();      // body locals bound once (null: rebound)
    }

    /// <summary>
    /// An index this iteration alone writes: the loop counter i, or a row-major cell `i * w + j` (also `w * i + j`,
    /// directly or through a body local bound once) where j counts `0...w` in a nested loop and w is loop-invariant.
    /// Iteration i then owns the cells [i * w, i * w + w), disjoint from every other iteration (checked arithmetic
    /// raises instead of wrapping into another row).
    /// </summary>
    bool OwnIndex(Syntax.Expr idx, LoopPlan plan) => IndexShape(idx, plan) != null;

    /// <summary>
    /// The cells iteration i owns through this index: "i" (cell i) or "row:w" (cells [i * w, i * w + w)); null when it
    /// is neither. Different shapes own different cells: iteration 0 of `a[i * 2 + j]` writes cell 1, iteration 1's `a[i]`.
    /// </summary>
    string? IndexShape(Syntax.Expr idx, LoopPlan plan)
    {
        if (idx is NameExpr { Sym: LocalSym k } && k == plan.LoopVar) return "i";
        if (idx is NameExpr { Sym: LocalSym d } && plan.Defs.TryGetValue(d, out var def) && def != null) idx = def;
        if (idx is not BinaryExpr { Op: "+", L: BinaryExpr { Op: "*" } mul, R: NameExpr { Sym: LocalSym j } }) return null;
        var (a, b) = (mul.L, mul.R);
        var w = a is NameExpr { Sym: LocalSym ai } && ai == plan.LoopVar ? b : b is NameExpr { Sym: LocalSym bi } && bi == plan.LoopVar ? a : null;
        return w is NameExpr { Res: "local", Sym: LocalSym wl } && !plan.Inner.Contains(wl)
            && plan.InnerRanges.TryGetValue(j, out var r) && r.Excl && r.A is LitExpr { Kind: "int", Text: "0" } && r.B is NameExpr { Sym: LocalSym rb } && rb == wl
            ? "row:" + wl.CsName : null;
    }

    MStmt? TryParallelFor(ForStmt f)
    {
        if (inParallelGen || ctx.Contains("lambda") || noHoist > 0 || Method is { TypeParams.Count: > 0 } || Method?.IsAsync == true) return null;
        if (f.Iter is not RangeExpr r || f.Locals.Count != 1 || !f.Locals[0].Type.IsInteger || f.Locals[0].Type.Cs != "int") return null;
        if (WalkStmts(f.Body).Any(s => s is Assign { Target: NameExpr { Sym: LocalSym c } } && c == f.Locals[0])) return null;   // the body moves its own counter
        var plan = new LoopPlan { LoopVar = f.Locals[0] };
        plan.Inner.Add(plan.LoopVar);
        if (!LoopBody(f.Body, plan, 0)) return null;
        // a local first assigned in a branch is declared once for the whole method (Ruby): an iteration that skips the
        // branch reads the previous iteration's value, a loop-carried dependency (and not a local of the chunk lambda)
        if (plan.Inner.Any(hoisted.Contains)) return null;
        // inside a serial loop, a parallel dispatch per outer iteration only pays off for long inner loops
        bool nested = ctx.Contains("loop");
        if (nested && !f.Par) plan.Heavy = false;
        // constant trip count below the threshold: no parallel path at all
        if (!f.Par && r.A is LitExpr { Kind: "int" } la && r.B is LitExpr { Kind: "int" } lb && int.TryParse(la.Text.Replace("_", ""), out int ca) && int.TryParse(lb.Text.Replace("_", ""), out int cb)
            && (long)cb - ca + (r.Excl ? 0 : 1) < (plan.Heavy ? 2 : Threshold)) return null;
        if (plan.Acc != null && WalkReads(f.Body).Any(n => n.Sym == plan.Acc)) return null;
        if (plan.Acc == null && !WalkStmts(f.Body).Any(s => s is Assign { Target: IndexExpr })) return null;   // no observable effect to parallelize
        var aliasGuards = AliasGuards(f.Body, plan);
        if (aliasGuards == null) return null;

        // captured read-only locals become helper parameters
        var captured = new List<LocalSym>();
        foreach (var n in WalkReads(f.Body).Concat(WalkExpr(r.A)).Concat(WalkExpr(r.B)).OfType<NameExpr>())
            if (n is { Res: "local", Sym: LocalSym l } && !plan.Inner.Contains(l) && l != plan.Acc && !captured.Contains(l)) captured.Add(l);
        foreach (var s in WalkStmts(f.Body))
            if (s is Assign { Target: IndexExpr { Target: NameExpr { Sym: LocalSym arr } } } && !captured.Contains(arr)) captured.Add(arr);
        if (captured.Any(l => !l.Type.IsKnown) || WalkReads(f.Body).Any(e => e is NameExpr { Res: "field" or "call" or "selfcall" })) return null;
        if (Sequential) return SerialFor(f);   // proven independent (a `par for` is still checked), run serially

        string start = Temp("a"), count = Temp("n");
        pre.Add(new MDecl { Name = start, TypeCs = "int", Init = Expr(r.A) });
        var end = Expr(r.B);
        pre.Add(new MDecl { Name = count, TypeCs = "int", Init = new AirTripCount { End = end, Start = AirLeaf.Name(start, AirLeaf.Int), Inclusive = !r.Excl, Type = AirLeaf.Int } });

        // serial path: the ordinary loop
        var serial = new List<MStmt> { SerialFor(f) };

        // parallel helper
        string lo = Temp("lo"), hi = Temp("hi"), i = plan.LoopVar.CsName;
        bool saved = inParallelGen;
        inParallelGen = true;
        var savedPre = pre;
        pre = new List<MStmt>();
        ctx.Add("loop");
        var body = Stmts(f.Body, Tail.None, null);
        ctx.RemoveAt(ctx.Count - 1);
        var bodyPre = pre;
        pre = savedPre;
        inParallelGen = saved;
        var chunk = new List<MStmt>();
        string? accT = plan.Acc != null ? TypeCs(plan.Acc.Type) : null;
        var accA = plan.Acc?.Type.ToAir();
        if (plan.Acc != null) chunk.Add(new MDecl { Name = plan.Acc.CsName, TypeCs = accT, Init = AirLeaf.Default(accA) });
        chunk.AddRange(bodyPre);
        // chunk bounds lie in [0, count]: a literal range start bounds the counter from below
        long? minStart = r.A is LitExpr { Kind: "int" } ls && long.TryParse(ls.Text.Replace("_", ""), out long sv) ? sv : null;
        chunk.Add(new MFor { VarType = "int", Var = i, Start = Offset(start, lo), Cmp = "<", End = Offset(start, hi), Body = body, MinStart = minStart });
        int grain = plan.Heavy ? 1 : 8192;
        string helper = $"__parfor_{(Method?.CsName ?? "main").TrimStart('@')}_{++helperN}";
        var ps = captured.Select(l => $"{LocalTypeCs(l)} {l.CsName}").Append($"int {start}").Append($"int {count}");
        var args = captured.Select(l => l.CsName).Append(start).Append(count);
        var hp = new Backend.Printer("", 1);
        if (plan.Acc != null)
        {
            chunk.Add(new MReturn { Value = AirLeaf.Name(plan.Acc.CsName, accA) });
            hp.Root(new MReturn
            {
                Value = new AirPar
                {
                    Kind = AirParKind.Reduce,
                    ResultCs = accT,
                    Count = AirLeaf.Name(count, AirLeaf.Int),
                    Kernel = new MLambda { Params = { lo, hi }, Body = chunk, Hot = true },
                    Combine = Combine("+", "x", "y"),
                    Grain = grain,
                    Type = accA,
                },
            });
            Extra.Add($"private {HelperStatic}{accT} {helper}({string.Join(", ", ps)})\n{{\n{hp}}}\n");
        }
        else
        {
            hp.Root(new MExprStmt { E = new AirPar { Kind = AirParKind.Range, Count = AirLeaf.Name(count, AirLeaf.Int), Kernel = new MLambda { Params = { lo, hi }, Body = chunk, Hot = true }, Grain = grain, Type = AirLeaf.Void } });
            Extra.Add($"private {HelperStatic}void {helper}({string.Join(", ", ps)})\n{{\n{hp}}}\n");
        }
        MStmt Dispatch(string from, MExpr n)
        {
            var call = new MCall
            {
                Name = helper,
                Args = captured.Select(l => (MExpr)AirLeaf.Name(l.CsName, l.Type.ToAir())).Append(AirLeaf.Name(from, AirLeaf.Int)).Append(n).ToList(),
                Type = accA ?? AirLeaf.Void,
            };
            return plan.Acc != null
                ? new MAssign { Target = AirLeaf.Name(plan.Acc.CsName, accA), Op = "+=", Value = call }
                : new MExprStmt { E = call };
        }
        var cond = new AirParGuard
        {
            Count = AirLeaf.Name(count, AirLeaf.Int),
            Min = plan.Heavy || f.Par ? 2 : Threshold,
            Distinct = aliasGuards.Select(g => new AirReferenceEqualsGuard { W = AirLeaf.Name(g.W.CsName, g.W.Type.ToAir()), R = AirLeaf.Name(g.R.CsName, g.R.Type.ToAir()), Type = AirLeaf.Bool }).ToList(),
            Type = AirLeaf.Bool,
        };
        // probe: run the first two iterations here and time the second (the first runs cold: cache misses, first-touch
        // page faults); at the top of iteration 2, 4, 8, ..., offload the rest only when the projected work pays for a dispatch
        // (Par.Offload). Short loops never JIT or wake the parallel machinery.
        if (!f.Par && SerialFor(f) is MFor probe && probe.Var == i)
        {
            string t0 = Temp("t0");
            probe.Body.Insert(0, new MIf
            {
                Cond = new AirParProbe { Kind = AirProbeKind.Second, I = AirLeaf.Name(i, AirLeaf.Int), Start = AirLeaf.Name(start, AirLeaf.Int), Type = AirLeaf.Bool },
                Then = { new MAssign { Target = AirLeaf.Name(t0, AirLeaf.Long), Value = new AirParProbe { Kind = AirProbeKind.Timestamp, Type = AirLeaf.Long } } },
                Else = [new MIf
                {
                    // retried at k = 2, 4, 8, ... iterations: leading iterations can be far cheaper than the rest
                    // (mandelbrot's edge rows escape at once), and one sample would pin the whole loop serial
                    Cond = new AirParProbe
                    {
                        Kind = AirProbeKind.Offload,
                        I = AirLeaf.Name(i, AirLeaf.Int),
                        Start = AirLeaf.Name(start, AirLeaf.Int),
                        T0 = AirLeaf.Name(t0, AirLeaf.Long),
                        Count = AirLeaf.Name(count, AirLeaf.Int),
                        Type = AirLeaf.Bool,
                    },
                    // the iterations left: count - (i - start)
                    Then = { Dispatch(i, Remaining()), new MBreak() },
                }],
            });
            return new MIf { Cond = cond, Then = { new MDecl { Name = t0, TypeCs = "long", Init = AirLeaf.Lit("0", AirLeaf.Long) }, probe }, Else = serial };
        }
        return new MIf { Cond = cond, Then = { Dispatch(start, AirLeaf.Name(count, AirLeaf.Int)) }, Else = serial };

        MExpr Remaining() => new MBinary
        {
            Op = AirBinOp.Sub, Type = AirLeaf.Int, L = AirLeaf.Name(count, AirLeaf.Int),
            R = new MBinary { Op = AirBinOp.Sub, Type = AirLeaf.Int, L = AirLeaf.Name(i, AirLeaf.Int), R = AirLeaf.Name(start, AirLeaf.Int) },
        };
    }

    /// <summary>`start + lo`: a chunk bound of the parallel helper's loop.</summary>
    static AirOffset Offset(string start, string bound) =>
        new() { Base = AirLeaf.Name(start, AirLeaf.Int), Offset = AirLeaf.Name(bound, AirLeaf.Int), Type = AirLeaf.Int };

    /// <summary>
    /// Cross-iteration dependencies through arrays. A written array may be read only at the loop index (`a[i] = a[i - 1]`
    /// is a prefix scan, inherently serial). Another local array read at a shifted index (a stencil) is independent
    /// unless it is the same object as a written one, which a runtime check decides: the returned (written, read)
    /// pairs must be distinct for the parallel path. Null: a dependency the loop cannot rule out.
    /// </summary>
    List<(LocalSym W, LocalSym R)>? AliasGuards(List<Stmt> body, LoopPlan plan)
    {
        var writes = new List<(LocalSym Arr, string Shape)>();
        foreach (var a in WalkStmts(body).OfType<Assign>())
            if (a.Target is IndexExpr { Target: NameExpr { Sym: LocalSym w }, Args: [{ Value: { } k }] } && IndexShape(k, plan) is { } shape && !stackArrays.ContainsKey(w) && !writes.Contains((w, shape)))
                writes.Add((w, shape));
        var guards = new List<(LocalSym, LocalSym)>();
        void Guard(LocalSym w, LocalSym r)
        {
            if (!guards.Contains((w, r)) && !guards.Contains((r, w))) guards.Add((w, r));
        }
        // one array written in two shapes: iterations overlap; two arrays in two shapes: only when they are one object
        foreach (var (w1, s1) in writes)
            foreach (var (w2, s2) in writes)
                if (s1 != s2 && MayAlias(w1.Type, w2.Type))
                {
                    if (w1 == w2) return null;
                    Guard(w1, w2);
                }
        foreach (var ix in WalkReads(body).OfType<IndexExpr>())
        {
            if (ix.Target.Type?.Kind != "array") continue;   // strings and other immutable reads cannot alias an array
            if (ix.Target is NameExpr { Sym: LocalSym sa } && stackArrays.ContainsKey(sa)) continue;   // iteration-private buffer
            string? shape = ix.Args is [{ Mod: "", Value: { } k }] ? IndexShape(k, plan) : null;
            var r = ix.Target is NameExpr { Sym: LocalSym rl } ? rl : null;
            foreach (var (w, ws) in writes)
            {
                if (shape == ws) continue;   // the cells this iteration writes itself: never another iteration's
                if (r == w) return null;     // `a[i] = a[i - 1]` (a prefix scan) or a read in another shape
                if (!MayAlias(w.Type, ix.Target.Type)) continue;
                if (r == null) return null;  // an array behind an expression: no local to compare at run time
                Guard(w, r);
            }
        }
        if (writes.Count == 0) return guards;
        // an array read whole (handed to a callee, `xs.sum`) may be read at any index, also another iteration's cell
        var indexed = new HashSet<Syntax.Expr>();
        foreach (var e in WalkReads(body))
            if (e is IndexExpr ix) indexed.Add(ix.Target);
            else if (SizeOf(e) is { } sized) indexed.Add(sized);
        var atCounter = CounterLocalArgs(body, plan);
        foreach (var e in WalkReads(body))
        {
            // a callee may read arrays this body never names: only those reached through its array parameters are
            // checked here (as the arguments of the call); fields, globals and anything else keep the loop serial
            if (Callee(e) is { } m && !Overrides(m).All(o => ArraysOnlyThroughParams(o, new()))) return null;
            if (e.Type?.Kind != "array" || indexed.Contains(e) || e is NewExpr or ArrayLit) continue;
            if (e is NameExpr { Sym: LocalSym sa } && stackArrays.ContainsKey(sa)) continue;
            var r = e is NameExpr { Sym: LocalSym rl } && e.Type.Elem is { Kind: "prim" } ? rl : null;
            // `f(a, i)` where f reads only `a[i]`: a read of the cell this iteration owns, like a direct `a[i]`
            string? shape = r != null && atCounter.Contains(e) ? "i" : null;
            foreach (var (w, ws) in writes)
            {
                if (shape == ws) continue;
                if (r == w) return null;
                if (e.Type.Elem is { Kind: "prim" } && !MayAlias(w.Type, e.Type)) continue;
                if (r == null) return null;   // its elements may be the written array, or no local to compare at run time
                Guard(w, r);
            }
        }
        return guards;
    }

    /// <summary>
    /// Array arguments a callee reads only at the loop counter: `f(a, i)` where i is the counter and every override of
    /// f reads its array parameter only as `p[j]` (j the parameter bound to i), sizes it, or lends it on the same way.
    /// </summary>
    HashSet<Syntax.Expr> CounterLocalArgs(List<Stmt> body, LoopPlan plan)
    {
        var r = new HashSet<Syntax.Expr>();
        foreach (var c in WalkReads(body).OfType<CallExpr>())
        {
            if (c is not { Res: "alice", Sym: MethodSym m, Block: null }) continue;
            int j = c.Args.FindIndex(a => a is { Mod: "", Name: null, Value: NameExpr { Sym: LocalSym li } } && li == plan.LoopVar);
            if (j < 0) continue;
            for (int k = 0; k < c.Args.Count; k++)
                if (k != j && c.Args[k] is { Mod: "", Name: null, Value: NameExpr { Sym: LocalSym, Type.Kind: "array" } v }
                    && Overrides(m).All(o => ReadsOnlyAt(o, k, j, new())))
                    r.Add(v);
        }
        return r;
    }

    /// <summary>Inside m, array parameter k is read only at the index held by parameter j (neither is reassigned).</summary>
    bool ReadsOnlyAt(MethodSym m, int k, int j, HashSet<(MethodSym, int, int)> visiting)
    {
        if (m.Decl is not { HasBody: true } d || m.IsAsync || m.UsesYield || k >= m.Params.Count || j >= m.Params.Count) return false;
        if (m.Params[k].Splat || m.Params[k].Block || m.Params[j].Splat || m.Params[j].Block || !m.Params[j].Type.IsInteger) return false;
        if (!visiting.Add((m, k, j))) return true;
        string arr = m.Params[k].Name, idx = m.Params[j].Name;
        bool Is(Syntax.Expr? e, string name) => e is NameExpr { Sym: LocalSym x } && x.Name == name;
        if (WalkStmts(d.Body).Any(s => s is Assign a && (Is(a.Target, arr) || Is(a.Target, idx)) || s is MultiAssign ma && (ma.Names.Contains(arr) || ma.Names.Contains(idx)))) return false;
        int mentions = 0, allowed = 0;
        foreach (var e in Walk(d.Body))
        {
            switch (e)
            {
                case NameExpr n when Is(n, arr): mentions++; break;
                case IndexExpr { Args: [{ Mod: "", Value: var iv }] } ix when Is(ix.Target, arr): if (Is(iv, idx)) allowed++; break;
                case LambdaExpr lam when WalkExpr(lam).Any(x => Is(x, arr)): return false;
                case CallExpr { Block: { } blk } when Walk(blk.Body).Any(x => Is(x, arr)): return false;
                case CallExpr { Res: "alice", Sym: MethodSym callee, Block: null } call:
                    {
                        int jj = call.Args.FindIndex(a => a is { Mod: "", Name: null } && Is(a.Value, idx));
                        for (int kk = 0; kk < call.Args.Count; kk++)
                            if (Is(call.Args[kk].Value, arr) && call.Args[kk] is { Mod: "", Name: null } && jj >= 0
                                && Overrides(callee).All(o => ReadsOnlyAt(o, kk, jj, visiting))) allowed++;
                        break;
                    }
                default:
                    if (SizeOf(e) is { } sz && Is(sz, arr)) allowed++;
                    break;
            }
        }
        return mentions == allowed;
    }

    /// <summary>The array whose size `e` reads (`xs.size`, `xs.Length`): no element is read.</summary>
    static Syntax.Expr? SizeOf(Syntax.Expr e) => e switch
    {
        CallExpr { Res: "ext", Args.Count: 0, Block: null, Recv: { } r } c when Binder.Mangle(c.Name) is "size" or "length" => r,
        CallExpr { Name: "Length", Args.Count: 0, Recv: { } r } => r,
        _ => null,
    };

    /// <summary>The Alice method (or constructor) an expression runs, if any.</summary>
    static MethodSym? Callee(Syntax.Expr e) => e switch
    {
        CallExpr { Res: "alice", Sym: MethodSym m } => m,
        NameExpr { Res: "call", Sym: MethodSym m } => m,
        NewExpr { AsCall: null, Type.Sym: { } ts } n => ts.Methods.TryGetValue("initialize", out var list) ? list.FirstOrDefault(c => c.Accepts(n.Args.Count)) : null,
        _ => null,
    };

    /// <summary>
    /// Every array m reads is reached through one of its array parameters (or is allocated inside it): no field,
    /// global or other object leads it to an array, so the caller's arguments name everything it can read.
    /// </summary>
    bool ArraysOnlyThroughParams(MethodSym m, HashSet<MethodSym> visiting)
    {
        if (m.Decl is not { HasBody: true } d) return false;
        if (!visiting.Add(m)) return true;
        var assigned = WalkStmts(d.Body).OfType<Assign>().Select(Assigned).OfType<LocalSym>().ToHashSet();
        var parms = m.Params.Where(p => p.Type.Kind == "array").Select(p => p.Name).ToHashSet();
        var fresh = FreshArrays(d.Body);
        var targets = WalkStmts(d.Body).OfType<Assign>().Select(a => a.Target).Where(t => t is NameExpr or IvarExpr).ToHashSet();
        bool Rooted(Syntax.Expr x) => x switch
        {
            NameExpr { Sym: LocalSym l } => fresh.Contains(l) || parms.Contains(l.Name) && !assigned.Contains(l),
            IndexExpr ix => ix.Target.Type?.Kind == "array" && Rooted(ix.Target),
            NewExpr or ArrayLit => true,
            _ => false,
        };
        foreach (var e in Walk(d.Body))
        {
            if (Callee(e) is { } c && !Overrides(c).All(o => ArraysOnlyThroughParams(o, visiting))) return false;
            if (e.Type?.Kind == "array" && !targets.Contains(e) && !Rooted(e)) return false;
        }
        return true;
    }

    /// <summary>Arrays of distinct primitive element types are distinct objects; anything else may share storage.</summary>
    static bool MayAlias(AType a, AType b) =>
        !(a.Elem is { Kind: "prim" } ea && b.Elem is { Kind: "prim" } eb && ea.Cs != eb.Cs);

    /// <summary>Checks every statement for loop-carried dependencies; fills the plan.</summary>
    bool LoopBody(List<Stmt> body, LoopPlan plan, int depth)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case ExprStmt es when PureRead(es.Expr, plan):
                    break;
                case LocalDecl d when d.Value == null || PureRead(d.Value, plan):
                    if (d.Local != null) { plan.Inner.Add(d.Local); plan.Defs[d.Local] = null; }
                    break;
                case Assign a when PureRead(a.Value, plan):
                    if (a.Declares != null) { plan.Inner.Add(a.Declares); plan.Defs[a.Declares] = plan.Defs.ContainsKey(a.Declares) || a.Op != "=" ? null : a.Value; break; }
                    if (a.Target is NameExpr { Sym: LocalSym l })
                    {
                        if (plan.Inner.Contains(l)) { plan.Defs[l] = null; break; }
                        if (a.Op is "+=" or "-=" && l.Type.IsNumeric && (plan.Acc == null || plan.Acc == l)) { plan.Acc = l; break; }
                        return false;
                    }
                    if (a.Op == "=" && a.Target is IndexExpr { Target: NameExpr { Sym: LocalSym arr, Type.Kind: "array" }, Args: [{ Mod: "", Value: { } idx }] }
                        && OwnIndex(idx, plan) && !plan.Inner.Contains(arr)) break;
                    return false;
                case IfStmt i when i.Conds.All(c => PureRead(c, plan)):
                    if (!i.Bodies.All(b => LoopBody(b, plan, depth)) || (i.Else != null && !LoopBody(i.Else, plan, depth))) return false;
                    break;
                case WhileStmt w when PureRead(w.Cond, plan):
                    plan.Heavy = true;
                    if (!LoopBody(w.Body, plan, depth + 1)) return false;
                    break;
                case ForStmt fr when fr.Iter is RangeExpr rr && PureRead(rr.A, plan) && PureRead(rr.B, plan):
                    plan.Heavy = true;
                    plan.Inner.UnionWith(fr.Locals);
                    if (fr.Locals.Count == 1 && !WalkStmts(fr.Body).Any(x => x is Assign { Target: NameExpr { Sym: LocalSym c } } && c == fr.Locals[0])) plan.InnerRanges[fr.Locals[0]] = rr;
                    if (!LoopBody(fr.Body, plan, depth + 1)) return false;
                    break;
                case NextStmt { Value: null }:
                    break;
                case BreakStmt when depth > 0:
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>Side-effect-free expression (reads of locals/arrays/fields, arithmetic, Math.*).</summary>
    bool PureRead(Syntax.Expr e, LoopPlan plan) => e switch
    {
        LitExpr => true,
        NameExpr { Res: "local" or "field" } => true,
        IvarExpr or SelfExpr => true,
        TypeTestExpr tt => tt.E == null || PureRead(tt.E, plan),
        NewExpr { Type.Kind: "array", Block: null, AsCall: null } ne => ne.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),   // allocation is not an effect
        InterpExpr ie => ie.Holes.All(h => PureRead(h, plan)),
        ConstExpr { Res: "type" } => true,   // `Module.f(x)`: naming a type reads no state
        ArrayLit lit => lit.Items.All(x => PureRead(x, plan)),   // a fresh array: allocation is not an effect
        CallExpr { Res: "clr", Block: null, Sym: System.Reflection.MethodInfo mi } rc when PureRuntime(mi) => rc.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        BinaryExpr b => PureOperator(b) && PureRead(b.L, plan) && PureRead(b.R, plan),
        UnaryExpr { Op: "-" or "!" or "~" } u => PureRead(u.E, plan),
        TernaryExpr t => PureRead(t.C, plan) && PureRead(t.A, plan) && PureRead(t.B, plan),
        IndexExpr ix => PureRead(ix.Target, plan) && ix.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        CallExpr { Res: "field" } f => f.Recv != null && PureRead(f.Recv, plan),
        CallExpr m when IsClrMath(m) => m.Block == null && m.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        CallExpr { Res: "ext" } x => PureExt.Contains(Binder.Mangle(x.Name)) && x.Block == null && x.Recv != null && PureRead(x.Recv, plan) && x.Args.All(a => PureRead(a.Value!, plan)),
        CallExpr { Res: "alice", Sym: MethodSym m, Block: null } c => MarkHeavy(plan) && MethodPure(m) && (c.Recv == null || PureRead(c.Recv, plan)) && c.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        NameExpr { Res: "call", Sym: MethodSym m } => MarkHeavy(plan) && MethodPure(m),
        NewExpr { AsCall: null, Block: null } n => n.Type.Sym is { Kind: "class" or "struct" or "record" or "record struct" or "variant" } ts && CtorPure(ts, n.Args.Count) && n.Args.All(a => a.Mod == "" && PureRead(a.Value!, plan)),
        NewExpr { AsCall: { } ac } => PureRead(ac, plan),
        StmtExpr { Stmt: var st } => PureMethodBody([st], plan, false),   // if / case used as a value
        _ => false,
    };

    /// <summary>
    /// `xs << v` appends (a write); a user-defined operator (`def ==(o)`, `<=>`) runs a method that must be pure
    /// itself, like any other call.
    /// </summary>
    bool PureOperator(BinaryExpr b) => b.Res switch
    {
        "append" => false,
        "usercmp" => b.L.Type.Sym?.FindMethod("<=>", 1) is MethodSym c && MethodPure(c),
        _ => b.Op is "&&" or "||" || b.L.Type.Sym?.FindMethod(b.Op, 1) is not MethodSym om || MethodPure(om),
    };

    /// <summary>
    /// A call of System.Math / System.MathF as resolved by the binder, not by spelling: a user `module Math` is an
    /// ordinary Alice module whose methods may have effects.
    /// </summary>
    internal static bool IsClrMath(CallExpr c) =>
        c is { Res: "clr", Recv: ConstExpr, Sym: System.Reflection.MethodInfo { DeclaringType.FullName: "System.Math" or "System.MathF" } };

    /// <summary>A `when` pattern: type tests and bindings read nothing; compared values (`when lookup(k)`,
    /// `when Shape::Rect(w, limit())`) must be pure reads.</summary>
    bool PurePattern(Syntax.Expr p, LoopPlan plan) => p.Res switch
    {
        "destructure" => p.Sym is Destructure d && d.Parts.All(x => x.Eq == null || PureRead(x.Eq, plan)),
        "type" => true,
        "hashpat" or "arraypat" => true,   // literal keys/elements and bindings
        _ => PureRead(p, plan),
    };

    static bool MarkHeavy(LoopPlan plan)
    {
        plan.Heavy = true;   // user calls have unknown cost: parallelize even short loops
        return true;
    }

    readonly Dictionary<MethodSym, bool> purity = new();

    /// <summary>Loop helpers of instance methods are instance methods too (bodies may read @ivars / self).</summary>
    string HelperStatic => Method is { IsStatic: false } && Type.Kind != "extension" ? "" : "static ";

    /// <summary>
    /// Interprocedural purity: writes only its own locals (and, for constructors, the fresh object's fields),
    /// calls only pure methods, raises nothing. Recursion is assumed pure while being analysed.
    /// </summary>
    /// <summary>A static Alice.Runtime method marked [Pure]: the runtime vouches for it; arbitrary .NET code is never trusted.</summary>
    /// Compared by name: referenced assemblies may load in a separate metadata context.
    static bool PureRuntime(System.Reflection.MethodInfo mi) =>
        mi.IsStatic && mi.DeclaringType?.Assembly.GetName().Name == "Alice.Runtime"
        && mi.CustomAttributes.Any(a => a.AttributeType.FullName == "Alice.Runtime.PureAttribute");

    bool MethodPure(MethodSym m)
    {
        if (purity.TryGetValue(m, out var known)) return known;
        if (m.Decl == null || !m.Decl.HasBody || m.UsesYield || m.IsAsync || m.Mutates && !m.IsCtor) return purity[m] = false;
        purity[m] = true;
        var plan = new LoopPlan { LoopVar = new LocalSym(), Fresh = FreshArrays(m.Decl.Body) };
        bool ok = PureMethodBody(m.Decl.Body, plan, m.IsCtor) && OverridesPure(m);
        purity[m] = ok;
        return ok;
    }

    /// <summary>
    /// A call of an instance method of a class with subclasses is virtual: the receiver may be a subclass whose
    /// override writes fields. The call is pure only when every override in the program is.
    /// </summary>
    bool OverridesPure(MethodSym m)
    {
        if (m.IsStatic || m.IsCtor || m.Owner is not { Kind: "class", HasSubclass: true } owner) return true;
        foreach (var t in binder.Types.Values.Distinct())
        {
            if (t == owner || !Inherits(t, owner) || !t.Methods.TryGetValue(m.Name, out var list)) continue;
            foreach (var o in list)
                if (o != m && !o.IsStatic && !MethodPure(o)) return false;
        }
        return true;
    }

    static bool Inherits(TypeSym t, TypeSym b)
    {
        for (var s = t.BaseClass; s != null; s = s.BaseClass)
            if (s == b) return true;
        return false;
    }

    bool CtorPure(TypeSym t, int argc)
    {
        if (t.Kind is "record" or "record struct" or "variant") return true;   // positional records (and sum-type variants) only store their arguments
        var ctor = t.Methods.TryGetValue("initialize", out var list) ? list.FirstOrDefault(c => c.Accepts(argc)) : null;
        return ctor == null || MethodPure(ctor);
    }

    bool PureMethodBody(List<Stmt> body, LoopPlan plan, bool ctor)
    {
        foreach (var s in body)
        {
            bool ok = s switch
            {
                ExprStmt es => PureRead(es.Expr, plan),
                LocalDecl d => d.Value == null || PureRead(d.Value, plan),
                Assign a => PureRead(a.Value, plan) && (a.Declares != null || a.Target is NameExpr { Res: "local" } || (ctor && a.Target is IvarExpr)
                    || (a.Target is IndexExpr { Target: NameExpr { Sym: LocalSym fl } } ix && plan.Fresh.Contains(fl) && ix.Args.All(x => x.Mod == "" && PureRead(x.Value!, plan)))),
                ReturnStmt r => r.Value == null || PureRead(r.Value, plan),
                MultiAssign ma => ma.Values.All(v => PureRead(v, plan)),   // targets are locals
                IfStmt i => i.Conds.All(c => PureRead(c, plan)) && i.Bodies.All(b => PureMethodBody(b, plan, ctor)) && (i.Else == null || PureMethodBody(i.Else, plan, ctor)),
                WhileStmt w => PureRead(w.Cond, plan) && PureMethodBody(w.Body, plan, ctor),
                ForStmt f => PureRead(f.Iter is RangeExpr rr ? rr.A : f.Iter, plan) && (f.Iter is not RangeExpr r2 || PureRead(r2.B, plan)) && PureMethodBody(f.Body, plan, ctor),
                BreakStmt or NextStmt { Value: null } => true,
                // a match over a value: patterns only bind fresh locals, so it is pure when the subject, guards and arms are
                CaseStmt cs => (cs.Subject == null || PureRead(cs.Subject, plan))
                    && cs.Whens.All(w => w.Patterns.All(p => PurePattern(p, plan)) && (w.Guard == null || PureRead(w.Guard, plan)) && PureMethodBody(w.Body, plan, ctor))
                    && (cs.Else == null || PureMethodBody(cs.Else, plan, ctor)),
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Locals whose every assignment in the body is a new array: writes through them cannot reach any caller's
    /// data (no alias of a parameter, field or other local), so they do not make the method impure.
    /// </summary>
    static HashSet<LocalSym> FreshArrays(List<Stmt> body)
    {
        var fresh = new HashSet<LocalSym>();
        var tainted = new HashSet<LocalSym>();
        foreach (var s in WalkStmts(body))
        {
            if (s is not Assign a) continue;
            var l = a.Declares ?? (a.Target as NameExpr)?.Sym as LocalSym;
            if (l == null) continue;
            if (a.Op == "=" && a.Value is NewExpr { Type.Kind: "array" } or ArrayLit) fresh.Add(l);
            else tainted.Add(l);
        }
        fresh.ExceptWith(tainted);
        return fresh;
    }

    /// <summary>Every expression read in the body (assignment targets excluded, their indices included).</summary>
    static IEnumerable<Syntax.Expr> WalkReads(List<Stmt> body)
    {
        foreach (var s in WalkStmts(body))
        {
            IEnumerable<Syntax.Expr?> roots = s switch
            {
                ExprStmt es => [es.Expr],
                Assign a => a.Target is IndexExpr ix ? ix.Args.Select(x => x.Value).Append(a.Value) : [a.Value],
                LocalDecl d => [d.Value],
                IfStmt i => i.Conds,
                WhileStmt w => [w.Cond],
                ForStmt fr => [fr.Iter],
                _ => [],
            };
            foreach (var r in roots)
                if (r != null)
                    foreach (var x in WalkExpr(r)) yield return x;
        }
    }
}
