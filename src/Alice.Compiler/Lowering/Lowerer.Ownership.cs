using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Ownership (ADR 0016). A local array of unmanaged elements is owned when it is provably the only reference to its
/// array: every value it receives is a fresh `Array(T).new(n[, v])` or another owned local (a move), and every use
/// is an element access, `.size`, `for v in a`, a borrow by an Alice parameter that never retains it, a move into
/// another owned local, or the method's result. Owned buffers come from `Alice.Runtime.Own(T)` and go back to it
/// where the program provably drops them: before the local is overwritten and at every method exit, so a loop that
/// builds a fresh row per iteration stops allocating once warm. Nothing is released on an exception path; the
/// array is then ordinary garbage, so a missed release costs an allocation, never safety.
///
/// Inference is silent: a local that breaks a rule simply stays a GC array. `own x = ...` makes the rules a
/// contract: an escape is AL0207, a read of a moved local AL0208.
/// </summary>
public sealed partial class Lowerer
{
    /// <summary>Owned locals of the method being lowered: element C# type.</summary>
    Dictionary<LocalSym, string> owned = new();

    /// <summary>Source file of the item being lowered (ownership diagnostics).</summary>
    public string File = "";

    /// <summary>Assignments to an owned local whose previous array is definitely live (released first).</summary>
    readonly HashSet<Assign> releaseBefore = new();

    /// <summary>Exits (`return`, tail value, end of body) and the owned locals definitely live there.</summary>
    readonly Dictionary<Stmt, HashSet<LocalSym>> releaseAt = new();

    HashSet<LocalSym> releaseAtEnd = new();

