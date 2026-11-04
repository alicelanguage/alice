using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>
/// Struct-kernel overloads of Par.Range / Par.Reduce / Par.Fork (Parallel/ParKernels.cs, ADR 0033 closure ABI):
/// the same coverage, ordering and exception behaviour as the delegate forms, and no allocation in steady state.
/// </summary>
[Collection(IsolatedAllocationCollection.Name)]
public class ParKernelTests
{
    struct Hits : IParKernel
    {
        public int[] Counts;
        public void Invoke(int lo, int hi)
        {
            for (int i = lo; i < hi; i++) Interlocked.Increment(ref Counts[i]);
        }
    }

    /// <summary>~50 ns per element: a chunk is long enough that a cold pool still dispatches helpers.</summary>
    struct Heavy : IParKernel<long>
    {
        public int[] Xs;
        public long Invoke(int lo, int hi)
        {
            long s = 0;
            for (int i = lo; i < hi; i++)
            {
                long x = Xs[i];
                for (int k = 0; k < 48; k++) x = x * 31 + k;
                s += x;
            }
            return s;
        }
    }

    struct Sum : IParKernel<long>
    {
        public int[] Xs;
        public long Invoke(int lo, int hi)
        {
            long s = 0;
            for (int i = lo; i < hi; i++) s += Xs[i];
            return s;
        }
    }

    struct Starts : IParKernel<string>
    {
        public string Invoke(int lo, int hi) => lo + ",";
    }

    struct Throws : IParKernel
    {
        public void Invoke(int lo, int hi) { if (lo > 10) throw new InvalidOperationException(); }
    }

    struct Nop : IParKernel
    {
        public void Invoke(int lo, int hi) { }
    }

    struct Fib : IForkBranch<long>
    {
        public int N;
        public long Invoke() => Run(N);

        public static long Run(int n)
        {
            if (n < 2) return n;
            if (n < 18 || !Par.Forkable) return Run(n - 1) + Run(n - 2);
            var a = new Fib { N = n - 1 };
            var b = new Fib { N = n - 2 };
            var (x, y) = Par.Fork<long, long, Fib, Fib>(ref a, ref b);
            return x + y;
        }
    }

