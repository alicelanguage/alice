namespace Alice.Runtime;

/// <summary>
/// Lane ids for affine arenas (ADR 0037): each thread gets one lazily on its first arena allocation and keeps it for
/// life; the id is shared by every <see cref="Arena{TSlot}"/>. When a thread exits, its id (and, with it, the
/// thread's slab in every arena type) goes back to a pool and the next new thread adopts both: nodes the exited
/// thread allocated stay valid (nothing is freed early) and its memory is reused instead of leaked. At most 2^24
/// lanes exist at once. Exit is detected by the finalizer of a thread-static lease, so an id becomes reusable after
/// the GC has noticed the thread is gone. A fork child that runs inside an <see cref="ArenaRegion"/> is bound to a
/// lane the region leased for it (amendment 2026-11-03), and returns to its own lane when the child ends.
/// </summary>
public static class ArenaLane
{
    /// <summary>Number of lane ids a process can hand out at once (2^24, the lane field of <see cref="Term"/>).</summary>
    public const int MaxLanes = Term.MaxLane + 1;

    [ThreadStatic] static Lease? t_lease;
    [ThreadStatic] static LaneInfo? t_bound;   // a region-leased lane this thread allocates in; null = its own
    static int s_next, s_freeCount;
    static int[]? s_free;          // ids of exited threads and ended leases; guarded by ArenaRegistry.Gate
    static LaneInfo?[]? s_infos;   // per id, created once; guarded by ArenaRegistry.Gate

    /// <summary>The lane this thread allocates in, assigned on first use.</summary>
    public static int Current => CurrentInfo.Id;

    internal static LaneInfo CurrentInfo => t_bound ?? t_lease?.Info ?? Assign();

    /// <summary>This thread's own lane id, or -1 before its first arena use.</summary>
    static int OwnId => t_lease?.Info.Id ?? -1;

    static LaneInfo Assign()
    {
        var info = Take();
        t_lease = new Lease(info);
        return info;
    }

    /// <summary>A lane no thread owns, for a region's fork children; back to the pool with <see cref="Return"/>.</summary>
    internal static LaneInfo Take()
    {
        lock (ArenaRegistry.Gate)
        {
            int id;
            if (s_freeCount > 0) id = s_free![--s_freeCount];
            else
            {
                id = Check(s_next);
                s_next++;
            }
            var a = s_infos ??= new LaneInfo?[8];
            if (id >= a.Length) Array.Resize(ref s_infos, Math.Max(a.Length * 2, id + 1));
            return s_infos[id] ??= new LaneInfo(id);
        }
    }

    internal static void Return(LaneInfo info)
    {
        lock (ArenaRegistry.Gate)
        {
            var a = s_free ??= new int[8];
            if (s_freeCount == a.Length) Array.Resize(ref s_free, a.Length * 2);
            s_free[s_freeCount++] = info.Id;
        }
    }

    /// <summary>Binds this thread to <paramref name="info"/> (null: back to its own lane) in every arena type.</summary>
    internal static LaneInfo? Bind(LaneInfo? info)
    {
        var prev = t_bound;
        t_bound = info;
        int id = info?.Id ?? OwnId;
        foreach (var k in ArenaRegistry.Kinds) k.Bind(id);
        return prev;
    }

    /// <summary>Fail-stop at 2^24 lanes: a term cannot name a larger lane.</summary>
    internal static int Check(int id)
    {
        if ((uint)id >= MaxLanes)
            throw new InvalidOperationException($"arena lanes exhausted: {MaxLanes} (2^24) threads hold arena lanes at once");
        return id;
    }

    /// <summary>Owned by exactly one live thread; its finalizer runs once the thread has exited.</summary>
    sealed class Lease(LaneInfo info)
    {
        internal readonly LaneInfo Info = info;

        ~Lease() => Return(Info);
    }
}

/// <summary>One lane id and its region depth (marks pushed on its slabs). Mutated only by the thread bound to the
/// lane, or by a region over a leased lane no thread is bound to.</summary>
sealed class LaneInfo(int id)
{
    internal readonly int Id = id;
    internal int Depth;

    internal void PushMarks()
    {
        Depth++;
        foreach (var k in ArenaRegistry.Kinds) k.PushMark(Id);
    }

    internal void PopMarks()
    {
        Depth--;
        foreach (var k in ArenaRegistry.Kinds) k.PopMark(Id);
    }
}