    void FindOwned(List<Stmt> body, Tail tail)
    {
        owned = new();
        releaseBefore.Clear();
        releaseAt.Clear();
        releaseAtEnd = new();
        if (Method is null || Method.IsAsync || Method.UsesYield || Method.IsCtor) return;
        var explicitOwn = WalkStmts(body).OfType<Assign>().Where(a => a.Own).Select(a => a.Declares ?? (a.Target as NameExpr)?.Sym as LocalSym).OfType<LocalSym>().ToHashSet();
        var reasons = new Dictionary<LocalSym, (Node At, string Why)>();
        // a `scope` region checks the lifetime of every unmanaged array created in it: owned, or AL0207
        foreach (var region in WalkStmts(body).OfType<BeginStmt>().Where(b => b.Scope))
            foreach (var a in WalkStmts(region.Body).OfType<Assign>())
                if (a.Declares is { Type: { Kind: "array", Elem: { } rel } } rl && Unmanaged.Contains(rel.Cs) && (Fresh(a.Value) || OwnedResult(a.Value))) explicitOwn.Add(rl);

        // 1. candidates: unmanaged arrays fed only by fresh arrays or other candidates, and `own` parameters
        var cands = new Dictionary<LocalSym, string>();
        var ownParams = new HashSet<LocalSym>();
        foreach (var ps in Method.Params)
            if (ps is { Own: true, Local: { Type: { Kind: "array", Elem: { } pel } } pl } && Unmanaged.Contains(pel.Cs))
            {
                cands[pl] = pel.Cs;
                ownParams.Add(pl);
                explicitOwn.Add(pl);
            }
        foreach (var s in WalkStmts(body))
            if (s is Assign { Op: "=" } a && Assigned(a) is { } l && l.Type is { Kind: "array", Elem: { } el } && Unmanaged.Contains(el.Cs)
                && !stackArrays.ContainsKey(l) && !flatRows.ContainsKey(l))
                cands.TryAdd(l, el.Cs);
        var edges = new List<(LocalSym, LocalSym)>();
        foreach (var s in WalkStmts(body))
        {
            switch (s)
            {
                case Assign a when Assigned(a) is { } l && cands.ContainsKey(l):
                    if (a.Op != "=") Drop(cands, reasons, l, a, "compound assignment to an owned array");
                    else if (Fresh(a.Value) || InPlaceMap(a, l) != null || OwnedResult(a.Value)) { }
                    else if (a.Value is NameExpr { Sym: LocalSym src } && src != l && cands.ContainsKey(src)) edges.Add((l, src));
                    else Drop(cands, reasons, l, a, "it is assigned a value it does not own");
                    break;
                case MultiAssign ma:
                    foreach (var l in ma.Locals) Drop(cands, reasons, l, ma, "multiple assignment");
                    break;
                case LocalDecl { Local: { } l } d:
                    Drop(cands, reasons, l, d, "typed declaration");
                    break;
                case BeginStmt { Rescues.Count: > 0 } or BeginStmt { Ensure: not null }:
                    foreach (var n in Walk(((BeginStmt)s).Body).OfType<NameExpr>()) if (n.Sym is LocalSym l) Drop(cands, reasons, l, s, "used inside begin/rescue");
                    break;
            }
        }

        // 2. every mention must be an owning use
        var tailValues = new HashSet<Syntax.Expr>();
        if (tail == Tail.Return) TailExprs(body, tailValues);
        foreach (var l in cands.Keys.ToList()) CheckUses(body, l, cands, reasons, tailValues);

        // 3. moves join locals into one ownership group: all of it is owned or none
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (x, y) in edges)
                if (cands.ContainsKey(x) != cands.ContainsKey(y))
                {
                    var gone = cands.ContainsKey(x) ? x : y;
                    Drop(cands, reasons, gone, null, reasons.TryGetValue(gone == x ? y : x, out var r) ? r.Why : "it shares an array with a local that is not owned");
                    changed = true;
                }
        }

        // 4. flow: no use of a possibly moved local; releases where the old array is definitely live
        for (int round = 0; round < 8 && cands.Count > 0; round++)
        {
            releaseBefore.Clear();
            releaseAt.Clear();
            var flow = new OwnFlow(cands, tail);
            var end = flow.Block(body, new OwnState { Live = new(ownParams.Where(cands.ContainsKey)) }, tail);
            if (flow.Violations.Count == 0)
            {
                foreach (var (a, ok) in flow.ReleaseBefore) if (ok) releaseBefore.Add(a);
                foreach (var (st, live) in flow.ReleaseAt) releaseAt[st] = live;
                releaseAtEnd = end.Reachable ? end.Live : new();
                break;
            }
            foreach (var (l, at) in flow.Violations) Drop(cands, reasons, l, at, "use after move");
            foreach (var (x, y) in edges)   // a dropped local takes its move partners with it
                if (cands.ContainsKey(x) != cands.ContainsKey(y)) { Drop(cands, reasons, x, null, "use after move"); Drop(cands, reasons, y, null, "use after move"); }
        }

        foreach (var l in explicitOwn)
            if (!cands.ContainsKey(l) && reasons.TryGetValue(l, out var r))
                throw new CompileException($"{File}:{(r.At ?? (Node)body[0]).Line}:{Math.Max(1, (r.At ?? body[0]).Col)}: " + (r.Why == "use after move"
                    ? $"use after move: `{l.Name}` was moved into another owned local; reassign it before using it again"
                    : $"owned value `{l.Name}` escapes: {r.Why}"));
        owned = cands;
    }

    /// <summary>
    /// `l = l.map { |v| e }` keeping the element type, with a one-expression block that does not mention l:
    /// the block of the single-owner update. l owns its buffer, so the map can overwrite it (Bend's in-place update).
    /// </summary>
    static LambdaExpr? InPlaceMap(Assign a, LocalSym l) =>
        a.Op == "=" && a.Value is CallExpr { Res: "ext", Args.Count: 0, Recv: NameExpr { Sym: LocalSym r }, Block: { ParamSyms.Count: 1, Body: [ExprStmt] } blk } c
        && r == l && Binder.Mangle(c.Name) is "map" && c.Type.Elem?.Cs == l.Type.Elem?.Cs && c.Type.Elem != null
        && !Walk(blk.Body).OfType<NameExpr>().Any(n => n.Sym == l)
            ? blk : null;

    /// <summary>`for (int i = 0; i < l.Length; i++) { var v = l[i]; l[i] = e; }`: no allocation, no release.</summary>
    bool TryInPlaceMap(List<MStmt> o, Assign a, LocalSym l)
    {
        if (InPlaceMap(a, l) is not { } blk) return false;
        string i = Temp("i");
        var p = blk.ParamSyms[0];
        var body = new List<MStmt> { new MDecl { Name = p.CsName, Init = new MIndex { Target = new MCode(l.CsName), Args = { new MCode(i) } } } };
        var value = Isolated(((ExprStmt)blk.Body[0]).Expr, body);
        body.Add(new MAssign { Target = new MIndex { Target = new MCode(l.CsName), Args = { new MCode(i) } }, Value = value });
        o.Add(new MFor { Var = i, VarType = "int", Start = new MCode("0"), End = new MCode(l.CsName + ".Length"), Body = body });
        return true;
    }

    readonly Dictionary<MethodSym, bool> ownedResults = new();

    /// <summary>
    /// A call whose result the caller may own: every value the callee returns is a local it owns (fed only by fresh
    /// arrays, never stored, captured or aliased). The callee does not release what it returns, so the buffer moves
    /// out to the caller, which recycles it; `src = source(n)` then stops allocating once warm.
    /// </summary>
    bool OwnedResult(Syntax.Expr e)
    {
        if (Unwrap(e) is not CallExpr { Sym: MethodSym m, Block: null } || m.Ret is not { Kind: "array", Elem: { } el } || !Unmanaged.Contains(el.Cs)) return false;
        if (ownedResults.TryGetValue(m, out var known)) return known;
        if (m.Decl is not { HasBody: true } d || m.IsAsync || m.UsesYield || m.IsCtor) return ownedResults[m] = false;
        ownedResults[m] = false;   // recursion: not owned while being analysed
        var tails = new HashSet<Syntax.Expr>();
        TailExprs(d.Body, tails);
        var results = tails.Concat(WalkStmts(d.Body).OfType<ReturnStmt>().Select(r => r.Value)).ToList();
        if (results.Count == 0 || results.Any(r => r is not NameExpr { Sym: LocalSym })) return false;
        foreach (var l in results.Select(r => (LocalSym)((NameExpr)r!).Sym!).Distinct())
        {
            var assigns = WalkStmts(d.Body).OfType<Assign>().Where(a => Assigned(a) == l).ToList();
            if (assigns.Count == 0 || assigns.Any(a => a.Op != "=" || !Fresh(a.Value))) return false;
            if (WalkStmts(d.Body).OfType<MultiAssign>().Any(ma => ma.Locals.Contains(l))) return false;
            var cands = new Dictionary<LocalSym, string> { [l] = el.Cs };
            CheckUses(d.Body, l, cands, new(), tails);
            if (!cands.ContainsKey(l)) return false;
        }
        return ownedResults[m] = true;
    }

    static LocalSym? Assigned(Assign a) => a.Declares ?? (a.Target as NameExpr)?.Sym as LocalSym;

    static bool Fresh(Syntax.Expr e) => Unwrap(e) is NewExpr { Type: { Kind: "array" }, Block: null, AsCall: null, Args.Count: 1 or 2 } ne && ne.Args.All(x => x.Mod == "" && x.Name == null);

    static void Drop(Dictionary<LocalSym, string> cands, Dictionary<LocalSym, (Node, string)> reasons, LocalSym l, Node? at, string why)
    {
        if (cands.Remove(l) || !reasons.ContainsKey(l)) reasons.TryAdd(l, (at!, why));
    }

    /// <summary>Expressions that become the method's result (last statement, through if/case branches).</summary>
    static void TailExprs(List<Stmt> body, HashSet<Syntax.Expr> into)
    {
        if (body.Count == 0) return;
        switch (body[^1])
        {
            case ExprStmt es: into.Add(es.Expr); break;
            case IfStmt i: foreach (var b in i.Bodies) TailExprs(b, into); if (i.Else != null) TailExprs(i.Else, into); break;
            case CaseStmt c: foreach (var w in c.Whens) TailExprs(w.Body, into); if (c.Else != null) TailExprs(c.Else, into); break;
        }
    }

    void CheckUses(List<Stmt> body, LocalSym l, Dictionary<LocalSym, string> cands, Dictionary<LocalSym, (Node, string)> reasons, HashSet<Syntax.Expr> tailValues)
    {
        int mentions = 0, allowed = 0;
        foreach (var e in Walk(body))
        {
            switch (e)
            {
                case NameExpr { Sym: LocalSym x } when x == l: mentions++; break;
                case IndexExpr { Target: NameExpr { Sym: LocalSym x } } when x == l: allowed++; break;
                case CallExpr { Res: "ext", Args.Count: 0, Block: null, Recv: NameExpr { Sym: LocalSym x } } c when x == l && Binder.Mangle(c.Name) is "size" or "length": allowed++; break;
                case CallExpr { Recv: NameExpr { Sym: LocalSym x }, Name: "Length" } when x == l: allowed++; break;
                case LambdaExpr lam when WalkExpr(lam).OfType<NameExpr>().Any(n => n.Sym == l): Drop(cands, reasons, l, lam, "it is captured by a block"); return;
                case CallExpr { Block: { } blk } cb when Walk(blk.Body).OfType<NameExpr>().Any(n => n.Sym == l): Drop(cands, reasons, l, cb, "it is captured by a block"); return;
                case CallExpr { Sym: MethodSym m, Block: null } call:
                    for (int k = 0; k < call.Args.Count; k++)
                        if (call.Args[k] is { Value: NameExpr { Sym: LocalSym x } } arg && x == l)
                        {
                            if (arg.Mod == "" && arg.Name == null && k < m.Params.Count && m.Params[k].Own) allowed++;   // moved into the callee
                            else if (arg.Mod == "" && arg.Name == null && Borrows(m, k, new())) allowed++;
                            else { Drop(cands, reasons, l, call, $"`{m.Name}` may keep a reference to it"); return; }
                        }
                    break;
            }
        }
        foreach (var s in WalkStmts(body))
        {
            switch (s)
            {
                case ForStmt { Iter: NameExpr { Sym: LocalSym it } } f when it == l:
                    // the loop keeps reading this array: it must not change hands until the loop ends
                    if (WalkStmts(f.Body).Any(x => x is Assign a2 && (Assigned(a2) == l || a2.Value is NameExpr { Sym: LocalSym v2 } && v2 == l))) { Drop(cands, reasons, l, f, "it is moved while a loop iterates it"); return; }
                    allowed++;
                    break;
                case Assign { Target: NameExpr { Sym: LocalSym t } } a when t == l && a.Op == "=":   // the local being (re)bound
                    allowed += InPlaceMap(a, l) != null ? 2 : 1;   // `l = l.map { }` also reads l, in place
                    break;
                case Assign { Value: NameExpr { Sym: LocalSym v } } a when v == l && Assigned(a) is { } dst && dst != l && cands.ContainsKey(dst): allowed++; break;   // a move
                case ReturnStmt { Value: NameExpr { Sym: LocalSym r } } when r == l: allowed++; break;
            }
        }
        foreach (var t in tailValues) if (t is NameExpr { Sym: LocalSym r } && r == l) allowed++;
        if (mentions != allowed) Drop(cands, reasons, l, null, "it is stored, passed or aliased where it may outlive the local");
    }

    readonly Dictionary<(MethodSym, int), bool> borrows = new();

    /// <summary>
    /// Parameter k of m only borrows its array: inside m it is indexed, sized, iterated, or lent to another borrowing
    /// parameter; never assigned, stored, returned or captured. Recursion is assumed to borrow while being analysed.
    /// </summary>
    bool Borrows(MethodSym m, int k, HashSet<(MethodSym, int)> visiting)
    {
        if (borrows.TryGetValue((m, k), out var known)) return known;
        if (m.Decl is not { HasBody: true } d || m.IsAsync || m.UsesYield || k >= m.Params.Count || m.Params[k].Splat || m.Params[k].Block) return borrows[(m, k)] = false;
        if (!visiting.Add((m, k))) return true;
        string name = m.Params[k].Name;
        bool IsParam(Syntax.Expr e) => e is NameExpr { Sym: LocalSym x } && x.Name == name;
        int mentions = 0, allowed = 0;
        bool ok = true;
        foreach (var e in Walk(d.Body))
        {
            switch (e)
            {
                case NameExpr n when IsParam(n): mentions++; break;
                case IndexExpr ix when IsParam(ix.Target): allowed++; break;
                case CallExpr { Res: "ext", Args.Count: 0, Block: null } c when c.Recv != null && IsParam(c.Recv) && Binder.Mangle(c.Name) is "size" or "length": allowed++; break;
                case CallExpr { Name: "Length" } c when c.Recv != null && IsParam(c.Recv): allowed++; break;
                case LambdaExpr lam when WalkExpr(lam).Any(IsParam): ok = false; break;
                case CallExpr { Block: { } blk } when Walk(blk.Body).Any(IsParam): ok = false; break;
                case CallExpr { Sym: MethodSym callee, Block: null } call:
                    for (int j = 0; j < call.Args.Count; j++)
                        if (call.Args[j] is { Value: { } v } arg && IsParam(v))
                        {
                            if (arg.Mod == "" && arg.Name == null && Borrows(callee, j, visiting)) allowed++;
                            else ok = false;
                        }
                    break;
            }
        }
        foreach (var s in WalkStmts(d.Body))
        {
            if (s is ForStmt { Iter: { } it } && IsParam(it)) allowed++;
            if (s is Assign a && IsParam(a.Target)) ok = false;
            if (s is MultiAssign ma && ma.Names.Contains(name)) ok = false;
        }
        visiting.Remove((m, k));
        return borrows[(m, k)] = ok && mentions == allowed;
    }

    // ---- lowering hooks ----

    string? OwnedElem(LocalSym? l) => l != null && !inParallelGen && owned.TryGetValue(l, out var el) ? el : null;

    /// <summary>`a = Array(T).new(n[, v])` for an owned a: a pooled buffer, after releasing a's previous array.</summary>
    bool TryOwnedAssign(List<MStmt> o, Assign a)
    {
        if (Assigned(a) is not { } l || OwnedElem(l) is not { } el) return false;
        if (a.Declares == null && TryInPlaceMap(o, a, l)) return true;
        MExpr value = Fresh(a.Value) && Unwrap(a.Value) is NewExpr ne
            ? new MCall { Target = new MCode($"global::Alice.Runtime.Own<{el}>"), Name = "Rent", Args = ne.Args.Select(x => Expr(x.Value!)).ToList() }
            : Expr(a.Value);
        if (releaseBefore.Contains(a))
        {
            if (WalkExpr(a.Value).Any(n => n is NameExpr { Sym: LocalSym x } && x == l))
            {
                string tmp = Temp("own");
                o.Add(new MDecl { Name = tmp, Init = value });
                value = new MCode(tmp);
            }
            o.Add(Release(l, el));
        }
        if (a.Declares != null && !hoisted.Contains(a.Declares))
            o.Add(new MDecl
            {
                Name = l.CsName, Init = value,
                RentLength = Unwrap(a.Value) is NewExpr { Args: [{ Value: NameExpr { Sym: LocalSym n } }] } && value is MCall { Name: "Rent" } ? n.CsName : null,
            });
        else o.Add(new MAssign { Target = new MCode(l.CsName), Value = value });
        return true;
    }

    static MStmt Release(LocalSym l, string el) => new MExprStmt { E = new MCall { Target = new MCode($"global::Alice.Runtime.Own<{el}>"), Name = "Release", Args = { new MCode(l.CsName) } } };

    /// <summary>Before an exit: release every owned local live there except the one being returned.</summary>
    void ReleaseOwnedAt(List<MStmt> o, Stmt exit, ref MExpr? value, Syntax.Expr? returned)
    {
        if (inParallelGen || !releaseAt.TryGetValue(exit, out var live) || live.Count == 0) return;
        var keep = returned is NameExpr { Sym: LocalSym r } ? r : null;
        var drop = live.Where(l => l != keep && owned.ContainsKey(l)).ToList();
        if (drop.Count == 0) return;
        if (value != null && returned != null && WalkExpr(returned).Any(n => n is NameExpr { Sym: LocalSym x } && drop.Contains(x)))
        {
            string tmp = Temp("ret");
            o.Add(new MDecl { Name = tmp, Init = value });
            value = new MCode(tmp);
        }
        foreach (var l in drop) o.Add(Release(l, owned[l]));
    }

    IEnumerable<MStmt> ReleaseOwnedAtEnd() => inParallelGen ? [] : releaseAtEnd.Where(owned.ContainsKey).Select(l => Release(l, owned[l]));

    /// <summary>Owned locals passed to an `own` parameter: the call takes their array.</summary>
    static IEnumerable<LocalSym> MovedIntoCalls(IEnumerable<Syntax.Expr> exprs, Dictionary<LocalSym, string> cands)
    {
        foreach (var e in exprs)
            if (e is CallExpr { Sym: MethodSym m } c)
                for (int k = 0; k < c.Args.Count && k < m.Params.Count; k++)
                    if (m.Params[k].Own && c.Args[k].Value is NameExpr { Sym: LocalSym l } && cands.ContainsKey(l)) yield return l;
    }

    /// <summary>An argument to an `own` parameter must give up its array: an owned local (moved) or a fresh array.</summary>
    void CheckOwnArguments(List<Stmt> body)
    {
        foreach (var e in Walk(body))
            if (e is CallExpr { Sym: MethodSym m } c)
                for (int k = 0; k < c.Args.Count && k < m.Params.Count; k++)
                    if (m.Params[k].Own && c.Args[k].Value is { } v && !Fresh(v) && !OwnedResult(v) && !(v is NameExpr { Sym: LocalSym l } && owned.ContainsKey(l)))
                        throw new CompileException($"{File}:{c.Line}:{Math.Max(1, c.Col)}: owned value expected for `own {m.Params[k].Name}` of `{m.Name}`: pass an owned local (it moves) or a fresh `Array(T).new`, not an array others may still reference");
    }

    // ---- move-state flow analysis ----

    sealed class OwnState
    {
        public HashSet<LocalSym> Live = new();    // definitely holds an array it owns
        public HashSet<LocalSym> Moved = new();   // may have been moved out
        public bool Reachable = true;

        public OwnState Copy() => new() { Live = new(Live), Moved = new(Moved), Reachable = Reachable };

        public static OwnState Join(OwnState a, OwnState b)
        {
            if (!a.Reachable) return b.Copy();
            if (!b.Reachable) return a.Copy();
            var j = a.Copy();
            j.Live.IntersectWith(b.Live);
            j.Moved.UnionWith(b.Moved);
            return j;
        }

        public bool SameAs(OwnState o) => Reachable == o.Reachable && Live.SetEquals(o.Live) && Moved.SetEquals(o.Moved);
    }

    sealed class OwnFlow(Dictionary<LocalSym, string> cands, Tail methodTail)
    {
        public readonly List<(LocalSym, Node)> Violations = new();
        public readonly Dictionary<Assign, bool> ReleaseBefore = new();
        public readonly Dictionary<Stmt, HashSet<LocalSym>> ReleaseAt = new();
        readonly Stack<(List<OwnState> Breaks, List<OwnState> Nexts)> loops = new();

        void Use(Syntax.Expr? e, OwnState s, Node at)
        {
            if (e == null || !s.Reachable) return;
            foreach (var n in WalkExpr(e))
                if (n is NameExpr { Sym: LocalSym l } && cands.ContainsKey(l) && s.Moved.Contains(l)) Violations.Add((l, at));
        }

        void UseAll(Stmt st, OwnState s)
        {
            foreach (var n in Walk([st]))
                if (n is NameExpr { Sym: LocalSym l } && cands.ContainsKey(l) && s.Moved.Contains(l)) Violations.Add((l, st));
        }

        void Exit(Stmt st, OwnState s)
        {
            var live = new HashSet<LocalSym>(s.Live);
            ReleaseAt[st] = ReleaseAt.TryGetValue(st, out var prev) ? new HashSet<LocalSym>(prev.Intersect(live)) : live;
        }

        public OwnState Block(List<Stmt> body, OwnState s, Tail tail)
        {
            for (int i = 0; i < body.Count; i++) s = Stmt(body[i], s, i == body.Count - 1 ? tail : Tail.None);
            return s;
        }

        /// <summary>A statement, then the moves it makes into `own` parameters: those locals are gone afterwards.</summary>
        OwnState Stmt(Stmt st, OwnState s, Tail tail)
        {
            s = StmtCore(st, s, tail);
            if (!s.Reachable) return s;
            foreach (var l in MovedIntoCalls(Walk([st]), cands))
            {
                if (s.Live.Contains(l) || !s.Moved.Contains(l)) s = s.Copy();
                s.Live.Remove(l);
                s.Moved.Add(l);
            }
            return s;
        }

        OwnState StmtCore(Stmt st, OwnState s, Tail tail)
        {
            if (!s.Reachable) return s;
            switch (st)
            {
                case Assign a when Assigned(a) is { } l && cands.ContainsKey(l):
                    {
                        var src = a.Value is NameExpr { Sym: LocalSym v } && cands.ContainsKey(v) ? v : null;
                        Use(a.Value, s, a);
                        bool live = s.Live.Contains(l);
                        ReleaseBefore[a] = ReleaseBefore.TryGetValue(a, out var was) ? was && live : live;
                        s = s.Copy();
                        if (src != null) { s.Live.Remove(src); s.Moved.Add(src); }
                        s.Live.Add(l);
                        s.Moved.Remove(l);
                        return s;
                    }
                case ExprStmt es when tail == Tail.Return && methodTail == Tail.Return:
                    Use(es.Expr, s, es);
                    Exit(es, s);
                    return new OwnState { Reachable = false };
                case ReturnStmt r:
                    Use(r.Value, s, r);
                    Exit(r, s);
                    return new OwnState { Reachable = false };
                case RaiseStmt rs:
                    Use(rs.Value, s, rs);
                    return new OwnState { Reachable = false };
                case IfStmt i:
                    {
                        OwnState? outS = null;
                        var cur = s;
                        for (int k = 0; k < i.Conds.Count; k++)
                        {
                            Use(i.Conds[k], cur, i);
                            var b = Block(i.Bodies[k], cur.Copy(), tail);
                            outS = outS == null ? b : OwnState.Join(outS, b);
                        }
                        var e = i.Else != null ? Block(i.Else, cur.Copy(), tail) : cur.Copy();
                        return outS == null ? e : OwnState.Join(outS, e);
                    }
                case CaseStmt c:
                    {
                        Use(c.Subject, s, c);
                        foreach (var w in c.Whens) { foreach (var v in w.Patterns) Use(v, s, c); Use(w.Guard, s, c); }
                        OwnState? outS = null;
                        foreach (var w in c.Whens)
                        {
                            var b = Block(w.Body, s.Copy(), tail);
                            outS = outS == null ? b : OwnState.Join(outS, b);
                        }
                        var e = c.Else != null ? Block(c.Else, s.Copy(), tail) : s.Copy();
                        return outS == null ? e : OwnState.Join(outS, e);
                    }
                case WhileStmt w:
                    return Loop(s, head => Use(w.Cond, head, w), w.Body, exitsAtHead: true);
                case ForStmt f:
                    Use(f.Iter, s, f);
                    return Loop(s, _ => { }, f.Body, exitsAtHead: true);
                case BreakStmt when loops.Count > 0:
                    loops.Peek().Breaks.Add(s.Copy());
                    return new OwnState { Reachable = false };
                case NextStmt { Value: null } when loops.Count > 0:
                    loops.Peek().Nexts.Add(s.Copy());
                    return new OwnState { Reachable = false };
                case BeginStmt { Scope: true } region:
                    {
                        // end of the region: its owned arrays still live here die now, the rest of the method never sees them
                        var end = Block(region.Body, s, tail);
                        if (!end.Reachable) return end;
                        var inside = WalkStmts(region.Body).OfType<Assign>().Select(a => a.Declares).OfType<LocalSym>().Where(cands.ContainsKey).ToHashSet();
                        var dying = new HashSet<LocalSym>(end.Live.Where(inside.Contains));
                        ReleaseAt[region] = ReleaseAt.TryGetValue(region, out var prev) ? new HashSet<LocalSym>(prev.Intersect(dying)) : dying;
                        end = end.Copy();
                        end.Live.ExceptWith(inside);
                        return end;
                    }
                case BeginStmt { Rescues.Count: 0, Ensure: null } b:
                    return Block(b.Body, s, tail);
                default:
                    UseAll(st, s);
                    return s;
            }
        }

        OwnState Loop(OwnState entry, Action<OwnState> head, List<Stmt> body, bool exitsAtHead)
        {
            var at = entry.Copy();
            OwnState exit = entry;
            for (int iter = 0; iter < 16; iter++)
            {
                head(at);
                loops.Push((new(), new()));
                var end = Block(body, at.Copy(), Tail.None);
                var (breaks, nexts) = loops.Pop();
                var back = nexts.Aggregate(end, OwnState.Join);
                var next = OwnState.Join(entry, back);
                exit = breaks.Aggregate(exitsAtHead ? next : back, OwnState.Join);
                if (next.SameAs(at)) break;
                at = next;
            }
            return exit;
        }
    }
}
