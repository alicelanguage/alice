using System.Collections;
using System.Reflection;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Rust-inspired aliasing and lifetime checks over the bound AST (ADR 0024), run once after binding:
/// <list type="bullet">
/// <item>AL0217 conflicting borrow: one mutable value passed twice to the same Alice call while the callee writes
/// through one of those parameters (`copy_into(a, a)`), the `&amp;mut` + `&amp;` rule.</item>
/// <item>AL0218 stack value escapes: a `@[Stack]` value captured by a block or lambda, stored in a field of a heap
/// type, or put in a collection; it lives in its frame only.</item>
/// <item>AL0219 use after send: a mutable value sent through `Channel(T)`/`Actor(T)` belongs to the receiver; the
/// sender may not touch it until it rebinds the name (the move half of Rust's `Send`).</item>
/// <item>AL0202 (feeds the race checker): writing a value a running `spawn` reads before the task is joined
/// (the `Sync` half: shared reads and a concurrent write).</item>
/// </list>
/// </summary>
public sealed partial class Binder
{
    void CheckBorrowsAndLifetimes()
    {
        foreach (var t in Types.Values.Distinct())
            if (!t.IsStack && t.Decl != null)
                foreach (var f in t.Fields.Values)
                    if (!f.IsStatic && ContainsStack(f.Type) is { } st)
                        throw Err(t.Decl, $"stack value of @[Stack] type {st.Name} cannot be stored in field `{f.Name}` of heap type {t.Name}; mark {t.Name} @[Stack] or keep the value local");
        foreach (var f in Files)
        {
            file = f.Path;
            CheckBody(f.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList());
            CheckItems(f.Items);
        }
    }

    void CheckItems(List<Item> items)
    {
        foreach (var it in items)
            switch (it)
            {
                case NamespaceItem n: CheckItems(n.Items); break;
                case TypeDecl d: CheckItems(d.Members); break;
                case MethodDecl { IsTemplate: false, HasBody: true } m:
                    file = FileOf(m);
                    CheckBody(m.Body);
                    break;
            }
    }

    void CheckBody(List<Stmt> body)
    {
        if (body.Count == 0) return;
        AstWalk.Each(body, n =>
        {
            switch (n)
            {
                case CallExpr c: CheckAliasedArgs(c); break;
                case LambdaExpr lam: CheckStackCapture(lam); break;
                case Expr e when e is not NameExpr && e.Type.Kind != "func" && ContainsStack(e.Type) is { } st && !(e.Type.Sym?.IsStack ?? false):
                    throw Err(e, $"stack value of @[Stack] type {st.Name} cannot be stored in a collection ({e.Type.Cs}); it lives only in its frame");
            }
        });
        foreach (var list in StmtLists(body)) { CheckSends(list); CheckSpawnWrites(list); }
    }

    // ---- AL0217 ----

    void CheckAliasedArgs(CallExpr c)
    {
        if (c.Sym is not MethodSym { Decl: { HasBody: true } } m) return;
        var seen = new Dictionary<LocalSym, int>();
        for (int i = 0; i < c.Args.Count && i < m.Params.Count; i++)
        {
            var a = c.Args[i];
            if (a.Name != null || m.Params[i].Splat || m.Params[i].Block) return;
            if (a.Value is not NameExpr { Sym: LocalSym l } || !MutableRef(l.Type)) continue;
            if (seen.TryGetValue(l, out int j) && (WritesParam(m, i) || WritesParam(m, j)))
                throw Err(a, $"conflicting borrow: `{l.Name}` is passed as both `{m.Params[j].Name}` and `{m.Params[i].Name}` of {m.Name}, which writes through one of them; pass a copy (`{l.Name}.dup`) or a different value");
            seen.TryAdd(l, i);
        }
    }

    readonly Dictionary<(MethodSym, int), bool> paramWrites = new();

    /// <summary>Does m write through parameter k (element store, mutating call, or lending it to a writing parameter)?</summary>
    bool WritesParam(MethodSym m, int k)
    {
        if (paramWrites.TryGetValue((m, k), out var known)) return known;
        paramWrites[(m, k)] = false;   // recursion: assume read-only while analysing
        var p = m.Params[k].Local;
        bool w = false;
        if (p != null && m.Decl != null)
            AstWalk.Each(m.Decl.Body, n => w |= n switch
            {
                Assign { Target: IndexExpr { Target: NameExpr { Sym: LocalSym t } } } => t == p,
                CallExpr { Recv: NameExpr { Sym: LocalSym r } } c when r == p => c.Sym is MethodSym am ? am.Mutates : MutatingNames.Contains(c.Name),
                CallExpr { Sym: MethodSym callee } c => LendsToWriter(c, callee, p),
                _ => false,
            });
        return paramWrites[(m, k)] = w;
    }

    bool LendsToWriter(CallExpr c, MethodSym callee, LocalSym p)
    {
        if (callee.Decl is not { HasBody: true }) return false;
        for (int i = 0; i < c.Args.Count && i < callee.Params.Count; i++)
            if (c.Args[i].Name == null && c.Args[i].Value is NameExpr { Sym: LocalSym l } && l == p && WritesParam(callee, i)) return true;
        return false;
    }

    // ---- AL0218 ----

    static TypeSym? ContainsStack(AType? t, int depth = 0)
    {
        if (t == null || depth > 8) return null;
        if (t.Sym is { IsStack: true } s) return s;
        if (ContainsStack(t.Elem, depth + 1) is { } e) return e;
        foreach (var a in t.Args) if (ContainsStack(a, depth + 1) is { } x) return x;
        return null;
    }

