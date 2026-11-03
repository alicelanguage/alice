using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>A reference type (nil is a possible value of its `?` form; value types, Nullable(T) and type
    /// parameters are not tracked: C# reports nothing for them either).</summary>
    static bool RefType(AType t)
    {
        if (t.Kind == "null") return true;
        if (t.Kind is not ("class" or "array" or "iface" or "func" or "generic")) return false;
        if (t.Clr is { IsValueType: true }) return false;
        return t.Sym is not { Kind: "struct" or "record struct" or "enum" };
    }

    /// <summary>Declared maybe-nil: `T?` of a reference type, or the nil literal's type.</summary>
    static bool MaybeNilType(AType t) => t.Kind == "null" || (t.Nullable || t.Cs.EndsWith('?')) && RefType(t);

    /// <summary>
    /// Nullable flow analysis over bound HIR, the C# nullable analysis the generated code got from Roslyn: each local,
    /// `@field` and member path has a state (maybe nil or not); checks (`if x`, `unless`, `x.nil?`, `x == nil`,
    /// `&&`/`||`, `case`/`when`, early exits) narrow it, joins merge it, loops run to a fixpoint. Dereferencing a
    /// maybe-nil value, or storing one where the declared type is not `T?` (explicitly typed local, field, parameter
    /// of an Alice method, return value), is AL0310.
    /// </summary>
    sealed class NilFlow(Binder b, string path)
    {
        /// <summary>Flow state: maybe-nil per tracked key; null is unreachable.</summary>
        sealed class St
        {
            public readonly Dictionary<string, bool> M = new();
            public St Clone() { var s = new St(); foreach (var kv in M) s.M[kv.Key] = kv.Value; return s; }
        }

        St? cur = new();
        readonly Dictionary<string, bool> defaults = new();
        readonly Dictionary<LocalSym, int> ids = new(ReferenceEqualityComparer.Instance);
        /// <summary>Locals the C# declares with their type (`T x = default!`): storing a maybe-nil value warns.</summary>
        readonly HashSet<LocalSym> typed = new(ReferenceEqualityComparer.Instance);

        sealed class Frame
        {
            public AType? Ret;          // value-returning function or block: its declared result
            public bool Lambda;
            public bool Loop;
            public List<St?> Breaks = new(), Nexts = new();
        }
        readonly List<Frame> frames = new();
        /// <summary>Joined states of the statements of the enclosing `begin` bodies (the state a `rescue` starts from).</summary>
        readonly List<St?> tries = new();

        void Nil(Node at) => b.FlowError(path, at, NilCode, NilText);

        // ---- states

        St? Join(St? a, St? c)
        {
            if (a == null) return c?.Clone();
            if (c == null) return a.Clone();
            var r = new St();
            foreach (var k in a.M.Keys.Union(c.M.Keys))
                r.M[k] = Get(a, k) || Get(c, k);
            return r;
        }

        St? JoinAll(IEnumerable<St?> xs)
        {
            St? r = null;
            foreach (var x in xs) r = Join(r, x);
            return r;
        }

        bool Get(St s, string k) => s.M.TryGetValue(k, out var v) ? v : defaults.GetValueOrDefault(k);

        bool Same(St? a, St? c)
        {
            if (a == null || c == null) return a == c;
            return a.M.Keys.Union(c.M.Keys).All(k => Get(a, k) == Get(c, k));
        }

        void Set(string? k, bool maybeNil)
        {
            if (k == null || cur == null) return;
            foreach (var sub in cur.M.Keys.Where(x => x.StartsWith(k + ".", StringComparison.Ordinal)).ToList()) cur.M.Remove(sub);
            cur.M[k] = maybeNil;
            if (k[0] == 'L' && !k.Contains('.')) cur.M.Remove("U" + k[1..]);   // assigned
        }

        /// <summary>A reference-typed local declared without a value (`x : String`, a hoisted Ruby local): unassigned
        /// until a store on every path; a read before that is AL0309 (it would read nil).</summary>
        void Unassigned(LocalSym l)
        {
            if (cur != null && RefType(l.Type) && !MaybeNilType(l.Type)) cur.M["U" + LocalKey(l)[1..]] = true;
        }

        static void Set(St? s, string? k, bool maybeNil)
        {
            if (k == null || s == null) return;
            s.M[k] = maybeNil;
        }

        int Id(LocalSym l)
        {
            if (!ids.TryGetValue(l, out var i)) ids[l] = i = ids.Count;
            return i;
        }

        /// <summary>The tracked path of an expression (a local, `@field`, a member of a tracked path), or null.</summary>
        string? Key(Expr e)
        {
            string? k = e switch
            {
                NameExpr { Res: "local", Sym: LocalSym l } => "L" + Id(l),
                IvarExpr iv => "@" + iv.Name,
                NameExpr { Res: "field", Sym: FieldSym { IsStatic: false } f } => "@" + f.Name,
                CallExpr { Recv: null or SelfExpr, Res: "field", Args.Count: 0, Block: null } c => "@" + c.Name,
                CallExpr { Recv: { } r, Res: "field", Args.Count: 0, Block: null, Safe: false } c when Key(r) is { } rk => rk + "." + c.Name,
                _ => null,
            };
            if (k != null && !defaults.ContainsKey(k)) defaults[k] = MaybeNilType(e.Type);
            return k;
        }

        string LocalKey(LocalSym l) => Key(new NameExpr { Res = "local", Sym = l, Type = l.Type })!;

        bool Read(Expr e)
        {
            var k = Key(e);
            if (k != null && cur != null && e is NameExpr { Sym: LocalSym l } && cur.M.GetValueOrDefault("U" + k[1..]))
            {
                b.FlowError(path, e, UnassignedCode, $"'{l.Name}' may be used before it is assigned");
                cur.M.Remove("U" + k[1..]);
            }
            if (k != null && cur != null) return Get(cur, k);
            return MaybeNilType(e.Type);
        }

        /// <summary>The declared element type of an index: `Dictionary(String, String?)` reads `String?` (the bound
        /// node type comes from the closed CLR type, which has no `?`).</summary>
        static AType ElemType(IndexExpr ix)
        {
            var t = ix.Target.Type;
            if (ix.Slice != null || ix.TupleElement != null) return ix.Type;
            if (t.Kind == "array" && t.Elem != null) return t.Elem;
            if (t.Args.Count is 1 or 2 && t.Clr is { IsGenericType: true } g && g.GetGenericArguments().Length == t.Args.Count
                && g.GetGenericTypeDefinition() is var d
                && (d == typeof(List<>) || d == typeof(Dictionary<,>) || d == typeof(IList<>) || d == typeof(IDictionary<,>)
                    || d == typeof(IReadOnlyList<>) || d == typeof(IReadOnlyDictionary<,>) || d == typeof(SortedDictionary<,>)))
                return t.Args[^1];
            return ix.Type;
        }

        // ---- items

        public void File(FileNode f)
        {
            foreach (var l in f.HoistedLocals) typed.Add(l);
            var main = new List<Stmt>();
            Items(f.Items, main);
            cur = new St();
            foreach (var l in f.HoistedLocals) Unassigned(l);
            frames.Add(new Frame());
            Block(main, false);
            frames.Clear();
        }

        void Items(IEnumerable<Item> items, List<Stmt> main)
        {
            foreach (var it in items)
                switch (it)
                {
                    case StmtItem si: main.Add(si.Stmt); break;
                    case NamespaceItem n: Items(n.Items, main); break;
                    case TypeDecl td: Items(td.Members, main); break;
                    case MethodDecl { Sym: MethodSym m, IsTemplate: false, HasBody: true } md: Method(md, m); break;
                    case FieldDecl { Init: { } init } fd:
                        cur = new St();
                        frames.Add(new Frame());
                        Store((it.Sym as FieldSym)?.Type, Eval(init), init);
                        frames.Clear();
                        break;
                }
        }

        void Method(MethodDecl md, MethodSym m)
        {
            cur = new St();
            foreach (var l in md.HoistedLocals) { typed.Add(l); Unassigned(l); }
            foreach (var p in m.Params)
                if (p.Local != null) Set(LocalKey(p.Local), MaybeNilType(p.Local.Type));
            bool value = !m.IsCtor && !m.Ret.IsVoid && m.Ret.IsKnown && !md.IsAsync && !m.IsIterator;
            frames.Add(new Frame { Ret = value ? m.Ret : null });
            Block(md.Body, value);
            frames.Clear();
        }

        /// <summary>A value stored where `declared` is the target type: maybe-nil into a non-`?` reference is AL0310.</summary>
        void Store(AType? declared, bool maybeNil, Node at)
        {
            if (maybeNil && declared != null && RefType(declared) && !MaybeNilType(declared)) Nil(at);
        }

        // ---- statements

        void Block(List<Stmt> body, bool tail)
        {
            for (int i = 0; i < body.Count; i++)
            {
                if (cur == null) return;   // unreachable code: C# reports nothing there
                Stmt(body[i], tail && i == body.Count - 1);
                if (tries.Count > 0) tries[^1] = Join(tries[^1], cur);
            }
        }

        /// <summary>The innermost function or block frame (where `return` / a tail value goes).</summary>
        Frame Fn()
        {
            for (int k = frames.Count - 1; k > 0; k--) if (!frames[k].Loop) return frames[k];
            return frames[0];
        }

        void Stmt(Stmt s, bool tail)
        {
            if (cur == null) return;
            switch (s)
            {
                case ExprStmt es:
                    {
                        bool v = Eval(es.Expr);
                        if (tail && Fn().Ret is { } rt) Store(rt, v, es.Expr);
                        if (es.Expr is CallExpr { Recv: null, Name: "raise" }) cur = null;
                        break;
                    }
                case LocalDecl d:
                    {
                        // `x : String?` with no value holds nil (the C# `default!` hid that from Roslyn)
                        bool v = d.Value != null ? Eval(d.Value) : d.Local != null && MaybeNilType(d.Local.Type);
                        if (d.Local == null) break;
                        if (d.Type != null || d.Value == null) typed.Add(d.Local);
                        if (d.Type != null && d.Value != null) Store(d.Local.Type, v, d.Value);
                        Set(LocalKey(d.Local), v);
                        if (d.Value == null) Unassigned(d.Local);
                        break;
                    }
                case Assign a:
                    Assign(a);
                    break;
                case MultiAssign ma:
                    {
                        var vs = ma.Values.Select(Eval).ToList();
                        for (int i = 0; i < ma.Locals.Count; i++)
                            Set(LocalKey(ma.Locals[i]), ma.Values.Count == ma.Locals.Count ? vs[i] : MaybeNilType(ma.Locals[i].Type));
                        break;
                    }
                case IfStmt i:
                    {
                        var ends = new List<St?>();
                        for (int k = 0; k < i.Conds.Count && cur != null; k++)
                        {
                            var (t, f) = Cond(i.Conds[k]);
                            cur = t;
                            Block(i.Bodies[k], tail);
                            ends.Add(cur);
                            cur = f;
                        }
                        if (i.Else != null) Block(i.Else, tail);
                        ends.Add(cur);
                        cur = JoinAll(ends);
                        break;
                    }
                case WhileStmt w:
                    Loop(() => Cond(w.Cond), w.Body);
                    break;
                case ForStmt f:
                    Eval(f.Iter);
                    Loop(() =>
                    {
                        var exit = cur?.Clone();
                        foreach (var l in f.Locals) Set(LocalKey(l), MaybeNilType(l.Type));
                        return (cur, exit);
                    }, f.Body);
                    break;
                case CaseStmt c:
                    Case(c, tail);
                    break;
                case BeginStmt bg:
                    Begin(bg, tail);
                    break;
                case ReturnStmt r:
                    {
                        bool v = r.Value != null && Eval(r.Value);
                        if (r.Value != null && Fn().Ret is { } rt) Store(rt, v, r.Value);
                        cur = null;
                        break;
                    }
                case BreakStmt:
                    frames.LastOrDefault(x => x.Loop)?.Breaks.Add(cur);
                    cur = null;
                    break;
                case NextStmt n:
                    {
                        bool v = n.Value != null && Eval(n.Value);
                        var fr = frames[^1];
                        if (fr.Loop) fr.Nexts.Add(cur);
                        else if (fr.Lambda && n.Value != null && fr.Ret is { } rt) Store(rt, v, n.Value);
                        cur = null;
                        break;
                    }
                case RaiseStmt rs:
                    Eval(rs.Value);
                    cur = null;
                    break;
                case RetryStmt:
                    cur = null;
                    break;
                case UsingStmt u:
                    {
                        bool v = Eval(u.Value);
                        if (u.Local != null) Set(LocalKey(u.Local), v);
                        Block(u.Body, false);
                        break;
                    }
            }
        }

        void Assign(Assign a)
        {
            var key = Key(a.Target);
            if (a.Op == "||=")
            {
                bool before = Read(a.Target);
                bool v = Eval(a.Value);
                Set(key, before && v);
                return;
            }
            if (a.Op != "=")
            {
                EvalTarget(a.Target);
                Eval(a.Value);
                if (a.Target.Type.IsString) Set(key, false);
                return;
            }
            if (a.Target is NameExpr { Res: "local", Sym: LocalSym l })
            {
                bool v = Eval(a.Value);
                if (typed.Contains(a.Declares ?? l)) Store(l.Type, v, a.Value);
                Set(key, v);
                return;
            }
            EvalTarget(a.Target);
            bool w = Eval(a.Value);
            if (a.Target is IvarExpr or NameExpr { Res: "field" } or CallExpr { Res: "field" }) Store(a.Target.Type, w, a.Value);
            else if (a.Target is IndexExpr ti) Store(ElemType(ti), w, a.Value);
            Set(key, w);
        }

        /// <summary>The receiver and indexes of an assignment target (a store through a maybe-nil receiver dereferences it).</summary>
        void EvalTarget(Expr t)
        {
            switch (t)
            {
                case CallExpr { Recv: { } r } c:
                    if (Eval(r) && !c.Safe) Deref(r);
                    foreach (var a in c.Args) if (a.Value != null) Eval(a.Value);
                    break;
                case IndexExpr ix:
                    if (Eval(ix.Target)) Deref(ix.Target);
                    foreach (var a in ix.Args) if (a.Value != null) Eval(a.Value);
                    break;
            }
        }

        void Deref(Expr recv)
        {
            Nil(recv);
            Set(Key(recv), false);   // reported once: C# then takes it as non-nil
        }

        /// <summary>Runs a loop body to a fixpoint. `enter` gives (state entering the body, state leaving the loop
        /// by its condition) from the head state in <see cref="cur"/>.</summary>
        void Loop(Func<(St? Body, St? Exit)> enter, List<Stmt> body)
        {
            var head = cur;
            St? exit = null;
            Frame fr = new();
            for (int iter = 0; iter < 8; iter++)
            {
                cur = head?.Clone();
                var (bs, ex) = enter();
                exit = ex;
                fr = new Frame { Loop = true };
                frames.Add(fr);
                cur = bs;
                Block(body, false);
                frames.RemoveAt(frames.Count - 1);
                var next = Join(head, JoinAll(fr.Nexts.Append(cur)));
                if (Same(next, head)) break;
                head = next;
            }
            cur = JoinAll(fr.Breaks.Append(exit));
        }

        void Case(CaseStmt c, bool tail)
        {
            bool subj = c.Subject != null && Eval(c.Subject);
            string? sk = c.Subject != null ? Key(c.Subject) : null;
            var ends = new List<St?>();
            var rest = cur;
            foreach (var w in c.Whens)
            {
                if (rest == null) break;
                St? t = null;
                cur = rest;
                foreach (var p in w.Patterns)
                {
                    if (cur == null) break;
                    var (pt, pf) = c.Subject == null ? Cond(p) : Pattern(p, sk, subj);
                    t = Join(t, pt);
                    cur = pf;
                }
                var f = cur;
                if (w.Guard != null && t != null)
                {
                    cur = t;
                    var (gt, gf) = Cond(w.Guard);
                    t = gt;
                    f = Join(f, gf);
                }
                cur = t;
                Block(w.Body, tail);
                ends.Add(cur);
                rest = f;
            }
            cur = rest;
            if (c.Else != null) Block(c.Else, tail);
            if (c.Else != null || !c.Exhaustive) ends.Add(cur);
            cur = JoinAll(ends);
        }

        (St? T, St? F) Split(string? k, bool whenTrue, bool whenFalse)
        {
            var t = cur!.Clone(); var f = cur.Clone();
            Set(t, k, whenTrue); Set(f, k, whenFalse);
            return (t, f);
        }

        /// <summary>A `when` pattern against the subject: (state where it matches, state where it does not).</summary>
        (St? T, St? F) Pattern(Expr p, string? sk, bool subjNil)
        {
            switch (p)
            {
                case LitExpr { Kind: "nil" }:
                    return Split(sk, true, false);
                case BindPatternExpr bp:
                    if (bp.Local != null) Set(LocalKey(bp.Local), sk != null ? Get(cur!, sk) : subjNil);
                    return (cur, null);
                case ArrayPatternExpr ap:
                    foreach (var l in ap.Literals) if (l != null) Eval(l);
                    foreach (var l in ap.Binds) if (l != null) Set(LocalKey(l), MaybeNilType(l.Type));
                    if (ap.RestSym != null) Set(LocalKey(ap.RestSym), false);
                    return (Split(sk, false, false).T, cur!.Clone());
                case HashPatternExpr hp:
                    foreach (var l in hp.Binds) if (l != null) Set(LocalKey(l), MaybeNilType(l.Type));
                    return (Split(sk, false, false).T, cur!.Clone());
            }
            if (p.Res == "destructure" && p.Sym is Destructure d)
            {
                foreach (var (_, bind, eq) in d.Parts)
                {
                    if (eq != null) Eval(eq);
                    if (bind != null) Set(LocalKey(bind), MaybeNilType(bind.Type));
                }
                return (Split(sk, false, false).T, cur!.Clone());
            }
            bool typeTest = p is ConstExpr || p.Res == "type" || p is LitExpr { Kind: not "nil" };   // `when "abc"`: x == "abc"
            if (p is not ConstExpr) Eval(p);
            if (cur == null) return (null, null);
            var tt = cur.Clone();
            if (typeTest) Set(tt, sk, false);
            return (tt, cur.Clone());
        }

        void Begin(BeginStmt bg, bool tail)
        {
            if (bg.Lock != null) Eval(bg.Lock);
            tries.Add(cur?.Clone());
            Block(bg.Body, tail && bg.Ensure == null);
            var all = tries[^1];
            tries.RemoveAt(tries.Count - 1);
            if (tries.Count > 0) tries[^1] = Join(tries[^1], all);
            var ends = new List<St?> { cur };
            foreach (var r in bg.Rescues)
            {
                cur = all?.Clone();
                if (cur == null) continue;
                if (r.Local != null) Set(LocalKey(r.Local), false);
                Block(r.Body, tail && bg.Ensure == null);
                ends.Add(cur);
            }
            cur = JoinAll(ends);
            if (bg.Ensure != null)
            {
                bool reach = cur != null;
                cur = Join(cur, all);
                Block(bg.Ensure, false);
                if (!reach) cur = null;
            }
        }

        // ---- conditions

        /// <summary>(state when `e` is truthy, state when it is falsy).</summary>
        (St? T, St? F) Cond(Expr e)
        {
            if (cur == null) return (null, null);
            switch (e)
            {
                case UnaryExpr { Op: "!" or "not" } u:
                    {
                        var (t, f) = Cond(u.E);
                        return (f, t);
                    }
                case BinaryExpr { Op: "&&" or "and" } ba:
                    {
                        var (lt, lf) = Cond(ba.L);
                        cur = lt;
                        var (rt, rf) = Cond(ba.R);
                        return (rt, Join(lf, rf));
                    }
                case BinaryExpr { Op: "||" or "or" } bo when bo.Res != "coalesce":
                    {
                        var (lt, lf) = Cond(bo.L);
                        cur = lf;
                        var (rt, rf) = Cond(bo.R);
                        return (Join(lt, rt), rf);
                    }
                case BinaryExpr { Op: "==" or "!=" } be when be.L is LitExpr { Kind: "nil" } || be.R is LitExpr { Kind: "nil" }:
                    {
                        var x = be.L is LitExpr { Kind: "nil" } ? be.R : be.L;
                        Eval(x);
                        if (cur == null) return (null, null);
                        var (isNil, notNil) = Split(Key(x), true, false);
                        return be.Op == "==" ? (isNil, notNil) : (notNil, isNil);
                    }
                case BinaryExpr { Op: "==" or "!=" } bq when bq.L is LitExpr { Kind: not "nil" } || bq.R is LitExpr { Kind: not "nil" }:
                    {
                        // `x == "abc"`: equal to a non-nil constant, so not nil (C# learns this too)
                        var x = bq.L is LitExpr { Kind: not "nil" } ? bq.R : bq.L;
                        Eval(bq.L);
                        Eval(bq.R);
                        if (cur == null) return (null, null);
                        var (eq, ne) = (cur.Clone(), cur.Clone());
                        Set(bq.Op == "==" ? eq : ne, Key(x), false);
                        return (eq, ne);
                    }
                case LitExpr { Kind: "bool", Text: "true" }:
                    return (cur, null);
                case LitExpr { Kind: "bool", Text: "false" }:
                    return (null, cur);
                case CallExpr { Name: "nil?", Recv: { } r, Args.Count: 0 }:
                    Eval(r);
                    return cur == null ? (null, null) : Split(Key(r), true, false);
                case TypeTestExpr { Kind: "nil?", E: { } x }:
                    Eval(x);
                    return cur == null ? (null, null) : Split(Key(x), true, false);
                case TypeTestExpr { Kind: "is", E: { } x }:
                    {
                        Eval(x);
                        if (cur == null) return (null, null);
                        var t = cur.Clone();
                        Set(t, Key(x), false);
                        return (t, cur.Clone());
                    }
                case StmtExpr { Stmt: Assign a }:
                    Assign(a);
                    return Cond(a.Target);
            }
            Eval(e);
            if (cur == null) return (null, null);
            var tt = cur.Clone();
            if (RefType(e.Type)) Set(tt, Key(e), false);
            return (tt, cur.Clone());
        }

        // ---- expressions

        /// <summary>Walks an expression (reporting dereferences of maybe-nil values); returns whether its value may be nil.</summary>
        bool Eval(Expr e)
        {
            if (cur == null) return false;
            switch (e)
            {
                case LitExpr l:
                    return l.Kind == "nil";
                case NameExpr n:
                    return n.Res is "local" or "field" ? Read(n) : MaybeNilType(n.Type);
                case IvarExpr:
                    return Read(e);
                case SelfExpr or RegexExpr:
                    return false;
                case SuperExpr sp:
                    if (sp.Args != null) Args(sp.Args, null);
                    return false;
                case ConstExpr:
                    return MaybeNilType(e.Type);
                case CallExpr c:
                    return Call(c);
                case NewExpr ne:
                    if (ne.AsCall != null) return Eval(ne.AsCall);
                    Args(ne.Args, ne.Sym as MethodSym);
                    if (ne.Block != null) Lambda(ne.Block);
                    return false;
                case IndexExpr ix:
                    if (Eval(ix.Target)) Deref(ix.Target);
                    foreach (var a in ix.Args) if (a.Value != null) Eval(a.Value);
                    return MaybeNilType(ElemType(ix));
                case BinaryExpr { Res: "coalesce" } co:
                    {
                        if (!Eval(co.L)) { var s0 = cur?.Clone(); Eval(co.R); cur = Join(s0, cur); return false; }
                        var save = cur?.Clone();
                        bool r = Eval(co.R);
                        cur = Join(save, cur);
                        return r;
                    }
                case BinaryExpr { Op: "&&" or "and" or "||" or "or" } bl:
                    {
                        var (t, f) = Cond(bl);
                        cur = Join(t, f);
                        return MaybeNilType(e.Type);
                    }
                case BinaryExpr bi:
                    Eval(bi.L);
                    Eval(bi.R);
                    return MaybeNilType(e.Type);
                case UnaryExpr u:
                    Eval(u.E);
                    return MaybeNilType(e.Type);
                case TernaryExpr te:
                    {
                        var (t, f) = Cond(te.C);
                        cur = t;
                        bool a = t != null && Eval(te.A);
                        var sa = cur;
                        cur = f;
                        bool bb = f != null && Eval(te.B);
                        cur = Join(sa, cur);
                        return a || bb;
                    }
                case RangeExpr r:
                    Eval(r.A);
                    Eval(r.B);
                    return false;
                case ArrayLit al:
                    foreach (var x in al.Items) Eval(x);
                    return false;
                case HashLit hl:
                    foreach (var x in hl.Keys) Eval(x);
                    foreach (var x in hl.Values) Eval(x);
                    return false;
                case TupleLit tl:
                    foreach (var x in tl.Items) Eval(x);
                    return false;
                case InterpExpr ie:
                    foreach (var x in ie.Holes) Eval(x);
                    return false;
                case LambdaExpr lam:
                    Lambda(lam);
                    return false;
                case TypeTestExpr tt:
                    if (tt.E != null) Eval(tt.E);
                    return tt.Kind switch
                    {
                        "not_nil!" or "nil?" or "is" or "typeof" or "sizeof" => false,
                        "default" => RefType(e.Type),
                        _ => MaybeNilType(e.Type),
                    };
                case InvokeExpr iv:
                    if (Eval(iv.Target)) Deref(iv.Target);
                    Args(iv.Args, null);
                    return MaybeNilType(e.Type);
                case AwaitExpr aw:
                    Eval(aw.E);
                    return MaybeNilType(e.Type);
                case SpawnExpr sp:
                    Lambda(sp.Body);
                    return false;
                case DataflowExpr df:
                    Args(df.Args, null);
                    Lambda(df.Body);
                    return false;
                case YieldExpr y:
                    Args(y.Args, null);
                    return MaybeNilType(e.Type);
                case StmtExpr se:
                    Stmt(se.Stmt, false);
                    return MaybeNilType(e.Type);
            }
            return MaybeNilType(e.Type);
        }

        /// <summary>Calls that do not dereference their receiver in the generated C# (`x.nil?` is `x is null`).</summary>
        static readonly HashSet<string> NoDeref = ["nil?", "not_nil!", "==", "!=", "===", "equal?", "is_a?", "kind_of?", "instance_of?", "try", "to_s", "inspect"];

        bool Call(CallExpr c)
        {
            bool rn = false;
            if (c.Recv != null)
            {
                rn = Eval(c.Recv);
                if (rn && !c.Safe && !NoDeref.Contains(c.Name)) Deref(c.Recv);
            }
            var clr = ClrMember(c);
            Args(c.Args, c.Sym as MethodSym, clr as System.Reflection.MethodBase ?? (c.Recv == null && c.Res == "call" && c.Sym == null ? (object)c.Name : null));
            if (c.Block != null) Lambda(c.Block);
            if (c.Name == "not_nil!") return false;
            if (c.Safe && rn) return true;
            if (Key(c) != null) return Read(c);
            return MaybeNilType(c.Type) || RefType(c.Type) && ClrMaybeNil(clr);
        }

        readonly System.Reflection.NullabilityInfoContext nic = new();

        static readonly Type[] ExtHosts = [typeof(Enumerable), typeof(CollectionExtensions)];

        /// <summary>The .NET member a CLR call or property read binds to (re-resolved as the binder resolved it).</summary>
        System.Reflection.MemberInfo? ClrMember(CallExpr c)
        {
            if (c.Sym is System.Reflection.MethodInfo smi) return smi;
            if (c.Res is not ("clr" or "field") || c.Recv?.Type.Clr is not { } rt || c.Recv is ConstExpr) return null;
            try
            {
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
                if (c.Res == "field")
                    return (System.Reflection.MemberInfo?)rt.GetProperties(F).FirstOrDefault(p => p.Name == c.Name && p.GetIndexParameters().Length == 0)
                        ?? rt.GetField(c.Name, F);
                int argc = c.Args.Count + (c.Block != null ? 1 : 0);
                if (ClrTypes.Lookup(rt, c.Name, argc, true, false, c.Args.Select(a => a.Value?.Type ?? AType.Unknown).ToList())?.Method is { } im) return im;
                // a BCL extension method (`d.GetValueOrDefault(k)`, `xs.FirstOrDefault`): nilable when every overload is
                var ext = ExtHosts.SelectMany(h => h.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    .Where(m => m.Name == c.Name && m.GetParameters().Length == argc + 1).ToList();
                return ext.Count > 0 && ext.All(ClrMaybeNil) ? ext[0] : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The member's result is annotated nilable (`string?`, `[MaybeNull] T`): what C# sees in metadata.</summary>
        bool ClrMaybeNil(System.Reflection.MemberInfo? m)
        {
            try
            {
                // a member of a constructed generic type: its declaration (`T receive`, `T? Find`) carries the
                // annotation; the closed member's `T` has none to read
                if (m?.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } dt)
                    m = dt.GetGenericTypeDefinition().GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                        .FirstOrDefault(x => x.MetadataToken == m.MetadataToken && x.Module == m.Module);
                // a bare type parameter result (`T receive`): its nullability is the type argument's, not readable here
                // (`T receive`), unless the declaration itself says nilable (`T? GetValueOrDefault`, `[MaybeNull] T`)
                // `[NotNullIfNotNull("path")] string? GetFileName(string? path)`: non-nil for a non-nil argument, which
                // is how it is nearly always called; taken as non-nil (a nil argument is the caller's own check)
                if (m is System.Reflection.MethodInfo nn && nn.ReturnParameter.CustomAttributes.Any(a => a.AttributeType.Name == "NotNullIfNotNullAttribute")) return false;
                if (m is System.Reflection.PropertyInfo { PropertyType.IsGenericParameter: true, GetMethod: { } gm }) m = gm;
                if (m is System.Reflection.MethodInfo { ReturnType.IsGenericParameter: true } gmi) return AnnotatedNilable(gmi);
                if (m is System.Reflection.PropertyInfo { PropertyType.IsGenericParameter: true }) return false;
                return m switch
                {
                    System.Reflection.MethodInfo mi when !mi.ReturnType.IsValueType && mi.ReturnType != typeof(void) => nic.Create(mi.ReturnParameter).ReadState == System.Reflection.NullabilityState.Nullable,
                    System.Reflection.PropertyInfo pi when !pi.PropertyType.IsValueType => nic.Create(pi).ReadState == System.Reflection.NullabilityState.Nullable,
                    System.Reflection.FieldInfo fi when !fi.FieldType.IsValueType => nic.Create(fi).ReadState == System.Reflection.NullabilityState.Nullable,
                    _ => false,
                };
            }
            catch (Exception) { return false; }
        }

        /// <summary>The compiler-written annotation of a type-parameter result: `[MaybeNull]`, or NullableAttribute 2
        /// on the return (or, absent, the NullableContextAttribute of the method or its enclosing types).</summary>
        static bool AnnotatedNilable(System.Reflection.MethodInfo mi)
        {
            var ret = mi.ReturnParameter;
            if (ret.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.MaybeNullAttribute")) return true;
            static byte? Flag(IEnumerable<System.Reflection.CustomAttributeData> attrs, string name)
            {
                var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == name);
                if (a == null || a.ConstructorArguments.Count == 0) return null;
                var v = a.ConstructorArguments[0].Value;
                return v is byte b ? b : v is IReadOnlyCollection<System.Reflection.CustomAttributeTypedArgument> xs && xs.Count > 0 && xs.First().Value is byte f ? f : null;
            }
            if (Flag(ret.CustomAttributes, "System.Runtime.CompilerServices.NullableAttribute") is { } r) return r == 2;
            for (System.Reflection.MemberInfo? at = mi; at != null; at = at.DeclaringType)
                if (Flag(at.CustomAttributes, "System.Runtime.CompilerServices.NullableContextAttribute") is { } c) return c == 2;
            return false;
        }

        /// <summary>A .NET parameter that rejects nil: a non-generic reference type annotated non-nullable.</summary>
        bool ClrRejectsNil(System.Reflection.ParameterInfo p)
        {
            try
            {
                var t = p.ParameterType;
                if (t.IsValueType || t.IsByRef || t.IsPointer || t.ContainsGenericParameters || p.Member.DeclaringType is { IsGenericType: true }) return false;
                if (p.IsDefined(typeof(ParamArrayAttribute), false)) return false;
                return nic.Create(p).WriteState == System.Reflection.NullabilityState.NotNull;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Prelude free functions (`len(s)`): every overload of that arity rejects nil at the position.</summary>
        bool PreludeRejectsNil(string name, int argc, int i)
        {
            var cands = typeof(Alice.Runtime.Prelude).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == Mangle(name) && m.GetParameters() is var ps && ps.Length >= argc && ps.Count(p => !p.IsOptional) <= argc).ToList();
            return cands.Count > 0 && cands.All(m => i < m.GetParameters().Length && ClrRejectsNil(m.GetParameters()[i]));
        }

        /// <summary>Arguments: a maybe-nil value passed where the parameter is not `T?` (an Alice method's declared
        /// parameter, a .NET parameter annotated non-nullable, a Prelude function's) is AL0310.</summary>
        void Args(List<Arg> args, MethodSym? m, object? clr = null)
        {
            var ps = m?.Decl != null ? m.Params.Where(p => !p.Block).ToList() : null;
            var cps = ps == null && clr is System.Reflection.MethodBase mb ? mb.GetParameters() : null;
            bool ext = clr is System.Reflection.MethodBase { IsStatic: true } xm && xm.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false);
            int pos = 0, idx = 0;
            foreach (var a in args)
            {
                if (a.Value == null)
                {
                    if (a.OutLocal != null) Set(LocalKey(a.OutLocal), MaybeNilType(a.OutLocal.Type));
                    idx++; pos++;
                    continue;
                }
                if (a.Mod is "out" or "ref")
                {
                    Eval(a.Value);
                    Set(Key(a.Value), MaybeNilType(a.Value.Type));
                    idx++; pos++;
                    continue;
                }
                bool v = Eval(a.Value);
                ParamSym? p = null;
                if (ps != null)
                {
                    if (a.Name != null) p = ps.FirstOrDefault(x => x.Name == a.Name);
                    else if (pos < ps.Count) { p = ps[pos]; if (!p.Splat) pos++; }
                }
                if (p != null && !p.Splat && !p.Inferred) Store(p.Type, v, a.Value);
                int ci = idx + (ext ? 1 : 0);
                if (v && a.Name == null && cps != null && ci < cps.Length && ClrRejectsNil(cps[ci])) Nil(a.Value);
                if (v && a.Name == null && clr is string fn && PreludeRejectsNil(fn, args.Count, idx)) Nil(a.Value);
                idx++;
            }
        }

        void Lambda(LambdaExpr lam)
        {
            if (cur == null) return;
            var save = cur;
            var saveTries = tries.ToList();
            tries.Clear();
            cur = cur.Clone();
            foreach (var l in lam.ParamSyms) Set(LocalKey(l), MaybeNilType(l.Type));
            // a block's result is not checked: C# infers the delegate's result (nilable included) for generic targets
            frames.Add(new Frame { Lambda = true });
            Block(lam.Body, false);
            frames.RemoveAt(frames.Count - 1);
            tries.AddRange(saveTries);
            cur = save;
        }
    }
}
