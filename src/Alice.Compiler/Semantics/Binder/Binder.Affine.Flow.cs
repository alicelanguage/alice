using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// The per-body half of the affine analysis (ADR 0037): a small control-flow graph over the bound AST, backward
/// liveness of arena locals, then a forward move-state flow with the <c>OwnState</c> shape of
/// <c>Lowerer.Ownership.cs</c> (owned / moved, joined over branches and loops) extended with packed, kept and
/// borrowed states. Decisions:
/// <list type="bullet">
/// <item>A `case` arm Takes its scrutinee (reads the fields, frees the node) when the scrutinee is owned, not used again
/// on any path from the arm, has no live borrowed field, and the arm has no guard; otherwise it Borrows (fields read in
/// place; they stay borrowed from the scrutinee until it dies, when a Drop frees its slot and the fields own their
/// nodes). An arm on a fresh value (`case f(x)`) Takes.</item>
/// <item>A local passed to an Alice parameter, built into a node, returned or assigned moves (unless it holds a packed
/// value, which copies); a `borrow` parameter lends it (BorrowArg).</item>
/// <item>A use after a move: under `affine enum`, a read (a `case`, or a `borrow` argument) after a move into a call,
/// node or local turns that move into a Keep (Bend 2's counted redirect: the receiver and the local each hold a count;
/// the local's last read drops its count); any other use after a move is AL0242. For an inferred type, any use after a
/// move demotes it to records.</item>
/// <item>Consuming a `borrow` parameter, or a field read from one, is AL0243 (`affine enum`) or a demotion.</item>
/// </list>
/// </summary>
public sealed partial class Binder
{
    enum UseKind { Read, BorrowArg, Case, Move, Return, Assign, Escape }

    enum EvKind { Use, Def, Arm, Anchor, Escape }

    enum DefKind { Owned, Packed, FromUse, Borrowed }

    enum StK { Undefined, Owned, Packed, Kept, Borrowed, BorrowedParam, Moved, MaybeMoved }

    readonly record struct St(StK K, LocalSym? Root = null, Node? MovedAt = null, bool Keepable = false);

    sealed class Ev
    {
        public EvKind K;
        public LocalSym? L;
        public NameExpr? N;
        public Node At = null!;
        public UseKind U;
        public string Reason = "";
        public DefKind D;
        public Ev? FromUse;
        public When? W;
        public bool Fresh;
        public List<LocalSym> Binds = [];
        public ArenaLayout? Lay;
        public bool Before;        // anchor: DropsBefore (branch entry)
        public HashSet<LocalSym> LiveAfter = [];
        public St Pre;             // use: state before the use (read by a FromUse def)
    }

    sealed class Blk
    {
        public readonly List<Ev> Evs = [];
        public readonly List<Blk> Succ = [], Pred = [];
        public HashSet<LocalSym> LiveIn = [], LiveOut = [];
        public Dictionary<LocalSym, St>? In;
    }

    readonly record struct Sink(UseKind U, string Reason = "")
    {
        public static readonly Sink Read = new(UseKind.Read);
        public static readonly Sink Move = new(UseKind.Move);
        public static readonly Sink Return = new(UseKind.Return);
        public static Sink Esc(string why) => new(UseKind.Escape, why);
    }

    // ---- builder state (one body at a time) ----

    sealed class Flow
    {
        public readonly List<Blk> Blocks = [];
        public Blk Cur = null!, Exit = null!;
        public readonly Stack<(Blk Break, Blk Next)> Loops = new();
        public readonly List<LocalSym> Tracked = [];
        public readonly Dictionary<LocalSym, ParamSym> Params = [];
        public string File = "";

        public Blk New() { var b = new Blk(); Blocks.Add(b); return b; }
        public static void Edge(Blk a, Blk b) { a.Succ.Add(b); b.Pred.Add(a); }
        public Ev Add(Ev e) { Cur.Evs.Add(e); return e; }
    }

    Flow fl = null!;

    bool Tracked(LocalSym l)
    {
        if (Cand(l.Type) == null) return false;
        if (!fl.Tracked.Contains(l)) fl.Tracked.Add(l);
        return true;
    }

    void FlowBody(Body body, string f)
    {
        fl = new Flow { File = f };
        var entry = fl.New();
        fl.Exit = fl.New();
        fl.Cur = entry;
        var m = body.Method?.Sym as MethodSym;
        if (m != null)
            foreach (var p in m.Params)
                if (p.Local != null && Tracked(p.Local))
                {
                    fl.Params[p.Local] = p;
                    fl.Add(new Ev { K = EvKind.Def, L = p.Local, At = body.Method!, D = Lends(p) ? DefKind.Borrowed : DefKind.Owned });
                }
        if (body.Stmts.Count > 0) fl.Add(new Ev { K = EvKind.Anchor, At = body.Stmts[0], Before = true });
        var tail = m != null && !m.Ret.IsVoid && !m.IsCtor ? Sink.Return : (Sink?)null;
        Stmts(body.Stmts, tail);
        Flow.Edge(fl.Cur, fl.Exit);
        if (fl.Tracked.Count == 0 && !fl.Blocks.Any(b => b.Evs.Any(e => e.K == EvKind.Escape))) return;
        Liveness();
        Forward(entry);
    }

