namespace Alice.Runtime;

/// <summary>
/// Lane ids for affine arenas (ADR 0037): each thread gets one lazily on its first arena allocation and keeps it for
/// life; the id is shared by every <see cref="Arena{TSlot}"/>. When a thread exits, its id (and, with it, the
/// thread's slab in every arena type) goes back to a pool and the next new thread adopts both: nodes the exited
/// thread allocated stay valid (nothing is freed early) and its memory is reused instead of leaked. At most 2^24
/// lanes exist at once. Exit is detected by the finalizer of a thread-static lease, so an id becomes reusable after
/// the GC has noticed the thread is gone.
/// </summary>
public static class ArenaLane
{
    /// <summary>Number of lane ids a process can hand out at once (2^24, the lane field of <see cref="Term"/>).</summary>
    public const int MaxLanes = Term.MaxLane + 1;

    [ThreadStatic] static Lease? t_lease;
    static int s_next, s_freeCount;
    static int[]? s_free;   // ids of exited threads; guarded by ArenaRegistry.Gate

    /// <summary>This thread's lane id, assigned on first use.</summary>
    public static int Current => t_lease?.Id ?? Assign();

    static int Assign()
    {
        int id;
        lock (ArenaRegistry.Gate)
        {
            if (s_freeCount > 0) id = s_free![--s_freeCount];
            else
            {
                id = Check(s_next);
                s_next++;
            }
        }
        t_lease = new Lease(id);
        return id;
    }

    static void Release(int id)
    {
        lock (ArenaRegistry.Gate)
        {
            var a = s_free ??= new int[8];
            if (s_freeCount == a.Length) Array.Resize(ref s_free, a.Length * 2);
            s_free[s_freeCount++] = id;
        }
    }

    /// <summary>Fail-stop at 2^24 lanes: a term cannot name a larger lane.</summary>
    internal static int Check(int id)
    {
        if ((uint)id >= MaxLanes)
            throw new InvalidOperationException($"arena lanes exhausted: {MaxLanes} (2^24) threads hold arena lanes at once");
        return id;
    }

    /// <summary>Owned by exactly one live thread; its finalizer runs once the thread has exited.</summary>
    sealed class Lease(int id)
    {
        internal readonly int Id = id;

        ~Lease() => Release(Id);
    }
}
