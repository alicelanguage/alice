using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Alice.Runtime;
using Xunit.Abstractions;

namespace Alice.Tests.Runtime;

/// <summary>
/// Stress for the dataflow wake protocol (ADR 0038): chains of more stages than processors over rings of 2-4 slots,
/// with seeded <see cref="Thread.Yield"/>/<see cref="Thread.SpinWait(int)"/> injection points in every stage body so
/// a stage is descheduled anywhere in its turn. Two failures are told apart: no progress at all for
/// <see cref="Stall"/> (a lost wake: every node idle with work pending; the node states are dumped) and progress
/// that is too slow (idle nodes holding the cores or pool threads that the busy ones need). The second is what
/// 2-4 core CI runners hit: before idle nodes yielded and stopped spinning when work was queued, a round here took
/// more than 20 s on two cores, after it about 1 s. Run alone: it pins the process to two cores.
/// </summary>
[Trait("Category", "Stress")]
[Collection(IsolatedAllocationCollection.Name)]
public class DataflowStressTests(ITestOutputHelper output)
{
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Round = TimeSpan.FromSeconds(20);
    // Pinned to two cores a round took 6-9 s on the forge (all 28 pool threads on 2 CPUs; more than 20 s before the
    // fix, about 110 s projected). CI runners vary in speed and pool size, so the pinned bound has room to spare.
    static readonly TimeSpan PinnedRound = TimeSpan.FromSeconds(40);

    [Theory]
    [InlineData(2, 1, 1u)]
    [InlineData(4, 3, 2u)]
    public void Oversubscribed_chain_of_tiny_rings_neither_stalls_nor_crawls(int capacity, int batch, uint seed) =>
        RunChain(capacity, batch, seed);

    [Theory]
    [InlineData(2, 1, 21u)]
    [InlineData(4, 3, 22u)]
    public void Chain_of_tiny_rings_on_two_cores_neither_stalls_nor_crawls(int capacity, int batch, uint seed)
    {
        using var pin = TwoCores.Pin();
        if (pin == null) output.WriteLine("no thread affinity on this platform: ran unpinned");
        RunChain(capacity, batch, seed, PinnedRound);
    }

    void RunChain(int capacity, int batch, uint seed, TimeSpan? limit = null)
    {
        const int N = 20_000;
        var bound = limit ?? Round;
        int stages = 2 * Environment.ProcessorCount + 4;   // more runnable nodes than cores
        for (int round = 0; round < 3; round++)
        {
            var g = new Graph(capacity, batch);
            var p = g.Source(Enumerable.Range(0, N).Select(i => (long)i).ToArray());
            for (int s = 0; s < stages; s++)
                p = p.Stage(seed * 2654435761u + (uint)(s * 40503 + round * 97 + 1), static (ref uint r, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
                {
                    foreach (var x in xs)
                    {
                        r ^= r << 13; r ^= r >> 17; r ^= r << 5;   // xorshift32: deterministic injection points
                        if ((r & 63) == 0) Thread.Yield();
                        else if ((r & 255) == 1) Thread.SpinWait(200);
                        w.Write(x + 1);
                    }
                });
            p = p.Replicate(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) => w.Write(xs), workers: 3, batch: batch);
            var sink = p.Sink((next: (long)stages, ok: true, seen: new Box()), static (ref (long next, bool ok, Box seen) st, ReadOnlySpan<long> xs) =>
            {
                foreach (var x in xs) { st.ok &= x == st.next; st.next++; }
                Volatile.Write(ref st.seen.Value, st.next);
            });
            Exception? error = null;
            var t = new Thread(() => { try { g.Run(); } catch (Exception e) { error = e; } }) { IsBackground = true };
            var sw = Stopwatch.StartNew();
            t.Start();
            long seen = 0;
            var lastMove = sw.Elapsed;
            while (!t.Join(100))
            {
                long now = Volatile.Read(ref sink.State.seen.Value);
                if (now != seen) { seen = now; lastMove = sw.Elapsed; }
                else if (sw.Elapsed - lastMove > Stall)
                    Assert.Fail($"lost wake: no progress for {Stall.TotalSeconds} s (round {round}, {Math.Max(0, seen - stages)}/{N} items)\n{Dump(g)}");
                if (sw.Elapsed > bound)
                    Assert.Fail($"too slow: round {round} still running after {bound.TotalSeconds} s ({Math.Max(0, seen - stages)}/{N} items, {stages} stages)");
            }
            output.WriteLine($"round {round}: {sw.ElapsedMilliseconds} ms ({stages} stages)");
            if (error != null) throw error;
            Assert.True(sink.State.ok);
            Assert.Equal(N + stages, sink.State.next);
        }
    }

