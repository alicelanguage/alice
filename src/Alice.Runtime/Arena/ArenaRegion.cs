namespace Alice.Runtime;

/// <summary>
/// An arena region (`scope ... end`, ADR 0037, amendment 2026-11-03): <see cref="Enter"/> records the cursor of the
/// calling thread's lane in every arena type, <see cref="Dispose"/> puts them back, so everything this thread
/// allocated inside is reclaimed at once, in O(1) per arena type. Regions are per thread: another thread's lane is
/// never touched, so a region on one thread cannot move a node another thread holds. A fork child (Par.Fork, the
/// parallel loops and kernels) that runs inside a region allocates in a lane the region leases for it; the lease's
/// marks are pushed when it is first taken and popped by Dispose, so nodes built by forks stay valid after the join
/// and are reclaimed with the region, including nodes a foreign lane consumed without freeing. Regions nest and must
/// be disposed innermost first, on the thread that entered them. Terms allocated inside a region are invalid after
/// it ends (accessing one is a bounds-checked error or reads a reused slot, never memory outside the slab).
/// 0 allocations once the nesting depth and lease count were reached before.
/// </summary>
public readonly struct ArenaRegion : IDisposable
{
    readonly RegionFrame? _frame;
    readonly int _gen;

    ArenaRegion(RegionFrame frame)
    {
        _frame = frame;
        _gen = frame.Gen;
    }

    [ThreadStatic] static RegionFrame? t_region;   // innermost region this thread allocates under (its own or a fork parent's)
    [ThreadStatic] static RegionFrame?[]? t_frames;
    [ThreadStatic] static int t_open;              // regions this thread entered and has not disposed

    /// <summary>Opens a region nested in the current one, over this thread's lane.</summary>
    public static ArenaRegion Enter()
    {
        var lane = ArenaLane.CurrentInfo;
        var frames = t_frames ??= new RegionFrame?[4];
        if (t_open == frames.Length) Array.Resize(ref t_frames, frames.Length * 2);
        var f = t_frames![t_open] ??= new RegionFrame();
        f.Open(lane, t_region);
        t_open++;
        lane.PushMarks();
        t_region = f;
        return new ArenaRegion(f);
    }

    /// <summary>Nesting depth of this region, counting the regions of the forks it runs in (1 = outermost;
    /// 0 = default value, a no-op).</summary>
    public int Depth => _frame?.Level ?? 0;

    /// <summary>Number of regions the calling thread currently allocates under.</summary>
    public static int CurrentDepth => t_region?.Level ?? 0;

    /// <summary>Resets this thread's lane, and the lanes leased to forks inside, to their cursors at <see cref="Enter"/>.</summary>
    public void Dispose()
    {
        var f = _frame;
        if (f is null) return;
        if (f.Gen == _gen && f.Owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException($"arena region entered on thread {f.Owner} disposed on thread {Environment.CurrentManagedThreadId}: a region ends on the thread that entered it");
        if (f.Gen != _gen || !ReferenceEquals(t_region, f))
            throw new InvalidOperationException($"arena region {(f.Gen == _gen ? f.Level : 0)} disposed while the innermost open region is {CurrentDepth}: regions end innermost first, once each");
        t_region = f.Parent;
        t_open--;
        f.Close();
    }

    /// <summary>The region a fork job created on this thread runs its children under (null outside regions).</summary>
    internal static RegionFrame? Context => t_region;

    /// <summary>A fork child starts on this thread: inside <paramref name="ctx"/>, bind a lane leased from it.
    /// A no-op outside regions and on a thread already allocating under <paramref name="ctx"/>.</summary>
    internal static ChildLane EnterChild(RegionFrame? ctx)
    {
        if (ctx is null || ReferenceEquals(t_region, ctx)) return default;
        var lane = ctx.Lease();
        var prevLane = ArenaLane.Bind(lane);
        var prevRegion = t_region;
        t_region = ctx;
        return new ChildLane(ctx, lane, prevLane, prevRegion);
    }

    /// <summary>The fork child ends, before it signals its join: back to the thread's previous lane and region.</summary>
    internal static void ExitChild(in ChildLane c)
    {
        if (c.Ctx is null) return;
        ArenaLane.Bind(c.PrevLane);
        t_region = c.PrevRegion;
        c.Ctx.Unlease(c.Lane!);
    }
}

/// <summary>What <see cref="ArenaRegion.EnterChild"/> changed on a fork child's thread.</summary>
readonly record struct ChildLane(RegionFrame? Ctx, LaneInfo? Lane, LaneInfo? PrevLane, RegionFrame? PrevRegion);

/// <summary>One open region of one thread, reused by depth: the lane it marked and the lanes it leased to forks.</summary>
sealed class RegionFrame
{
    internal int Owner, Gen, Level;
    internal LaneInfo? Lane;
    internal RegionFrame? Parent;
    readonly Lock _gate = new();
    LaneInfo?[] _leased = [], _spare = [];
    int _leasedCount, _spareCount;

    internal void Open(LaneInfo lane, RegionFrame? parent)
    {
        Owner = Environment.CurrentManagedThreadId;
        Lane = lane;
        Parent = parent;
        Level = (parent?.Level ?? 0) + 1;
    }

    /// <summary>A lane for a fork child: a spare one this region leased before, else a fresh lease with its marks pushed.</summary>
    internal LaneInfo Lease()
    {
        lock (_gate)
        {
            if (_spareCount > 0)
            {
                var s = _spare[--_spareCount]!;
                _spare[_spareCount] = null;
                return s;
            }
            var lane = ArenaLane.Take();
            lane.PushMarks();   // no thread is bound to it yet
            if (_leasedCount == _leased.Length)
            {
                Array.Resize(ref _leased, Math.Max(4, _leased.Length * 2));
                Array.Resize(ref _spare, _leased.Length);
            }
            _leased[_leasedCount++] = lane;
            return lane;
        }
    }

    internal void Unlease(LaneInfo lane)
    {
        lock (_gate) _spare[_spareCount++] = lane;
    }

    /// <summary>Every fork has joined: pop the leased lanes (back to the lane pool), then this thread's lane.</summary>
    internal void Close()
    {
        lock (_gate)
        {
            for (int i = 0; i < _leasedCount; i++)
            {
                var l = _leased[i]!;
                _leased[i] = null;
                l.PopMarks();
                ArenaLane.Return(l);
            }
            Array.Clear(_spare, 0, _spareCount);
            _leasedCount = _spareCount = 0;
        }
        Lane!.PopMarks();
        Lane = null;
        Parent = null;
        Gen++;
    }
}

/// <summary>An arena type as a region sees it: per-lane cursor stacks and this thread's cached slab.</summary>
abstract class ArenaKind
{
    internal abstract void PushMark(int lane);
    internal abstract void PopMark(int lane);

    /// <summary>Sets this thread's cached slab of this type to <paramref name="lane"/>'s (null: attach lazily).</summary>
    internal abstract void Bind(int lane);
}

/// <summary>Arena types in use; mutated only under <see cref="Gate"/>.</summary>
static class ArenaRegistry
{
    static Lock? s_gate;
    static ArenaKind[]? s_kinds;

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

    /// <summary>A snapshot of the arena types: the array is replaced, never mutated, by <see cref="Add"/>.</summary>
    internal static ArenaKind[] Kinds => Volatile.Read(ref s_kinds) ?? [];

    /// <summary>Caller holds <see cref="Gate"/>.</summary>
    internal static void Add(ArenaKind kind)
    {
        var a = s_kinds ?? [];
        var b = new ArenaKind[a.Length + 1];
        a.CopyTo(b, 0);
        b[a.Length] = kind;
        Volatile.Write(ref s_kinds, b);
    }
}