    struct Const : IForkBranch<int>
    {
        public int V;
        public int Invoke() => V < 0 ? throw new ArgumentException("branch") : V;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 1)]
    [InlineData(1_000_003, 8192)]
    public void Range_covers_every_index_once(int n, int grain)
    {
        var k = new Hits { Counts = new int[n] };
        Par.Range(n, ref k, grain);
        Assert.All(k.Counts, h => Assert.Equal(1, h));
    }

    [Fact]
    public void Reduce_matches_the_serial_sum_and_combines_in_index_order()
    {
        var xs = Enumerable.Range(0, 1_000_000).Select(i => i % 7).ToArray();
        var k = new Sum { Xs = xs };
        Assert.Equal(xs.Sum(x => (long)x), Par.Reduce<long, Sum>(xs.Length, ref k, static (a, b) => a + b, 1000));
        var s = new Starts();
        var starts = Par.Reduce<string, Starts>(100_000, ref s, static (a, b) => a + b, 1000).TrimEnd(',').Split(',').Select(int.Parse).ToList();
        Assert.Equal(starts.OrderBy(x => x), starts);
        Assert.Equal(0, starts[0]);
        Assert.Equal(0L, Par.Reduce<long, Sum>(0, ref k, static (a, b) => a + b));
    }

    [Fact]
    public void First_exception_propagates_unwrapped()
    {
        var k = new Throws();
        Assert.Throws<InvalidOperationException>(() => Par.Range(100, ref k, 1));
        var a = new Const { V = 1 };
        var bad = new Const { V = -1 };
        Assert.Throws<ArgumentException>(() => Par.Fork<int, int, Const, Const>(ref bad, ref a));
        Assert.Throws<ArgumentException>(() => Par.Fork<int, int, Const, Const>(ref a, ref bad));
        Assert.Throws<ArgumentException>(() => Par.Fork<int, int, int, Const, Const, Const>(ref a, ref bad, ref a));
    }

    [Fact]
    public void Forks_return_both_results_and_nest()
    {
        var a = new Const { V = 1 };
        var b = new Const { V = 2 };
        var c = new Const { V = 3 };
        Assert.Equal((1, 2), Par.Fork<int, int, Const, Const>(ref a, ref b));
        Assert.Equal((1, 2, 3), Par.Fork<int, int, int, Const, Const, Const>(ref a, ref b, ref c));
        Assert.Equal(832040L, Fib.Run(30));
    }

    [Fact]
    public void Jobs_are_reused_in_steady_state()
    {
        var nop = new Nop();
        var xs = new int[1 << 20];
        var sum = new Sum { Xs = xs };
        Func<long, long, long> add = static (a, b) => a + b;
        for (int i = 0; i < 20; i++)   // warm the pool, the JIT, this thread's job slots, and thread-pool bookkeeping
        {
            Par.Range(1 << 20, ref nop, 8192);
            Par.Reduce(xs.Length, ref sum, add, 8192);
            Fib.Run(24);
        }
        // Force GC to settle platform-specific lazy initialization
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(0, AllocationProbe.Least(() => Par.Range(1000, ref nop, 1)));
        var f10 = new Fib { N = 10 };
        var f9 = new Fib { N = 9 };
        Assert.Equal(0, AllocationProbe.Least(() => Par.Fork<long, long, Fib, Fib>(ref f10, ref f9)));
        // parallel paths: the job is reused once its helpers returned; allow the pool's queue to grow once in a while
        long range = AllocationProbe.Least(() => Par.Range(1 << 20, ref nop, 8192));
        long reduce = AllocationProbe.Least(() => Par.Reduce(xs.Length, ref sum, add, 8192));
        Assert.True(range == 0, $"Par.Range with a struct kernel allocated {range} B");
        Assert.True(reduce == 0, $"Par.Reduce with a struct kernel allocated {reduce} B");
    }

    [Fact]
    public void Jobs_are_reused_while_their_helpers_are_still_queued()
    {
        // 1.0.2 (macOS CI, 3 cores: "Par.Reduce with a struct kernel allocated 288 B" = a fresh ReduceKernelJob + 24
        // partials): when the pool wakes late, the caller runs every chunk itself and returns while the helpers it queued
        // still wait. The next call must reuse the job anyway. Here every pool thread is held, so no helper can start.
        var xs = new int[1 << 20];
        var sum = new Heavy { Xs = xs };
        var nop = new Nop();
        Func<long, long, long> add = static (a, b) => a + b;
        for (int i = 0; i < 5; i++) { Par.Reduce(xs.Length, ref sum, add, 8192); Par.Range(1 << 20, ref nop, 8192); }
        var gate = new ManualResetEventSlim(false);   // never disposed: blockers still queued at the end start after Set and return
        // far more blockers than pool threads: the helpers queue behind them, and a thread the pool injects meanwhile
        // takes a blocker first
        const int held = 512;
        int started = 0;
        for (int i = 0; i < held; i++)
            ThreadPool.UnsafeQueueUserWorkItem<object?>(_ => { Interlocked.Increment(ref started); gate.Wait(); }, null, preferLocal: false);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref started) < Environment.ProcessorCount && DateTime.UtcNow < until) Thread.Sleep(1);
            long expected = sum.Invoke(0, xs.Length);
            Par.Reduce(xs.Length, ref sum, add, 8192);   // queues helpers that cannot run yet
            Par.Range(1 << 20, ref nop, 8192);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long got = Par.Reduce(xs.Length, ref sum, add, 8192);
            Par.Range(1 << 20, ref nop, 8192);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected, got);
            Assert.True(allocated == 0, $"Par.Reduce/Range with helpers still queued allocated {allocated} B");
        }
        finally { gate.Set(); }
        // the late helpers find their old run closed, or join the current one: results stay exact afterwards
        for (int i = 0; i < 20; i++) Assert.Equal(sum.Invoke(0, xs.Length), Par.Reduce(xs.Length, ref sum, add, 8192));
    }
}