    sealed class Box { public long Value; }

    /// <summary>Node states (scheduled/blocked flags, ring fill) for a stall report, read by reflection.</summary>
    static string Dump(Graph g)
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        var sb = new System.Text.StringBuilder();
        sb.Append("remaining=").Append(typeof(Graph).GetField("remaining", F)?.GetValue(g)).Append('\n');
        if (typeof(Graph).GetField("nodes", F)?.GetValue(g) is not IList nodes) return sb.ToString();
        foreach (var n in nodes)
        {
            var type = n!.GetType();
            var bt = type.BaseType!;
            sb.Append(type.Name.Split((char)96)[0]).Append(" scheduled=").Append(bt.GetField("scheduled", F)?.GetValue(n))
              .Append(" blocked=").Append(bt.GetField("blocked", F)?.GetValue(n));
            foreach (var f in new[] { "input", "output" })
                if (type.GetField(f, F)?.GetValue(n) is { } r)
                    sb.Append(' ').Append(f).Append(".count=").Append(r.GetType().GetProperty("Count")!.GetValue(r))
                      .Append(" completed=").Append(r.GetType().GetProperty("IsCompleted")!.GetValue(r));
            foreach (var f in new[] { "inflight", "nextIn", "nextOut" })
                if (type.GetField(f, F)?.GetValue(n) is { } v) sb.Append(' ').Append(f).Append('=').Append(v);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pins every thread of the process to its first two allowed CPUs until disposed (null where thread affinity is
    /// not available). Linux sets the affinity per thread, so both pinning and restoring walk /proc/self/task (a
    /// thread created while pinned inherits the pin and is restored too); Windows sets it for the process.
    /// </summary>
    sealed class TwoCores : IDisposable
    {
        [DllImport("libc", SetLastError = true)] static extern int sched_getaffinity(int pid, nint size, out ulong mask);
        [DllImport("libc", SetLastError = true)] static extern int sched_setaffinity(int pid, nint size, ref ulong mask);

        readonly ulong saved;

        TwoCores(ulong saved) => this.saved = saved;

        public static TwoCores? Pin()
        {
            ulong all;
            if (OperatingSystem.IsLinux()) { if (sched_getaffinity(0, sizeof(ulong), out all) != 0) return null; }
            else if (OperatingSystem.IsWindows()) all = (ulong)Process.GetCurrentProcess().ProcessorAffinity;
            else return null;
            ulong first = all & (~all + 1), rest = all & ~first, two = first | (rest & (~rest + 1));
            Apply(two);
            return new TwoCores(all);
        }

        static void Apply(ulong mask)
        {
            if (OperatingSystem.IsWindows()) { Process.GetCurrentProcess().ProcessorAffinity = (nint)mask; return; }
            foreach (var dir in Directory.GetDirectories("/proc/self/task"))
                if (int.TryParse(Path.GetFileName(dir), out int tid)) sched_setaffinity(tid, sizeof(ulong), ref mask);
        }

        public void Dispose()
        {
            Apply(saved);
            if (OperatingSystem.IsLinux()) Apply(saved);   // threads started while the first pass ran
        }
    }
}
