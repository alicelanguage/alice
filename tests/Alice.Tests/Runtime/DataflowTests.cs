using System.Diagnostics;
using Alice.Runtime;
using Xunit.Abstractions;

namespace Alice.Tests.Runtime;

/// <summary>
/// Static dataflow networks (ADR 0038): the SPSC ring, Source -> Stage -> Sink graphs on the thread pool,
/// backpressure, exception propagation, replicated stages (ordered and unordered) and the zero-allocation steady
/// state.
/// </summary>
public class DataflowTests(ITestOutputHelper output)
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(30);

    /// <summary>Runs the graph on another thread so a hang fails the test instead of the run.</summary>
    static void RunWithin(Graph g)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { g.Run(); }
            catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        });
        t.Start();
        Assert.True(t.Join(T), "dataflow graph did not finish");
        error?.Throw();
    }

    static long[] Range(int n)
    {
        var a = new long[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    // ---- ring ----

    [Fact]
    public void Ring_rounds_capacity_and_is_fifo()
    {
        var r = new Ring<int>(5);
        Assert.Equal(8, r.Capacity);
        for (int i = 0; i < 8; i++) Assert.True(r.TryWrite(i));
        Assert.False(r.TryWrite(99));
        Assert.Equal(8, r.Count);
        for (int i = 0; i < 8; i++) { Assert.True(r.TryRead(out var x)); Assert.Equal(i, x); }
        Assert.False(r.TryRead(out _));
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void Ring_span_reads_and_writes_wrap_around()
    {
        var r = new Ring<int>(8);
        Span<int> buf = stackalloc int[8];
        int next = 0, expect = 0;
        for (int round = 0; round < 100; round++)
        {
            int[] chunk = [next, next + 1, next + 2, next + 3, next + 4];
            int w = r.TryWrite(chunk);
            next += w;
            int n = r.TryRead(buf[..3]);
            for (int i = 0; i < n; i++) Assert.Equal(expect++, buf[i]);
        }
        while (r.TryRead(out var x)) Assert.Equal(expect++, x);
        Assert.Equal(next, expect);
        Assert.True(r.TryWrite(new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }) == 8);
    }

    [Fact]
    public void Ring_peek_release_and_completion()
    {
        var r = new Ring<string>(4);
        r.TryWrite("a"); r.TryWrite("b"); r.TryWrite("c");
        var s = r.Peek(2);
        Assert.Equal(["a", "b"], s.ToArray());
        r.Release(2);
        r.Complete();
        Assert.False(r.IsCompleted);   // "c" is still there
        Assert.True(r.TryRead(out var c) && c == "c");
        Assert.True(r.IsCompleted);
    }

    [Fact]
    public void Ring_spsc_across_threads_keeps_order()
    {
        const int N = 2_000_000;
        var r = new Ring<long>(1024);
        var producer = new Thread(() =>
        {
            for (long i = 0; i < N;) if (r.TryWrite(i)) i++; else Thread.SpinWait(4);
            r.Complete();
        });
        producer.Start();
        long expect = 0;
        var buf = new long[100];
        while (!r.IsCompleted)
        {
            int n = r.TryRead(buf);
            for (int i = 0; i < n; i++) Assert.Equal(expect++, buf[i]);
        }
        producer.Join();
        Assert.Equal(N, expect);
    }

    [Fact]
    public void Ring_read_write_allocates_nothing()
    {
        var r = new Ring<long>(64);
        Span<long> buf = stackalloc long[16];
        r.TryWrite(1); r.TryRead(out _); r.TryWrite(buf); r.TryRead(buf);   // JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            r.TryWrite(i);
            r.TryWrite(buf[..7]);
            r.TryRead(out _);
            r.TryRead(buf[..7]);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- graphs ----

    [Fact]
    public void Linear_graph_maps_filters_and_expands_in_order()
    {
        const int N = 200_000;
        var g = new Graph(capacity: 256, batch: 32);
        var sink = g.Source(Range(N))
            .Stage(0L, static (ref long _, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
            {
                foreach (var x in xs) w.Write(x * 3);
            })
            .Stage(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
            {
                foreach (var x in xs)
                    if (x % 2 == 0) { w.Write(x); w.Write(-x); }   // 1:2 for evens, 1:0 for odds
            })
            .Sink(new List<long>(), static (ref List<long> acc, ReadOnlySpan<long> xs) => acc.AddRange(xs));
        RunWithin(g);
        var expect = new List<long>();
        for (long i = 0; i < N; i++) if (i * 3 % 2 == 0) { expect.Add(i * 3); expect.Add(-i * 3); }
        Assert.Equal(expect, sink.State);
    }

    [Fact]
    public void Stateful_stage_and_finish_see_the_whole_stream()
    {
        // running sum, plus one final item written at end of stream
        var g = new Graph(capacity: 64, batch: 8);
        var sink = g.Source(Range(1000))
            .Stage(0L, static (ref long sum, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
            {
                foreach (var x in xs) { sum += x; w.Write(sum); }
            }, finish: static (ref long sum, ref RingWriter<long> w) => w.Write(-sum))
            .Sink(new List<long>(), static (ref List<long> acc, ReadOnlySpan<long> xs) => acc.AddRange(xs));
        RunWithin(g);
        Assert.Equal(1001, sink.State.Count);
        Assert.Equal(999L * 1000 / 2, sink.State[999]);
        Assert.Equal(-(999L * 1000 / 2), sink.State[1000]);
    }

    [Fact]
    public void Custom_source_and_empty_source_complete()
    {
        var g = new Graph();
        var sink = g.Source(0, static (ref int i, ref RingWriter<int> w) =>
        {
            while (i < 10_000 && w.HasRoom) w.Write(i++);
            return i < 10_000;
        }).Sink(0L, static (ref long s, ReadOnlySpan<int> xs) => { foreach (var x in xs) s += x; });
        RunWithin(g);
        Assert.Equal(9999L * 10000 / 2, sink.State);

        var g2 = new Graph();
        int calls = 0;
        var empty = g2.Source(Array.Empty<int>()).Stage(0, (ref int _, ReadOnlySpan<int> xs, ref RingWriter<int> w) => w.Write(xs))
            .Sink(0, (ref int s, ReadOnlySpan<int> xs) => { calls++; s += xs.Length; });
        RunWithin(g2);
        Assert.Equal(0, empty.State);
        Assert.Equal(0, calls);
    }

    static void TimesTwo(ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w)
    {
        foreach (var x in xs) w.Write(2 * x);
    }

    static void Sum(ref long s, ReadOnlySpan<long> xs)
    {
        foreach (var x in xs) s += x;
    }

    [Fact]
    public unsafe void Function_pointer_bodies()
    {
        var g = new Graph();
        var sink = g.Source(Range(5000)).Stage<int, long>(0, &TimesTwo).Replicate<int, long>(0, &TimesTwo, workers: 3).Sink<long>(0, &Sum);
        RunWithin(g);
        Assert.Equal(4 * (4999L * 5000 / 2), sink.State);
    }

    [Fact]
    public void Builder_enforces_single_consumer_and_one_run()
    {
        var g = new Graph();
        var p = g.Source(Range(3));
        p.Sink(0, static (ref int _, ReadOnlySpan<long> _) => { });
        Assert.Throws<InvalidOperationException>(() => p.Sink(0, static (ref int _, ReadOnlySpan<long> _) => { }));
        RunWithin(g);
        Assert.Throws<InvalidOperationException>(g.Run);

        var g2 = new Graph();
        g2.Source(Range(3)).Stage(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) => w.Write(xs));
        Assert.Throws<InvalidOperationException>(g2.Run);   // dangling pipe
    }

    [Fact]
    public void Slow_sink_applies_backpressure()
    {
        const int N = 20_000, Cap = 16, Batch = 4;
        long produced = 0, consumed = 0, maxGap = 0;
        var g = new Graph(capacity: Cap, batch: Batch);
        var sink = g.Source(0, (ref int i, ref RingWriter<int> w) =>
            {
                while (i < N && w.HasRoom) { w.Write(i++); Interlocked.Increment(ref produced); }
                return i < N;
            })
            .Stage(0, static (ref int _, ReadOnlySpan<int> xs, ref RingWriter<int> w) => w.Write(xs))
            .Sink(0, (ref int n, ReadOnlySpan<int> xs) =>
            {
                long gap = Volatile.Read(ref produced) - consumed;
                if (gap > maxGap) maxGap = gap;
                Thread.SpinWait(2000);   // slow consumer
                n += xs.Length;
                consumed += xs.Length;
            });
        RunWithin(g);
        Assert.Equal(N, sink.State);
        output.WriteLine($"max items in flight: {maxGap}");
        // two rings, one stage overflow buffer (at most one input batch), the sink's batch
        Assert.True(maxGap <= 2 * Cap + 2 * Batch, $"{maxGap} items in flight");
    }

    sealed class Boom(string m) : Exception(m);

    [Theory]
    [InlineData("source")]
    [InlineData("stage")]
    [InlineData("replicate")]
    [InlineData("sink")]
    public void First_exception_is_rethrown_once_at_run(string where)
    {
        var g = new Graph(capacity: 64, batch: 16);
        var src = g.Source(0L, (ref long i, ref RingWriter<long> w) =>
        {
            if (where == "source" && i > 5000) throw new Boom("source");
            while (w.HasRoom) w.Write(i++);
            return true;   // infinite: only a failure ends this graph
        });
        var st = src.Stage(0, (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
        {
            foreach (var x in xs) { if (where == "stage" && x == 7000) throw new Boom("stage"); w.Write(x); }
        });
        var rep = st.Replicate(0, (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
        {
            foreach (var x in xs) { if (where == "replicate" && x >= 9000) throw new Boom("replicate"); w.Write(x); }
        }, workers: 4);
        rep.Sink(0, (ref int _, ReadOnlySpan<long> xs) =>
        {
            foreach (var x in xs) if (where == "sink" && x == 11_000) throw new Boom("sink");
        });
        var e = Assert.Throws<Boom>(() => RunWithin(g));
        Assert.Equal(where, e.Message);
        Assert.Contains(nameof(First_exception_is_rethrown_once_at_run), e.StackTrace);   // original frames kept
    }

    [Fact]
    public void Two_failing_stages_surface_one_exception()
    {
        var g = new Graph(capacity: 16, batch: 4);
        g.Source(Range(10_000))
            .Stage(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) => { w.Write(xs); if (xs[0] > 100) throw new Boom("first"); })
            .Sink(0, static (ref int _, ReadOnlySpan<long> xs) => { if (xs[0] > 50) throw new Boom("second"); });
        var e = Assert.Throws<Boom>(() => RunWithin(g));
        Assert.True(e.Message is "first" or "second");
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    [InlineData(64, 16)]
    public void Long_chain_of_tiny_rings_never_loses_a_wake(int capacity, int batch)
    {
        // every ring is nearly always full or empty, so stages idle and wake constantly
        const int N = 50_000, Stages = 12;
        for (int round = 0; round < 3; round++)
        {
            var g = new Graph(capacity, batch);
            var p = g.Source(Range(N));
            for (int s = 0; s < Stages; s++)
                p = p.Stage(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) => { foreach (var x in xs) w.Write(x + 1); });
            p = p.Replicate(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) => w.Write(xs), workers: 3, batch: batch);
            var sink = p.Sink((next: (long)Stages, ok: true), static (ref (long next, bool ok) s, ReadOnlySpan<long> xs) =>
            {
                foreach (var x in xs) { s.ok &= x == s.next; s.next++; }
            });
            RunWithin(g);
            Assert.True(sink.State.ok);
            Assert.Equal(N + Stages, sink.State.next);
        }
    }

    // ---- replication ----

    static void Square(ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w)
    {
        foreach (var x in xs)
        {
            if ((x & 255) == 0) Thread.SpinWait((int)(x * 7919 % 5000));   // uneven work: batches finish out of order
            w.Write(x * x);
            if (x % 3 == 0) w.Write(-x);   // 1:n
        }
    }

    static List<long> RunReplicated(int n, int workers, bool ordered, int batch)
    {
        var g = new Graph(capacity: 1024, batch: 64);
        var sink = g.Source(Range(n))
            .Replicate<int, long>(0, Square, workers: workers, ordered: ordered, batch: batch)
            .Sink(new List<long>(), static (ref List<long> acc, ReadOnlySpan<long> xs) => acc.AddRange(xs));
        RunWithin(g);
        return sink.State;
    }

    [Fact]
    public void Replicated_ordered_output_is_identical_for_every_worker_count()
    {
        const int N = 100_000;
        var serial = new List<long>();
        foreach (var x in Range(N)) { serial.Add(x * x); if (x % 3 == 0) serial.Add(-x); }
        foreach (int k in new[] { 1, 2, 3, 4, 8, 16, 32 })
            foreach (int b in new[] { 1, 7, 64, 500 })
                Assert.Equal(serial, RunReplicated(N, k, ordered: true, batch: b));
    }

    [Fact]
    public void Replicated_unordered_output_is_a_permutation()
    {
        const int N = 100_000;
        var expect = new List<long>();
        foreach (var x in Range(N)) { expect.Add(x * x); if (x % 3 == 0) expect.Add(-x); }
        expect.Sort();
        foreach (int k in new[] { 1, 4, 16 })
        {
            var got = RunReplicated(N, k, ordered: false, batch: 32);
            got.Sort();
            Assert.Equal(expect, got);
        }
    }

    [Fact]
    public void Replicated_stage_runs_batches_in_parallel()
    {
        if (Environment.ProcessorCount < 4) return;
        int running = 0, peak = 0;
        var g = new Graph(capacity: 64, batch: 8);
        g.Source(Range(400)).Replicate(0, (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
        {
            int now = Interlocked.Increment(ref running);
            int p;
            while ((p = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, p) != p) { }
            Thread.Sleep(2);
            Interlocked.Decrement(ref running);
            w.Write(xs);
        }, workers: 4).Sink(0, static (ref int _, ReadOnlySpan<long> _) => { });
        RunWithin(g);
        Assert.True(peak is > 1 and <= 4, $"peak {peak}");
    }

    // ---- allocation ----

    /// <summary>Per body thread: bytes allocated between two consecutive calls on the same thread.</summary>
    struct AllocProbe
    {
        public int thread, pairs, dirty;
        public long bytes;

        public void Step()
        {
            long b = GC.GetAllocatedBytesForCurrentThread();
            int t = Environment.CurrentManagedThreadId;
            if (t == thread) { pairs++; if (b != bytes) dirty++; }
            thread = t;
            bytes = GC.GetAllocatedBytesForCurrentThread();   // excludes this probe's own bookkeeping
        }
    }

    sealed class Box<T> { public T value = default!; }

    struct SrcState { public long i, n; public AllocProbe probe; }
    struct StageState { public long seen; public AllocProbe probe; }

    [Fact]
    public void Steady_state_allocates_nothing_per_item()
    {
        const long N = 4_000_000;
        for (int attempt = 0; ; attempt++)
        {
            var g = new Graph(capacity: 1024, batch: 128);
            var src = new Box<SrcState>();
            var mid = new Box<StageState>();
            var sink = g.Source(new SrcState { n = N }, (ref SrcState s, ref RingWriter<long> w) =>
                {
                    s.probe.Step();
                    int k = 0;
                    while (s.i < s.n && k++ < 512 && w.HasRoom) w.Write(s.i++);
                    if (s.i == s.n) src.value = s;
                    return s.i < s.n;
                })
                .Stage(new StageState(), (ref StageState s, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
                {
                    s.probe.Step();
                    foreach (var x in xs) { w.Write(x + 1); w.Write(x); }   // 1:2
                    s.seen += xs.Length;
                    if (s.seen == N) mid.value = s;
                })
                .Replicate(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
                {
                    foreach (var x in xs) w.Write(x * 2);
                }, workers: 4, batch: 256)
                .Sink(new StageState(), static (ref StageState s, ReadOnlySpan<long> xs) =>
                {
                    s.probe.Step();
                    s.seen += xs.Length;
                });
            RunWithin(g);
            Assert.Equal(2 * N, sink.State.seen);
            var probes = new[] { ("source", src.value.probe), ("stage", mid.value.probe), ("sink", sink.State.probe) };
            bool clean = true;
            foreach (var (name, p) in probes)
            {
                output.WriteLine($"{name}: {p.dirty}/{p.pairs} same-thread steps allocated");
                clean &= p.pairs > 0 && p.dirty <= p.pairs / 100;   // pool bookkeeping between turns is O(wakes), not per item
            }
            if (clean) return;
            Assert.True(attempt < 2, "steady-state dataflow allocated per batch");
        }
    }

    [Fact]
    public void Throughput_microbenchmark()
    {
        const long N = 20_000_000;
        var sw = Stopwatch.StartNew();
        var g = new Graph();
        var sink = g.Source(0L, static (ref long i, ref RingWriter<long> w) =>
            {
                long end = Math.Min(i + 4096, N);
                while (i < end) w.Write(i++);
                return i < N;
            })
            .Stage(0, static (ref int _, ReadOnlySpan<long> xs, ref RingWriter<long> w) => { foreach (var x in xs) w.Write(x ^ 1); })
            .Sink(0L, static (ref long s, ReadOnlySpan<long> xs) => { foreach (var x in xs) s += x; });
        RunWithin(g);
        sw.Stop();
        Assert.Equal((N - 1) * N / 2, sink.State);
        output.WriteLine($"3-stage long pipeline: {N / sw.Elapsed.TotalSeconds / 1e6:F1} M items/s");
    }
}
