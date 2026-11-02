namespace Alice.Runtime;

/// <summary>
/// Lane ids for affine arenas (ADR 0037): each thread gets one lazily on its first arena allocation and keeps it for
/// life; the id is shared by every <see cref="Arena{TSlot}"/>. Ids are never reused, at most 2^24 per process.
/// </summary>
public static class ArenaLane
{
    /// <summary>Number of lane ids a process can hand out (2^24, the lane field of <see cref="Term"/>).</summary>
    public const int MaxLanes = Term.MaxLane + 1;

    [ThreadStatic] static int t_lane;   // id + 1; 0 = not assigned yet
    static int s_next;

    /// <summary>This thread's lane id, assigned on first use.</summary>
    public static int Current
    {
        get
        {
            int l = t_lane;
            return l != 0 ? l - 1 : Assign();
        }
    }

    static int Assign()
    {
        int id = Check(Interlocked.Increment(ref s_next) - 1);
        t_lane = id + 1;
        return id;
    }

    /// <summary>Fail-stop at 2^24 lanes: a term cannot name a larger lane.</summary>
    internal static int Check(int id)
    {
        if ((uint)id >= MaxLanes)
            throw new InvalidOperationException($"arena lanes exhausted: {MaxLanes} (2^24) threads already allocated in arenas; lane ids are never reused");
        return id;
    }
}