    // ---- CFG construction ----

    void Stmts(List<Stmt> list, Sink? tail)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            bool last = i == list.Count - 1;
            if (Statement(s, last ? tail : null)) fl.Add(new Ev { K = EvKind.Anchor, At = s });
        }
    }

    /// <summary>Adds s; false when s ends its path (return, break, next, raise), which anchors itself.</summary>
    bool Statement(Stmt s, Sink? tail)
    {
        switch (s)
        {
            case ExprStmt es:
                Expr(es.Expr, tail ?? Sink.Read);
                return true;
            case LocalDecl ld:
                if (ld.Value != null) AssignTo(ld.Local, ld.Value, s);
                return true;
            case Assign a:
                if (a.Target is NameExpr { Sym: LocalSym t } && a.Op == "=") AssignTo(t, a.Value, s);
                else if (a.Target is NameExpr { Sym: LocalSym t2 })
                {
                    Expr(a.Value, Sink.Esc($"operand of `{a.Op}`"));
                    if (Tracked(t2)) fl.Add(new Ev { K = EvKind.Use, L = t2, N = (NameExpr)a.Target, At = a.Target, U = UseKind.Escape, Reason = $"operand of `{a.Op}`" });
                }
                else
                {
                    Children(a.Target, Sink.Esc("used to address a field or element"));
                    Expr(a.Value, Sink.Esc(a.Target is IndexExpr ? "stored in a collection" : "stored in a field"));
                }
                return true;
            case MultiAssign ma:
                {
                    var uses = new List<Ev?>();
                    for (int i = 0; i < ma.Values.Count; i++)
                    {
                        bool paired = ma.Values.Count == ma.Locals.Count;
                        uses.Add(paired ? AssignValue(ma.Values[i], ma.Locals[i]) : null);
                        if (!paired) Expr(ma.Values[i], Sink.Esc("destructured"));
                    }
                    for (int i = 0; i < ma.Locals.Count; i++)
                        if (Tracked(ma.Locals[i])) fl.Add(DefFrom(ma.Locals[i], i < uses.Count ? uses[i] : null, i < ma.Values.Count && ma.Values.Count == ma.Locals.Count ? ma.Values[i] : null, s));
                    return true;
                }
            case IfStmt ifs:
                {
                    var join = fl.New();
                    for (int i = 0; i < ifs.Conds.Count; i++)
                    {
                        Expr(ifs.Conds[i], Sink.Read);
                        var then = fl.New();
                        var next = fl.New();
                        Flow.Edge(fl.Cur, then);
                        Flow.Edge(fl.Cur, next);
                        fl.Cur = then;
                        Branch(ifs.Bodies[i], tail);
                        Flow.Edge(fl.Cur, join);
                        fl.Cur = next;
                    }
                    if (ifs.Else != null) Branch(ifs.Else, tail);
                    Flow.Edge(fl.Cur, join);
                    fl.Cur = join;
                    return true;
                }
            case WhileStmt ws:
                {
                    var head = fl.New();
                    var body = fl.New();
                    var exit = fl.New();
                    Flow.Edge(fl.Cur, head);
                    fl.Cur = head;
                    Expr(ws.Cond, Sink.Read);
                    Flow.Edge(fl.Cur, body);
                    Flow.Edge(fl.Cur, exit);
                    fl.Loops.Push((exit, head));
                    fl.Cur = body;
                    Stmts(ws.Body, null);
                    Flow.Edge(fl.Cur, head);
                    fl.Loops.Pop();
                    fl.Cur = exit;
                    return true;
                }
            case ForStmt fs:
                {
                    Expr(fs.Iter, Sink.Esc("iterated"));
                    if (fs.Par || fs.Gpu || fs.Await)
                    {
                        Opaque(fs.Body, "used in a parallel or async loop body");
                        return true;
                    }
                    var head = fl.New();
                    var body = fl.New();
                    var exit = fl.New();
                    Flow.Edge(fl.Cur, head);
                    Flow.Edge(head, body);
                    Flow.Edge(head, exit);
                    fl.Loops.Push((exit, head));
                    fl.Cur = body;
                    foreach (var l in fs.Locals) if (Tracked(l)) fl.Add(new Ev { K = EvKind.Def, L = l, At = fs, D = DefKind.Owned });
                    Stmts(fs.Body, null);
                    Flow.Edge(fl.Cur, head);
                    fl.Loops.Pop();
                    fl.Cur = exit;
                    return true;
                }
            case CaseStmt cs:
                Case(cs, tail);
                return true;
            case BeginStmt bs:
                {
                    if (bs.Lock != null) Expr(bs.Lock, Sink.Esc("used as a lock"));
                    var start = fl.Cur;
                    Stmts(bs.Body, tail);
                    if (bs.Rescues.Count > 0)
                    {
                        var join = fl.New();
                        Flow.Edge(fl.Cur, join);
                        foreach (var r in bs.Rescues)
                        {
                            Opaque(r.Body, "reaches `rescue`");
                            var rb = fl.New();
                            Flow.Edge(start, rb);
                            fl.Cur = rb;
                            Stmts(r.Body, tail);
                            Flow.Edge(fl.Cur, join);
                        }
                        fl.Cur = join;
                    }
                    if (bs.Ensure != null)
                    {
                        Opaque(bs.Ensure, "reaches `ensure`");
                        Stmts(bs.Ensure, null);
                    }
                    return true;
                }
            case UsingStmt us:
                Expr(us.Value, Sink.Esc("used by `using`"));
                if (us.Local != null && Tracked(us.Local)) fl.Add(new Ev { K = EvKind.Def, L = us.Local, At = us, D = DefKind.Owned });
                Stmts(us.Body, tail);
                return true;
            case ReturnStmt rs:
                if (rs.Value != null) Expr(rs.Value, Sink.Return);
                fl.Add(new Ev { K = EvKind.Anchor, At = rs });
                Flow.Edge(fl.Cur, fl.Exit);
                fl.Cur = fl.New();
                return false;
            case BreakStmt:
                fl.Add(new Ev { K = EvKind.Anchor, At = s });
                if (fl.Loops.Count > 0) Flow.Edge(fl.Cur, fl.Loops.Peek().Break);
                fl.Cur = fl.New();
                return false;
            case NextStmt ns:
                if (ns.Value != null) Expr(ns.Value, Sink.Esc("passed to `next`"));
                fl.Add(new Ev { K = EvKind.Anchor, At = s });
                if (fl.Loops.Count > 0) Flow.Edge(fl.Cur, fl.Loops.Peek().Next);
                fl.Cur = fl.New();
                return false;
            case RaiseStmt rs:
                Expr(rs.Value, Sink.Esc("raised"));
                fl.Cur = fl.New();
                return false;
            case RetryStmt:
                fl.Cur = fl.New();
                return false;
            default:
                Opaque([s], "used by an unsupported statement");
                return true;
        }
    }

    /// <summary>An `if`/`else` branch: Drops of locals that die on entering it, then its statements.</summary>
    void Branch(List<Stmt> body, Sink? tail)
    {
        if (body.Count > 0) fl.Add(new Ev { K = EvKind.Anchor, At = body[0], Before = true });
        Stmts(body, tail);
    }

    void AssignTo(LocalSym? target, Expr value, Node at)
    {
        if (target == null) { Expr(value, Sink.Read); return; }
        var use = AssignValue(value, target);
        if (Tracked(target)) fl.Add(DefFrom(target, use, value, at));
    }

    /// <summary>Evaluates the value of an assignment to target; returns the use when it is a plain local (aliasing).</summary>
    Ev? AssignValue(Expr value, LocalSym target)
    {
        if (Cand(target.Type) == null)
        {
            Expr(value, Cand(value.Type) != null ? Sink.Esc($"assigned to `{target.Name}` of type {target.Type.Cs}") : Sink.Read);
            return null;
        }
        if (value is NameExpr { Sym: LocalSym src } n && Tracked(src))
            return fl.Add(new Ev { K = EvKind.Use, L = src, N = n, At = n, U = UseKind.Assign });
        Expr(value, Sink.Move);
        return null;
    }

    Ev DefFrom(LocalSym l, Ev? use, Expr? value, Node at) => new()
    {
        K = EvKind.Def, L = l, At = at, FromUse = use,
        D = use != null ? DefKind.FromUse : value != null && PackedValue(value) ? DefKind.Packed : DefKind.Owned,
    };

    /// <summary>A value that is statically a packed variant (no fields, or one field of at most 32 bits).</summary>
    bool PackedValue(Expr e) => e switch
    {
        TernaryExpr t => PackedValue(t.A) && PackedValue(t.B),
        ConstExpr { Res: "type" } c => c.Type.Sym is { Kind: "variant" } v && PackedVariant(v),
        NewExpr { AsCall: null } ne => ne.Type.Sym is { Kind: "variant" } v && PackedVariant(v),
        _ => false,
    };

    bool PackedVariant(TypeSym v) =>
        v.FieldOrder.Count == 0 || v.FieldOrder.Count == 1 && v.Fields[v.FieldOrder[0]].Type is var t && Cand(t) == null && Fits32(t);

    void Case(CaseStmt c, Sink? tail)
    {
        LocalSym? scrut = null;
        ArenaLayout? lay = c.Subject != null ? Cand(c.Subject.Type) : null;
        bool fresh = false;
        if (c.Subject is NameExpr { Sym: LocalSym x } n && Tracked(x))
        {
            scrut = x;
            lay = Cand(x.Type);
            fl.Add(new Ev { K = EvKind.Use, L = x, N = n, At = n, U = UseKind.Case });
        }
        else if (lay != null && c.Subject is CallExpr or NewExpr or ConstExpr or TernaryExpr or StmtExpr)
        {
            Expr(c.Subject, Sink.Move);
            fresh = true;
        }
        else if (c.Subject != null) Expr(c.Subject, lay != null ? Sink.Esc("matched without a local") : Sink.Read);
        var pre = fl.Cur;
        var join = fl.New();
        var entries = c.Whens.Select(_ => fl.New()).ToList();
        foreach (var e in entries) Flow.Edge(pre, e);
        Blk? elseBlk = c.Else != null ? fl.New() : null;
        if (elseBlk != null) Flow.Edge(pre, elseBlk);
        else if (!c.Exhaustive) Flow.Edge(pre, join);
        for (int i = 0; i < c.Whens.Count; i++)
        {
            var w = c.Whens[i];
            fl.Cur = entries[i];
            var arm = new Ev { K = EvKind.Arm, W = w, At = w, L = scrut, Fresh = fresh, Lay = lay };
            fl.Add(arm);
            foreach (var p in w.Patterns) Pattern(p, arm, scrut, lay);
            fl.Add(new Ev { K = EvKind.Anchor, At = w });
            if (w.Guard != null)
            {
                Expr(w.Guard, Sink.Read);
                var body = fl.New();
                var fallback = i + 1 < entries.Count ? entries[i + 1] : elseBlk ?? join;
                Flow.Edge(fl.Cur, body);
                Flow.Edge(fl.Cur, fallback);
                // a failed guard falls to the next arm, which matches the scrutinee again: it is live until then
                if (scrut != null && fallback != join && fallback.Evs.Count == 0)
                    fallback.Evs.Add(new Ev { K = EvKind.Use, L = scrut, At = w, U = UseKind.Read });
                fl.Cur = body;
            }
            Stmts(w.Body, tail);
            Flow.Edge(fl.Cur, join);
        }
        if (elseBlk != null)
        {
            fl.Cur = elseBlk;
            Branch(c.Else!, tail);
            Flow.Edge(fl.Cur, join);
        }
        fl.Cur = join;
    }

    void Pattern(Expr p, Ev arm, LocalSym? scrut, ArenaLayout? lay)
    {
        switch (p.Res)
        {
            case "destructure" when p.Sym is Destructure d:
                foreach (var part in d.Parts)
                {
                    if (part.Eq != null) Expr(part.Eq, Sink.Esc("compared in a `when` pattern"));
                    if (part.Bind != null && Tracked(part.Bind)) arm.Binds.Add(part.Bind);
                }
                return;
            case "type" or "wild":
                return;
            case "bindpat" when p is BindPatternExpr bp:
                if (lay != null) SubjectEscape(scrut, arm, lay, $"bound by `when {bp.Name}`");
                if (bp.Local != null && Tracked(bp.Local)) fl.Add(new Ev { K = EvKind.Def, L = bp.Local, At = p, D = DefKind.Owned });
                return;
            default:
                if (lay != null) SubjectEscape(scrut, arm, lay, "compared in a `when` pattern");
                if (p is ArrayPatternExpr or HashPatternExpr) Children(p, Sink.Read);
                else Expr(p, lay != null ? Sink.Esc("compared in a `when` pattern") : Sink.Read);
                if (p is ArrayPatternExpr ap) foreach (var b in ap.Binds.Append(ap.RestSym)) if (b != null && Tracked(b)) fl.Add(new Ev { K = EvKind.Def, L = b, At = p, D = DefKind.Owned });
                if (p is HashPatternExpr hp) foreach (var b in hp.Binds) if (b != null && Tracked(b)) fl.Add(new Ev { K = EvKind.Def, L = b, At = p, D = DefKind.Owned });
                return;
        }
    }

    void SubjectEscape(LocalSym? scrut, Ev arm, ArenaLayout lay, string why)
    {
        if (scrut != null) fl.Add(new Ev { K = EvKind.Use, L = scrut, At = arm.At, U = UseKind.Escape, Reason = why });
        else fl.Add(new Ev { K = EvKind.Escape, At = arm.At, Lay = lay, Reason = why });
    }

    /// <summary>Statements the flow does not model (rescue bodies, parallel loops): every arena value in them escapes.</summary>
    void Opaque(IEnumerable<Node> nodes, string why)
    {
        AstWalk.Each(nodes, n =>
        {
            if (n is NameExpr { Sym: LocalSym l } ne && Tracked(l))
                fl.Add(new Ev { K = EvKind.Use, L = l, N = ne, At = ne, U = UseKind.Escape, Reason = why });
            else if (n is Expr e && n is not NameExpr && Cand(e.Type) is { } lay)
                fl.Add(new Ev { K = EvKind.Escape, At = e, Lay = lay, Reason = why });
        });
    }

    // ---- expressions ----

    void Expr(Expr? e, Sink sink)
    {
        if (e == null) return;
        var lay = Cand(e.Type);
        switch (e)
        {
            case NameExpr { Sym: LocalSym l } n:
                if (Tracked(l)) fl.Add(new Ev { K = EvKind.Use, L = l, N = n, At = n, U = sink.U, Reason = sink.Reason });
                return;
            case NameExpr:
                Temp(lay, e, sink);
                return;
            case LitExpr or RegexExpr:
                return;
            case CallExpr c:
                Call(c, lay, sink);
                return;
            case NewExpr ne:
                if (ne.AsCall != null) { Expr(ne.AsCall, sink); return; }
                if (ne.Block != null) Opaque([ne.Block], "captured by a block or lambda");
                if (ne.Type.Sym is { Kind: "variant" } v)
                {
                    var deferred = new List<(NameExpr, LocalSym, Sink)>();
                    for (int j = 0; j < ne.Args.Count; j++)
                    {
                        var ft = j < v.FieldOrder.Count ? v.Fields[v.FieldOrder[j]].Type : AType.Unknown;
                        Arg(ne.Args[j].Value, Cand(ft) != null ? Sink.Move : Sink.Esc($"passed to field of {v.Name} typed {ft.Cs}"), deferred);
                    }
                    Flush(deferred);
                }
                else foreach (var a in ne.Args) Expr(a.Value, Sink.Esc($"passed to `{ne.TypeRef.Name}.new`"));
                Temp(lay, e, sink);
                return;
            case ConstExpr:
                Temp(lay, e, sink);
                return;
            case BinaryExpr b when b.Op is "&&" or "||" or "and" or "or":
                {
                    Expr(b.L, Sink.Read);
                    var rhs = fl.New();
                    var join = fl.New();
                    Flow.Edge(fl.Cur, rhs);
                    Flow.Edge(fl.Cur, join);
                    fl.Cur = rhs;
                    Expr(b.R, Sink.Read);
                    Flow.Edge(fl.Cur, join);
                    fl.Cur = join;
                    return;
                }
            case BinaryExpr b:
                {
                    var why = b.Op is "==" or "!=" or "===" or "<=>" or "equal?" ? $"compared with `{b.Op}`" : $"operand of `{b.Op}`";
                    Expr(b.L, Sink.Esc(why));
                    Expr(b.R, Sink.Esc(why));
                    return;
                }
            case TernaryExpr t:
                {
                    Expr(t.C, Sink.Read);
                    var a = fl.New();
                    var bb = fl.New();
                    var join = fl.New();
                    Flow.Edge(fl.Cur, a);
                    Flow.Edge(fl.Cur, bb);
                    fl.Cur = a;
                    Expr(t.A, sink);
                    Flow.Edge(fl.Cur, join);
                    fl.Cur = bb;
                    Expr(t.B, sink);
                    Flow.Edge(fl.Cur, join);
                    fl.Cur = join;
                    return;
                }
            case StmtExpr se:
                if (se.Stmt is IfStmt or CaseStmt or BeginStmt) Statement(se.Stmt, sink);
                else Opaque([se.Stmt], "used by an unsupported statement");
                return;
            case LambdaExpr lam:
                Opaque([lam], "captured by a block or lambda");
                return;
            case SpawnExpr sp:
                Opaque([sp.Body], "captured by a `spawn` block");
                return;
            case InterpExpr ie:
                foreach (var h in ie.Holes) Expr(h, Sink.Esc("interpolated into a string (`to_s`)"));
                return;
            case ArrayLit or HashLit or TupleLit:
                Children(e, Sink.Esc("stored in a collection"));
                return;
            case TypeTestExpr tt:
                Expr(tt.E, tt.Kind == "is" ? Sink.Read : Sink.Esc($"`{tt.Kind}` applied to it"));
                return;
            case UnaryExpr u:
                Expr(u.E, Sink.Esc($"operand of `{u.Op}`"));
                return;
            case IndexExpr ix:
                Expr(ix.Target, Sink.Esc("indexed"));
                foreach (var a in ix.Args) Expr(a.Value, Sink.Esc("used as an index"));
                Temp(lay, e, sink.U == UseKind.Escape ? sink : Sink.Esc("read out of a collection"));
                return;
            default:
                Children(e, Sink.Esc($"used by {e.GetType().Name.Replace("Expr", "").ToLowerInvariant()}"));
                if (lay != null) Temp(lay, e, sink.U is UseKind.Read or UseKind.Escape ? sink : Sink.Esc($"produced by {e.GetType().Name.Replace("Expr", "").ToLowerInvariant()}"));
                return;
        }
    }

    /// <summary>Every direct child expression of n, with one sink.</summary>
    void Children(Node n, Sink sink)
    {
        foreach (var fi in n.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            switch (fi.GetValue(n))
            {
                case Expr c: Expr(c, sink); break;
                case Arg a: Expr(a.Value, sink); break;
                case System.Collections.IEnumerable list and not string:
                    foreach (var x in list)
                        if (x is Expr xe) Expr(xe, sink);
                        else if (x is Arg xa) Expr(xa.Value, sink);
                        else if (x is Stmt st) Opaque([st], "used by an unsupported statement");
                    break;
                case Stmt st: Opaque([st], "used by an unsupported statement"); break;
            }
    }

    /// <summary>A fresh value (call result, construction): only an escaping sink matters.</summary>
    void Temp(ArenaLayout? lay, Expr e, Sink sink)
    {
        if (lay != null && sink.U == UseKind.Escape) fl.Add(new Ev { K = EvKind.Escape, At = e, Lay = lay, Reason = sink.Reason });
    }

    void Call(CallExpr c, ArenaLayout? lay, Sink sink)
    {
        if (c.Recv != null)
        {
            if (Cand(c.Recv.Type) != null)
            {
                bool getter = c.Sym is FieldSym && c.Args.Count == 0;
                Expr(c.Recv, getter ? Sink.Read : Sink.Esc($"`.{c.Name}` called on it"));
                if (getter && lay != null) fl.Add(new Ev { K = EvKind.Escape, At = c, Lay = lay, Reason = $"field read through `.{c.Name}`" });
                foreach (var a in c.Args) Expr(a.Value, Sink.Esc($"passed to `.{c.Name}`"));
                if (c.Block != null) Opaque([c.Block], "captured by a block or lambda");
                return;
            }
            Expr(c.Recv, Sink.Read);
        }
        if (c.Block != null) Opaque([c.Block], "captured by a block or lambda");
        var m = c.Sym as MethodSym;
        bool alice = m is { Decl: not null };
        var deferred = new List<(NameExpr, LocalSym, Sink)>();
        for (int i = 0; i < c.Args.Count; i++)
        {
            var a = c.Args[i];
            Sink s;
            if (!alice) s = Sink.Esc($"passed to .NET `{c.Name}`");
            else
            {
                var p = a.Name != null ? m!.Params.FirstOrDefault(x => x.Name == a.Name) : i < m!.Params.Count ? m.Params[i] : null;
                if (p == null || p.Splat || p.Block || a.Mod != "") s = Sink.Esc($"passed to a splat or block parameter of `{c.Name}`");
                else if (Cand(p.Type) == null) s = Sink.Esc($"passed to parameter `{p.Name}` of `{c.Name}` typed {p.Type.Cs}");
                else s = Lends(p) ? new Sink(UseKind.BorrowArg) : Sink.Move;
            }
            Arg(a.Value, s, deferred);
        }
        Flush(deferred);
        if (lay != null && !alice) { Temp(lay, c, Sink.Esc($"returned by .NET `{c.Name}`")); return; }
        Temp(lay, c, sink);
    }

    /// <summary>Arguments evaluate left to right; a plain local moves only when the call is made, after every
    /// argument (so `f(t, size(t))` reads t before f takes it).</summary>
    void Arg(Expr? v, Sink s, List<(NameExpr, LocalSym, Sink)> deferred)
    {
        if (v is NameExpr { Sym: LocalSym l } n && s.U == UseKind.Move && Tracked(l)) deferred.Add((n, l, s));
        else Expr(v, s);
    }

    void Flush(List<(NameExpr N, LocalSym L, Sink S)> deferred)
    {
        foreach (var (n, l, s) in deferred) fl.Add(new Ev { K = EvKind.Use, L = l, N = n, At = n, U = s.U, Reason = s.Reason });
    }

    // ---- liveness (backward) ----

    void Liveness()
    {
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = fl.Blocks.Count - 1; i >= 0; i--)
            {
                var b = fl.Blocks[i];
                var o = new HashSet<LocalSym>();
                foreach (var s in b.Succ) o.UnionWith(s.LiveIn);
                var live = new HashSet<LocalSym>(o);
                for (int k = b.Evs.Count - 1; k >= 0; k--) Back(b.Evs[k], live, record: false);
                if (!live.SetEquals(b.LiveIn) || !o.SetEquals(b.LiveOut)) { b.LiveIn = live; b.LiveOut = o; changed = true; }
            }
        }
        foreach (var b in fl.Blocks)
        {
            var live = new HashSet<LocalSym>(b.LiveOut);
            for (int k = b.Evs.Count - 1; k >= 0; k--) Back(b.Evs[k], live, record: true);
        }
    }

    static void Back(Ev e, HashSet<LocalSym> live, bool record)
    {
        if (record) e.LiveAfter = new HashSet<LocalSym>(live);
        switch (e.K)
        {
            case EvKind.Use: live.Add(e.L!); break;
            case EvKind.Def: live.Remove(e.L!); break;
            case EvKind.Arm: foreach (var b in e.Binds) live.Remove(b); break;
        }
    }

    // ---- move states (forward) ----

    static St Join(St a, St b)
    {
        if (a == b) return a;
        if (a.K == StK.Undefined || b.K == StK.Undefined)
        {
            var d = a.K == StK.Undefined ? b : a;
            return d.K is StK.Owned or StK.Kept or StK.Moved ? new St(StK.MaybeMoved, null, d.MovedAt) : d;
        }
        if (a.K == StK.Moved && b.K == StK.Moved) return a.MovedAt == b.MovedAt ? a with { Keepable = a.Keepable && b.Keepable } : new St(StK.MaybeMoved, null, a.MovedAt ?? b.MovedAt);
        if (a.K is StK.Moved or StK.MaybeMoved || b.K is StK.Moved or StK.MaybeMoved) return new St(StK.MaybeMoved, null, a.MovedAt ?? b.MovedAt);
        if (a.K == StK.BorrowedParam) return a;
        if (b.K == StK.BorrowedParam) return b;
        if (a.K == StK.Borrowed) return a;
        if (b.K == StK.Borrowed) return b;
        if (a.K == StK.Kept || b.K == StK.Kept) return new St(StK.Kept);
        return new St(StK.Owned);   // owned + packed
    }

    void Forward(Blk entry)
    {
        var reach = new HashSet<Blk>();
        var stack = new Stack<Blk>([entry]);
        while (stack.Count > 0) { var b = stack.Pop(); if (reach.Add(b)) foreach (var s in b.Succ) stack.Push(s); }
        entry.In = new();
        var work = new Queue<Blk>([entry]);
        var queued = new HashSet<Blk>([entry]);
        int guard = 0;
        while (work.Count > 0 && guard++ < 100_000)
        {
            var b = work.Dequeue();
            queued.Remove(b);
            var st = new Dictionary<LocalSym, St>(b.In!);
            foreach (var e in b.Evs) Transfer(e, st, record: false);
            foreach (var s in b.Succ)
            {
                var merged = s.In == null ? new Dictionary<LocalSym, St>(st) : JoinMaps(s.In, st);
                if (s.In == null || !SameMap(s.In, merged)) { s.In = merged; if (queued.Add(s)) work.Enqueue(s); }
            }
        }
        foreach (var b in fl.Blocks)
        {
            if (!reach.Contains(b) || b.In == null) continue;
            var st = new Dictionary<LocalSym, St>(b.In);
            foreach (var e in b.Evs) Transfer(e, st, record: true);
        }
    }

    /// <summary>A local assigned on one path only is not definitely owned after the join (no Drop, no Take).</summary>
    static Dictionary<LocalSym, St> JoinMaps(Dictionary<LocalSym, St> a, Dictionary<LocalSym, St> b)
    {
        var r = new Dictionary<LocalSym, St>();
        foreach (var k in a.Keys.Union(b.Keys))
            r[k] = Join(a.TryGetValue(k, out var x) ? x : new St(StK.Undefined), b.TryGetValue(k, out var y) ? y : new St(StK.Undefined));
        return r;
    }

    static bool SameMap(Dictionary<LocalSym, St> a, Dictionary<LocalSym, St> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    readonly HashSet<Node> keepSites = [];

    void Transfer(Ev e, Dictionary<LocalSym, St> st, bool record)
    {
        switch (e.K)
        {
            case EvKind.Escape:
                if (record && e.Lay != null) Escape(e.Lay, fl.File, e.At, e.At is Expr ex ? Describe(ex) : "value", e.Reason);
                return;
            case EvKind.Def:
                {
                    var l = e.L!;
                    st[l] = e.D switch
                    {
                        DefKind.Packed => new St(StK.Packed),
                        DefKind.Borrowed => new St(StK.BorrowedParam, l),
                        DefKind.FromUse => e.FromUse!.Pre.K switch
                        {
                            StK.Packed => new St(StK.Packed),
                            StK.Borrowed or StK.BorrowedParam => e.FromUse.Pre,
                            _ => new St(StK.Owned),
                        },
                        _ => new St(StK.Owned),
                    };
                    return;
                }
            case EvKind.Use:
                Use(e, st, record);
                return;
            case EvKind.Arm:
                Arm(e, st, record);
                return;
            case EvKind.Anchor:
                Anchor(e, st, record);
                return;
        }
    }

    ArenaLayout LayOf(LocalSym l) => Affine.Layouts[(l.Type.Sym!.Kind == "variant" ? l.Type.Sym.Parent : l.Type.Sym)!];

    void Use(Ev e, Dictionary<LocalSym, St> st, bool record)
    {
        var l = e.L!;
        var s = st.TryGetValue(l, out var cur) ? cur : new St(StK.Owned);
        if (s.K == StK.Undefined) s = new St(StK.Owned);
        e.Pre = s;
        var lay = LayOf(l);
        string line(Node? n) => n != null ? $"{fl.File}:{n.Line}" : "an earlier line";
        switch (e.U)
        {
            case UseKind.Escape:
                if (record) Escape(lay, fl.File, e.At, l.Name, e.Reason);
                return;
            case UseKind.Read or UseKind.BorrowArg or UseKind.Case:
                if (e.U == UseKind.BorrowArg && record && e.N != null) Affine.Uses[e.N] = AffineOp.BorrowArg;
                if (s.K == StK.Moved && s.Keepable && lay.Declared)
                {
                    if (record && s.MovedAt is NameExpr ms) { keepSites.Add(ms); Affine.Uses[ms] = AffineOp.Keep; }
                    st[l] = new St(StK.Kept);
                }
                else if (s.K is StK.Moved or StK.MaybeMoved && record)
                    Violate(lay, fl.File, e.At, "AL0242", $"affine value `{l.Name}` read after it was consumed (moved at {line(s.MovedAt)}); read the fields inside the `case`, or pass it as `borrow`",
                        $"`{l.Name}` used after it was moved at {line(s.MovedAt)}");
                return;
            default:   // Move, Return, Assign
                if (record && e.N != null) Affine.Uses.TryAdd(e.N, AffineOp.Move);
                switch (s.K)
                {
                    case StK.Packed:
                        return;
                    case StK.Owned:
                        if (record && BorrowedLive(l, st, e.LiveAfter) is { } child)
                            Violate(lay, fl.File, e.At, "AL0242", $"affine value `{child.Name}` read after it was consumed: it is a field of `{l.Name}`, which is moved here; consume `{child.Name}` first or pass `{l.Name}` as `borrow`",
                                $"`{l.Name}` moved while its field `{child.Name}` is still used");
                        st[l] = new St(StK.Moved, null, e.N, e.U != UseKind.Return);
                        return;
                    case StK.Kept:
                        if (record) Violate(lay, fl.File, e.At, "AL0242", $"affine value `{l.Name}` read after it was consumed: it is shared (kept) and moved again; pass it as `borrow`",
                            $"`{l.Name}` moved again after a shared use");
                        return;
                    case StK.Borrowed:
                        if (e.U == UseKind.Assign) { st[l] = new St(StK.Moved, null, e.N, false); return; }
                        if (record && (e.LiveAfter.Contains(s.Root!) || st.TryGetValue(s.Root!, out var rs) && rs.K is not (StK.Owned or StK.Moved)))
                            Violate(lay, fl.File, e.At, "AL0242", $"affine value `{s.Root!.Name}` read after it was consumed: its field `{l.Name}` is moved here while `{s.Root.Name}` is still used",
                                $"field `{l.Name}` of `{s.Root!.Name}` moved while `{s.Root.Name}` is still used");
                        st[l] = new St(StK.Moved, null, e.N, false);
                        return;
                    case StK.BorrowedParam:
                        if (e.U == UseKind.Assign) return;
                        if (fl.Params.TryGetValue(s.Root!, out var bp) && !bp.Borrow)
                        {
                            // an inferred borrow that the body consumes: the parameter is owned after all
                            if (Affine.InferredBorrow.Contains(bp)) borrowFlips.Add(bp);
                            st[l] = new St(StK.Moved, null, e.N, false);
                            return;
                        }
                        if (record)
                        {
                            string what = s.Root == l ? $"`{l.Name}`" : $"`{l.Name}` (a field of borrowed `{s.Root!.Name}`)";
                            Violate(lay, fl.File, e.At, "AL0243", $"borrowed affine value {what} is consumed; drop `borrow`, or only read it",
                                $"borrowed parameter `{s.Root!.Name}` is consumed");
                        }
                        return;
                    default:   // Moved, MaybeMoved
                        if (record)
                            Violate(lay, fl.File, e.At, "AL0242", $"affine value `{l.Name}` read after it was consumed (moved at {line(s.MovedAt)}); read the fields inside the `case`, or pass it as `borrow`",
                                $"`{l.Name}` used after it was moved at {line(s.MovedAt)}");
                        return;
                }
        }
    }

    /// <summary>A local borrowed from `root` that is still used after this point.</summary>
    static LocalSym? BorrowedLive(LocalSym root, Dictionary<LocalSym, St> st, HashSet<LocalSym> liveAfter) =>
        st.FirstOrDefault(kv => kv.Value.K == StK.Borrowed && kv.Value.Root == root && liveAfter.Contains(kv.Key)).Key;

    void Arm(Ev e, Dictionary<LocalSym, St> st, bool record)
    {
        var w = e.W!;
        ArmMode mode;
        St bind;
        if (e.L is { } x)
        {
            var s = st.TryGetValue(x, out var cur) && cur.K != StK.Undefined ? cur : new St(StK.Owned);
            bool take = w.Guard == null && s.K is StK.Owned or StK.Packed && !e.LiveAfter.Contains(x) && BorrowedLive(x, st, e.LiveAfter) == null;
            if (take)
            {
                mode = ArmMode.Take;
                bind = new St(StK.Owned);
                st[x] = new St(StK.Moved, null, w, false);
            }
            else
            {
                mode = s.K == StK.Kept ? ArmMode.Keep : ArmMode.Borrow;
                bind = s.K switch
                {
                    StK.BorrowedParam => s,
                    StK.Borrowed => s,
                    _ => new St(StK.Borrowed, x),
                };
            }
        }
        else
        {
            mode = e.Fresh && w.Guard == null ? ArmMode.Take : ArmMode.Borrow;
            bind = new St(StK.Owned);
        }
        foreach (var b in e.Binds) st[b] = bind;
        if (record && e.Lay != null) { Affine.Arms[w] = mode; armTypes[w] = e.Lay; }
    }

    /// <summary>Owned locals dead here and not consumed: Drop (shallow); fields borrowed from them own their nodes now.</summary>
    void Anchor(Ev e, Dictionary<LocalSym, St> st, bool record)
    {
        for (bool again = true; again;)
        {
            again = false;
            foreach (var l in fl.Tracked)
            {
                if (!st.TryGetValue(l, out var s) || s.K is not (StK.Owned or StK.Kept) || e.LiveAfter.Contains(l)) continue;
                if (record)
                {
                    var list = e.Before && e.At is Stmt b2
                        ? (Affine.DropsBefore.TryGetValue(b2, out var x) ? x : Affine.DropsBefore[b2] = [])
                        : (Affine.Drops.TryGetValue(e.At, out var y) ? y : Affine.Drops[e.At] = []);
                    if (!list.Contains(l)) list.Add(l);
                }
                st[l] = new St(StK.Moved, null, e.At, false);
                foreach (var (k, v) in st.ToList())
                    if (v.K == StK.Borrowed && v.Root == l) st[k] = new St(StK.Owned);
                again = true;
            }
        }
    }
}
