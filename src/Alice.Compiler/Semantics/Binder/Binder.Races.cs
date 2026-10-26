using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Rust-like data-race freedom, checked at compile time. Code that may run on another thread (spawn bodies,
/// blocks given to Par/Parallel/Task.Run/Thread/Actor/par_*) may not mutate state it shares with other threads:
/// no writes to captured locals or fields, no mutating calls on captured non-thread-safe objects, no index writes
/// except at the block's own disjoint index, no `ref` except to Interlocked. Use Mutex(T), Atomic, Channel(T),
/// Actor(T) or System.Collections.Concurrent instead.
/// </summary>
public sealed partial class Binder
{
    bool pendingConcurrent;                 // the next lambda bound runs concurrently
    bool refAllowed;                        // binding args of an Interlocked call
    readonly List<HashSet<LocalSym>> disjoint = new();   // params of concurrent lambdas (safe per-index writes)

    static readonly HashSet<string> MutatingNames = new()
    {
        "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "Insert", "InsertRange", "Push", "Pop",
        "Enqueue", "Dequeue", "Sort", "Reverse", "Append", "AppendLine", "Set", "TryAdd", "TryRemove", "Write", "WriteLine",
        "push", "pop", "clear", "add", "sort_b", "reverse_b", "delete", "insert", "concat", "shift", "unshift",
    };

    static readonly HashSet<string> ConcurrentStatics = new() { "Par", "Parallel", "ThreadPool" };

    /// <summary>Does this call run its lambda/block arguments on other threads?</summary>
    bool IsConcurrentCall(CallExpr c, AType? recv)
    {
        string n = Mangle(c.Name);
        if (n is "par_map" or "par_sum" or "par_each" || IsActorConcurrentCall(c, recv)) return true;
        if (c.Recv is ConstExpr { Sym: AType owner } && owner.Clr != null)
        {
            if (ConcurrentStatics.Contains(owner.Clr.Name)) return true;
            if (owner.Clr == typeof(Task) && c.Name is "Run" or "Factory") return true;
        }
        if (recv?.Clr is Type rc && (rc == typeof(TaskFactory) || rc.Name.StartsWith("ParallelQuery"))) return true;
        return false;
    }

    static bool IsConcurrentType(AType t) =>
        IsActorType(t) || t.Clr is Type c && (c == typeof(Thread) || c == typeof(Task));

    static bool ThreadSafe(AType t)
    {
        var c = t.Clr;
        if (c == null) return false;
        string ns = c.Namespace ?? "";
        if (ns.StartsWith("System.Collections.Concurrent") || ns.StartsWith("System.Threading.Channels") || ns.StartsWith("System.Collections.Immutable")) return true;
        if (c == typeof(Alice.Runtime.Atomic) || ActorThreadSafe(t)) return true;
        if (c.IsGenericType)
        {
            var g = c.GetGenericTypeDefinition();
            if (g == typeof(Alice.Runtime.Channel<>) || g == typeof(Alice.Runtime.Actor<>) || g == typeof(Alice.Runtime.Mutex<>)) return true;
        }
        return c == typeof(Task) || (c.IsGenericType && c.GetGenericTypeDefinition() == typeof(Task<>)) || c == typeof(CancellationTokenSource);
    }

    bool InConcurrent => spawnDepth > 0;

    /// <summary>Lambda literals among these arguments run concurrently.</summary>
    void MarkConcurrentArgs(List<Arg> args)
    {
        foreach (var a in args)
            if (a.Value is LambdaExpr lam) concurrentLambdas.Add(lam);
    }

    readonly HashSet<LambdaExpr> concurrentLambdas = new();

    /// <summary>Visible to other threads from the current concurrent block?</summary>
    bool IsShared(Expr? e) => InConcurrent && e switch
    {
        NameExpr { Res: "local", Sym: LocalSym l } => l.SpawnDepth < spawnDepth,
        NameExpr { Res: "field" } => true,
        IvarExpr or SelfExpr => true,
        CallExpr { Res: "field" } f => IsShared(f.Recv) || f.Recv == null,
        _ => false,
    };

    CompileException Race(Node at, string what) =>
        Err(at, $"data race: {what} from code that runs concurrently; use Mutex(T), Atomic, Channel(T), Actor(T) or a System.Collections.Concurrent type");

    void CheckMutatingCall(CallExpr c, AType recv, MethodSym? alice)
    {
        if (!IsShared(c.Recv) || ThreadSafe(recv)) return;
        bool mutating = alice?.Mutates ?? MutatingNames.Contains(c.Name);
        if (mutating) throw Race(c, $"`{c.Name}` mutates a shared {recv.Cs}");
    }

    /// <summary>
    /// A call with implicit receiver (`bump`) of a method that writes fields writes self's fields: the caller mutates
    /// too (so `c.run` calling `bump` is a mutating call), and from concurrent code it races on self.
    /// </summary>
    void SelfCallEffects(Node at, MethodSym m)
    {
        if (m.IsStatic || !m.Mutates || m.IsCtor) return;
        if (curMethod is { IsStatic: false }) curMethod.Mutates = true;
        if (InConcurrent) throw Race(at, $"`{m.Name}` writes fields of self");
    }

    /// <summary>Concurrent calls whose block's first parameter is an index no other task gets: `Par.For(n) { |i| }`,
    /// `Parallel.For(a, b) { |i| }`, `Par.Range(n) { |lo, hi| }` (lo only: one chunk's hi is the next one's lo).</summary>
    static bool IsIndexCall(CallExpr c) =>
        c.Recv is ConstExpr { Sym: AType { Clr: Type t } } && (t.Name == "Par" && c.Name is "For" or "Range" || t.Name == "Parallel" && c.Name == "For");

    readonly HashSet<LambdaExpr> indexLambdas = new();

    /// <summary>The block's own disjoint index: the first parameter of an index call, unless the block assigns it.</summary>
    HashSet<LocalSym> DisjointParams(LambdaExpr lam)
    {
        var set = new HashSet<LocalSym>();
        if (!indexLambdas.Contains(lam) || lam.ParamSyms.Count == 0) return set;
        var p = lam.ParamSyms[0];
        bool moved = false;
        AstWalk.Each(lam.Body, n =>
        {
            if (n is Assign { Target: NameExpr nt } && nt.Name == p.Name || n is MultiAssign ma && ma.Names.Contains(p.Name)) moved = true;
        });
        if (!moved) set.Add(p);
        return set;
    }

    void CheckSharedTargetWrite(Expr target)
    {
        if (!InConcurrent) return;
        switch (target)
        {
            case IvarExpr iv:
                throw Race(target, $"writing field @{iv.Name}");
            case CallExpr { Res: "field" } f when IsShared(f.Recv) || f.Recv is SelfExpr:
                throw Race(target, $"writing `{f.Name}` of a shared object");
            case NameExpr { Res: "field" } nf:
                throw Race(target, $"writing field `{nf.Name}`");
            case IndexExpr ix when IsShared(ix.Target):
                bool own = ix.Target.Type.Kind == "array" && ix.Args.Count == 1 && ix.Args[0].Value is NameExpr { Sym: LocalSym i } && disjoint.Count > 0 && disjoint[^1].Contains(i);
                if (!own) throw Race(target, "writing an element of a shared collection at a non-disjoint index");
                break;
        }
    }

    void CheckRefArg(Arg a)
    {
        if (a.Mod is "ref" or "out" && !refAllowed && IsShared(a.Value))
            throw Race(a, $"passing a shared variable by {a.Mod}");
    }
}