    void CheckStackCapture(LambdaExpr lam)
    {
        var own = new HashSet<LocalSym>(lam.ParamSyms);
        AstWalk.Each(lam.Body, n => { if (n is Assign { Declares: { } d }) own.Add(d); });
        AstWalk.Each(lam.Body, n =>
        {
            if (n is NameExpr { Sym: LocalSym l } ne && l.Type.Sym is { IsStack: true } && !own.Contains(l) && !lam.ParamSyms.Any(ps => ps.Name == l.Name))
                throw Err(ne, $"stack value `{l.Name}` of @[Stack] type {l.Type.Sym.Name} cannot be captured by a block or lambda; pass the fields it needs instead");
        });
    }

    // ---- AL0219 ----

    /// <summary>A reference the receiver could mutate (or observe mutated): arrays, non-thread-safe .NET collections, Alice classes.</summary>
    static bool MutableRef(AType t)
    {
        if (t.Kind == "array") return true;
        if (t.Sym is { } s) return s.Kind == "class" && !s.IsStack;
        if (t.Clr is not Type c || c.IsValueType || c == typeof(string) || c == typeof(object) || ThreadSafe(t)) return false;
        return c.Namespace == "System.Collections.Generic" || c == typeof(System.Text.StringBuilder);
    }

    static bool IsChannelOrActor(AType? t) =>
        t?.Clr is { IsGenericType: true } c && (c.GetGenericTypeDefinition() == typeof(Alice.Runtime.Channel<>) || c.GetGenericTypeDefinition() == typeof(Alice.Runtime.Actor<>));

    void CheckSends(List<Stmt> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            LocalSym? sent = null;
            AstWalk.Each(list[i], n =>
            {
                if (n is CallExpr { Name: "send" or "send_async", Args.Count: 1 } c && IsChannelOrActor(c.Recv?.Type)
                    && c.Args[0].Value is NameExpr { Sym: LocalSym l } && MutableRef(l.Type)) sent = l;
            });
            if (sent == null) continue;
            for (int j = i + 1; j < list.Count; j++)
            {
                var s = list[j];
                if (s is Assign { Op: "=", Target: NameExpr { Sym: LocalSym t } } a && t == sent)
                {
                    if (Mentions(a.Value, sent) is { } early) throw SentErr(early, sent);
                    break;
                }
                if (Mentions(s, sent) is { } use) throw SentErr(use, sent);
            }
        }
    }

    CompileException SentErr(Node at, LocalSym l) =>
        Err(at, $"`{l.Name}` was sent to another thread and belongs to the receiver now; send a copy (`{l.Name}.dup`) or rebind `{l.Name}` before using it");

    static Node? Mentions(Node n, LocalSym l)
    {
        Node? hit = null;
        AstWalk.Each(n, x => { if (hit == null && x is NameExpr { Sym: LocalSym s } && s == l) hit = x; });
        return hit;
    }

    // ---- AL0202: write while a spawned task reads ----

    void CheckSpawnWrites(List<Stmt> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            SpawnExpr? sp = null;
            AstWalk.Each(list[i], n => { if (sp == null && n is SpawnExpr x) sp = x; });
            if (sp == null) continue;
            LocalSym? task = list[i] is Assign { Target: NameExpr { Sym: LocalSym t } } ? t : null;
            var inner = new HashSet<LocalSym>(sp.Body.ParamSyms);
            AstWalk.Each(sp.Body.Body, n => { if (n is Assign { Declares: { } d }) inner.Add(d); });
            var read = new HashSet<LocalSym>();
            AstWalk.Each(sp.Body.Body, n => { if (n is NameExpr { Sym: LocalSym l } && !inner.Contains(l) && l != task && MutableRef(l.Type)) read.Add(l); });
            if (read.Count == 0) continue;
            for (int j = i + 1; j < list.Count; j++)
            {
                bool joined = false;
                AstWalk.Each(list[j], n => joined |= n is AwaitExpr || (task != null && n is NameExpr { Sym: LocalSym s } && s == task));
                if (joined) break;
                AstWalk.Each(list[j], n =>
                {
                    LocalSym? w = n switch
                    {
                        Assign { Target: NameExpr { Sym: LocalSym t } } => t,
                        Assign { Target: IndexExpr { Target: NameExpr { Sym: LocalSym t } } } => t,
                        CallExpr { Recv: NameExpr { Sym: LocalSym r } } c when (c.Sym is MethodSym am ? am.Mutates : MutatingNames.Contains(c.Name)) => r,
                        _ => null,
                    };
                    if (w != null && read.Contains(w))
                        throw Race(n, $"writing `{w.Name}` while a spawned task may still read it (join the task first{(task != null ? $", `{task.Name}.wait`" : "")})");
                });
            }
        }
    }

    // ---- helpers ----

    static readonly Dictionary<Type, FieldInfo[]> stmtListFields = new();

    /// <summary>The body and every nested statement list (branches, loop bodies, blocks), outside nested definitions.</summary>
    static List<List<Stmt>> StmtLists(List<Stmt> body)
    {
        var all = new List<List<Stmt>> { body };
        AstWalk.Each(body, n =>
        {
            FieldInfo[] fs;
            lock (stmtListFields)
                if (!stmtListFields.TryGetValue(n.GetType(), out fs!))
                    stmtListFields[n.GetType()] = fs = n.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                        .Where(f => f.FieldType == typeof(List<Stmt>) || f.FieldType == typeof(List<List<Stmt>>)).ToArray();
            foreach (var f in fs)
                switch (f.GetValue(n))
                {
                    case List<Stmt> l: all.Add(l); break;
                    case List<List<Stmt>> ll: all.AddRange(ll); break;
                }
        });
        return all;
    }
}
