namespace Alice.Tests.Runtime;

/// <summary>
/// Steady-state allocation measurement for the zero-allocation tests (CLAUDE.md: allocation tests stay at 0 bytes).
/// </summary>
/// <remarks>
/// <c>GC.GetAllocatedBytesForCurrentThread</c> can move while the measured code allocates nothing:
/// <list type="bullet">
/// <item>With background GC on (the default, and the test host's setting), a GC started by another thread can retire
/// this thread's allocation context and count its unused tail as allocated. A harness on Linux x64 (.NET 10) that
/// allocates nothing inside each window saw the counter jump in 98 of 2000 windows while 8 other threads allocated
/// (sizes 7.3-8.1 KB, about one allocation quantum), in 0 of 2000 with no other allocators, and in 0 of 2000 under the
/// same load with <c>DOTNET_gcConcurrent=0</c>. The tensor (6.6-7.7 KB) and channel failures in the full suite fit this.</item>
/// <item>Shared runtime structures (the thread pool's global queue) grow on whichever thread enqueues when they are
/// full; the segment is kept and reused (see <c>ParSchedulerTests.Range_allocates_one_job</c>).</item>
/// </list>
/// Neither repeats in the next window, while an allocation in the code under test shows in every window. So a test
/// takes the least of up to <see cref="Attempts"/> windows: one window at 0 B shows the steady state allocates nothing.
/// The tests also run in <see cref="IsolatedAllocationCollection"/>, so no other test allocates or queues work meanwhile.
/// </remarks>
public static class AllocationProbe
{
    public const int Attempts = 3;

    /// <summary>Bytes <paramref name="body"/> allocated on this thread: the least over up to <see cref="Attempts"/> runs.</summary>
    public static long Least(Action body, Action<string>? log = null)
    {
        long least = long.MaxValue;
        for (int a = 0; a < Attempts && least != 0; a++)
        {
            int gcs = GC.CollectionCount(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long d = GC.GetAllocatedBytesForCurrentThread() - before;
            if (d != 0) log?.Invoke($"window {a}: {d} B with {GC.CollectionCount(0) - gcs} GCs meanwhile (discarded if a later window is 0)");
            least = Math.Min(least, d);
        }
        return least;
    }
}

/// <summary>Zero-allocation tests run alone: no other test allocates, triggers GCs or queues pool work meanwhile.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IsolatedAllocationCollection
{
    public const string Name = "isolated-allocation";
}
