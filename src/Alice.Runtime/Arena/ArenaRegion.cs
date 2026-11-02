namespace Alice.Runtime;

/// <summary>
/// An arena region (`scope ... end`, ADR 0037): <see cref="Enter"/> records every lane's cursor of every arena type
/// in use, <see cref="Dispose"/> puts them back, so everything allocated inside is reclaimed at once, in O(1) per
/// lane and type, including nodes a foreign lane consumed without freeing. Regions nest and must be disposed
/// innermost first. Enter and Dispose touch every lane: call them where the region's forks have joined and no other
/// thread allocates in arenas. Terms allocated inside a region are invalid after it ends (accessing one is a
/// bounds-checked error or reads a reused slot, never memory outside the slab). 0 allocations once the nesting
/// depth was reached before.
/// </summary>
public readonly struct ArenaRegion : IDisposable
{
    readonly int _depth;

    ArenaRegion(int depth) => _depth = depth;

    /// <summary>Opens a region nested in the current one.</summary>
    public static ArenaRegion Enter()
    {
        lock (ArenaRegistry.Gate)
        {
            int depth = ++ArenaRegistry.Depth;
            ArenaRegistry.ForEach(static k => k.PushMarks());
            return new ArenaRegion(depth);
        }
    }

    /// <summary>Nesting depth of this region (1 = outermost; 0 = default value, a no-op).</summary>
    public int Depth => _depth;

    /// <summary>Number of regions currently open.</summary>
    public static int CurrentDepth => Volatile.Read(ref ArenaRegistry.Depth);

    /// <summary>Resets every lane of every arena type to its cursor at <see cref="Enter"/>.</summary>
    public void Dispose()
    {
        if (_depth == 0) return;
        lock (ArenaRegistry.Gate)
        {
            if (ArenaRegistry.Depth != _depth)
                throw new InvalidOperationException($"arena region {_depth} disposed while the innermost open region is {ArenaRegistry.Depth}: regions end innermost first, once each");
            ArenaRegistry.ForEach(static k => k.PopMarks());
            ArenaRegistry.Depth--;
        }
    }
}

/// <summary>An arena type as the region sees it: every lane's cursor stack.</summary>
abstract class ArenaKind
{
    internal abstract void PushMarks();
    internal abstract void PopMarks();
}

/// <summary>Arena types in use and the region depth; mutated only under <see cref="Gate"/>.</summary>
static class ArenaRegistry
{
    static Lock? s_gate;
    static ArenaKind[]? s_kinds;
    static int s_count;
    internal static int Depth;

    internal static Lock Gate
    {
        get
        {
            var g = Volatile.Read(ref s_gate);
            if (g is not null) return g;
            Interlocked.CompareExchange(ref s_gate, new Lock(), null);
            return s_gate!;
        }
    }

    /// <summary>Caller holds <see cref="Gate"/>.</summary>
    internal static void Add(ArenaKind kind)
    {
        var a = s_kinds ?? new ArenaKind[8];
        if (s_count == a.Length) Array.Resize(ref a, a.Length * 2);
        a[s_count++] = kind;
        s_kinds = a;
    }

    /// <summary>Caller holds <see cref="Gate"/>. The action is a static lambda (cached, no allocation).</summary>
    internal static void ForEach(Action<ArenaKind> f)
    {
        var a = s_kinds;
        for (int i = 0; i < s_count; i++) f(a![i]);
    }
}
