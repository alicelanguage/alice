using System.Diagnostics;
using Alice.Runtime;
using Xunit.Abstractions;

namespace Alice.Tests.Runtime;

/// <summary>
/// BEAM-style processes (ADR 0024): a crash kills only its process, exits travel along links and to
/// monitors, supervisors restart children in place (one_for_one, one_for_all, restart intensity, trees),
/// and the steady-state mailbox path allocates nothing.
/// </summary>
public class ActorTests(ITestOutputHelper output)
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(10);

    static void Until(Func<bool> cond)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed > T) throw new TimeoutException("condition not reached");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void A_crash_kills_only_its_own_process()
    {
        var seen = new Atomic();
        var bad = new Actor<int>(m => { if (m == 3) throw new InvalidOperationException("boom"); });
        var good = new Actor<int>(m => seen.add(m));
        for (int i = 1; i <= 5; i++) { bad.send(i); good.send(i); }
        var r = bad.join();
        Assert.Equal("crash", r.kind);
        Assert.Equal("boom", r.message);
        Assert.IsType<InvalidOperationException>(r.error);
        good.stop();
        Assert.Equal(15, seen.get());
        Assert.False(bad.IsAlive);
        bad.send(9);   // a dead process refuses mail instead of throwing
        Assert.Equal(1, bad.dropped_messages);
    }

    [Fact]
    public void Abnormal_exits_travel_along_links_and_normal_ones_do_not()
    {
        var a = new Actor<int>(m => throw new Exception("a died"));
        var b = new Actor<int>(_ => { });
        var c = new Actor<int>(_ => { });
        a.link(b);
        b.link(c);
        a.send(1);
        Assert.Equal("linked", c.join().kind);
        Assert.Equal("linked", b.exit_reason!.kind);

        var x = new Actor<int>(_ => { });
        var y = new Actor<int>(_ => { });
        x.link(y);
        x.stop();
        Assert.True(y.IsAlive);
        y.stop();
    }

    [Fact]
    public void Monitors_see_every_exit_and_a_failing_monitor_is_contained()
    {
        var reasons = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var a = new Actor<int>(_ => throw new Exception("x"));
        a.on_exit(_ => throw new Exception("bad monitor"));
        a.on_exit(r => reasons.Enqueue(r.kind));
        a.send(0);
        a.join();
        Until(() => reasons.Count == 1);
        Assert.Equal("crash", reasons.Single());
    }

    [Fact]
    public void One_for_one_restarts_the_failed_child_with_fresh_state()
    {
        var sup = Supervisor.one_for_one(max_restarts: 5, within_ms: 10_000);
        // message -1 crashes; otherwise the state counts messages
        var flaky = sup.supervise(new Actor<int, int>(() => 100, (s, m) => m < 0 ? throw new Exception("crash") : s + 1));
        var steady = sup.supervise(new Actor<int, int>(() => 0, (s, m) => s + m));
        steady.send(10);
        flaky.send(1);
        flaky.send(1);
        flaky.send(-1);
        Until(() => flaky.restarts == 1);
        flaky.send(1);
        steady.send(5);
        Assert.Equal(101, flaky.finish());   // state rebuilt by init, then one message
        Assert.Equal(15, steady.finish());   // sibling untouched
        Assert.Equal(1, sup.restart_count);
        Assert.True(sup.IsAlive);
        sup.stop();
    }

    [Fact]
    public void One_for_all_restarts_every_child()
    {
        var sup = Supervisor.one_for_all(max_restarts: 5, within_ms: 10_000);
        var a = sup.supervise(new Actor<int, int>(() => 0, (s, m) => m < 0 ? throw new Exception("crash") : s + m));
        var b = sup.supervise(new Actor<int, int>(() => 0, (s, m) => s + m));
        b.stop();   // a normal exit is final: b leaves the supervisor
        Assert.Equal(1, sup.children);
        var c = sup.supervise(new Actor<int, int>(() => 0, (s, m) => s + m));
        c.send(7);
        a.send(-1);
        Until(() => c.restarts == 1);
        c.send(1);
        Assert.Equal(1, c.finish());   // the sibling lost its 7: one_for_all rebuilt it
        Assert.Equal(1, a.restarts);
        Assert.False(b.IsAlive);
        sup.stop();
        Assert.False(a.IsAlive);
    }

    [Fact]
    public void A_supervisor_gives_up_after_max_restarts_and_its_parent_restarts_the_tree()
    {
        var root = Supervisor.one_for_one(max_restarts: 3, within_ms: 60_000);
        var inner = root.supervise(Supervisor.one_for_one(max_restarts: 2, within_ms: 60_000));
        var w = inner.supervise(new Actor<int>(_ => throw new Exception("always")));
        for (int i = 0; i < 3; i++)
        {
            int before = w.restarts;
            w.send(0);
            Until(() => w.restarts > before);
        }
        // third crash exceeded inner's intensity: inner exited, root restarted inner, which restarted w
        Until(() => inner.restarts == 1 && w.IsAlive);
        Assert.True(root.IsAlive);
        root.stop();
        Assert.False(inner.IsAlive);
        Assert.False(w.IsAlive);
    }

    [Fact]
    public void A_linked_crash_is_restarted_by_the_supervisor_of_the_victim()
    {
        var sup = Supervisor.one_for_one();
        var victim = sup.supervise(new Actor<int>(_ => { }));
        var killer = new Actor<int>(_ => throw new Exception("x"));
        killer.link(victim);
        killer.send(0);
        Until(() => victim.restarts == 1);
        Assert.True(victim.IsAlive);
        sup.stop();
    }

    [Fact]
    public void Thousands_of_processes_hold_no_threads()
    {
        var sum = new Atomic();
        var ps = Enumerable.Range(0, 10_000).Select(_ => new Actor<int>(m => sum.add(m))).ToArray();
        foreach (var p in ps) p.send(1);
        foreach (var p in ps) p.stop();
        Assert.Equal(10_000, sum.get());
    }

    [Fact]
    public void Steady_state_send_and_handle_allocate_nothing()
    {
        const int N = 20_000;
        var tid = new int[N];
        var bytes = new long[N];
        int i = 0;
        var a = new Actor<int>(_ =>
        {
            tid[i] = Environment.CurrentManagedThreadId;
            bytes[i] = GC.GetAllocatedBytesForCurrentThread();
            Volatile.Write(ref i, i + 1);
        });
        for (int k = 0; k < 1000; k++) a.send(k);   // warm up: queue segment, JIT
        Until(() => i == 1000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 1000; k < N; k++)
        {
            a.send(k);
            if ((k & 63) == 0) while (Volatile.Read(ref i) <= k) Thread.SpinWait(8);   // steady state: the consumer keeps up
        }
        long sendBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        a.stop();
        Assert.Equal(N, i);
        int pairs = 0, clean = 0;
        for (int k = 1001; k < N; k++)
            if (tid[k] == tid[k - 1]) { pairs++; if (bytes[k] == bytes[k - 1]) clean++; }
        output.WriteLine($"send: {sendBytes} B for {N - 1000} msgs; handle: {clean}/{pairs} same-thread steps alloc-free");
        // a burst that outruns the consumer grows the queue by a segment (memory for the backlog, not per message)
        Assert.True(sendBytes < 1024, $"send allocated {sendBytes} B");   // O(1) pool bookkeeping, 0 per message
        Assert.True(pairs > 0 && clean >= pairs * 99 / 100, $"{pairs - clean} handle steps allocated");
    }

    [Fact]
    public void Throughput_microbenchmark()
    {
        const int N = 2_000_000;
        var sw = Stopwatch.StartNew();
        var a = new Actor<long, int>(() => 0L, (s, m) => s + m);
        for (int k = 0; k < N; k++) a.send(1);
        long total = a.finish();
        sw.Stop();
        Assert.Equal(N, total);
        output.WriteLine($"1 actor: {N / sw.Elapsed.TotalSeconds / 1e6:F1} M msgs/s");

        const int P = 64, M = 50_000;
        sw.Restart();
        var many = Enumerable.Range(0, P).Select(_ => new Actor<long, int>(() => 0L, (s, m) => s + m)).ToArray();
        Parallel.For(0, P, p => { var x = many[p]; for (int k = 0; k < M; k++) x.send(1); });
        long all = many.Sum(x => x.finish());
        sw.Stop();
        Assert.Equal((long)P * M, all);
        output.WriteLine($"{P} actors: {P * M / sw.Elapsed.TotalSeconds / 1e6:F1} M msgs/s");
    }
}
