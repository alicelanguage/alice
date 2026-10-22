using System.Numerics;

namespace Alice.Runtime;

/// <summary>Chunked data-parallel primitives. Chunking amortizes scheduling over many cheap iterations.</summary>
public static class Par
{
    public static int Workers => Environment.ProcessorCount;

    // ---- fork/join of independent pure calls (`a = f(x)` / `b = f(y)`), lazy task creation like BendRT's parallel let ----

    /// <summary>Whether a parallel loop already dispatched: pool threads exist and waking them is cheap.</summary>
    internal static bool Warm;

    [ThreadStatic] static int forkDepth;

    internal static int ForkDepth { get => forkDepth; set => forkDepth = value; }

    /// <summary>Forks spawn tasks only near the root: 2^depth tasks cover the cores, deeper forks run as plain calls.</summary>
    static readonly int MaxForkDepth = System.Numerics.BitOperations.Log2((uint)Math.Max(1, Environment.ProcessorCount)) + 3;

    public static bool Forkable => forkDepth < MaxForkDepth && Environment.ProcessorCount > 1;

    /// <summary>Offers <paramref name="left"/> to the pool and runs <paramref name="right"/> on this thread, then joins.
    /// A left half no pool thread has started yet is run inline by the join, so nested forks never block the pool.
    /// Exceptions surface as themselves, the left one first.</summary>
    public static (A, B) Fork<A, B>(Func<A> left, Func<B> right)
    {
        int depth = forkDepth + 1;
        var job = new ForkJob<A>(left, depth);
        ThreadPool.UnsafeQueueUserWorkItem(job, preferLocal: true);
        int outer = forkDepth;
        forkDepth = depth;
        B b;
        try { b = right(); }
        catch
        {
            forkDepth = outer;
            job.Join();   // the left half may still be running on our data: finish it before unwinding
            throw;
        }
        forkDepth = outer;
        return (job.Join(), b);
    }

    /// <summary>
    /// Whether the rest of a loop is worth running in parallel: the first iteration took since <paramref name="t0"/>
    /// (Stopwatch ticks), and <paramref name="remaining"/> iterations of about that cost are left. Generated loops call
    /// it once, at the top of their second iteration.
    /// </summary>
    public static bool Offload(long t0, int remaining) => ParJob<byte>.WorthDispatch((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * (long)remaining);

    /// <summary>Calls body(lo, hi) over disjoint contiguous ranges covering [0, n).</summary>
    public static void Range(int n, Action<int, int> body, int grain = 1)
    {
        if (n <= 0) return;
        int cap = grain <= 1 ? Workers * 64 : Workers * 8;
        int chunks = Math.Min(Math.Max(1, n / Math.Max(1, grain)), cap);
        if (chunks <= 1) { body(0, n); return; }
        ParJob<byte>.Run(n, chunks, body);
    }

    /// <summary>Chunked map-reduce over [0, n): `chunk(lo, hi)` runs in parallel, partials are combined in order.</summary>
    public static T Reduce<T>(int n, Func<int, int, T> chunk, Func<T, T, T> combine, int grain = 8192)
    {
        // grain 1 = expensive, uneven iterations: many small chunks so the scheduler balances the load
        int cap = grain <= 1 ? Workers * 64 : Workers * 8;
        int chunks = Math.Min(Math.Max(1, n / Math.Max(1, grain)), cap);
        if (chunks <= 1) return chunk(0, n);
        var partial = ParJob<T>.Run(n, chunks, chunk);
        T acc = partial[0];
        for (int i = 1; i < chunks; i++) acc = combine(acc, partial[i]);
        return acc;
    }

    public static void For(int n, Action<int> body) => Range(n, (lo, hi) => { for (int i = lo; i < hi; i++) body(i); });

    public static TOut[] Map<TIn, TOut>(TIn[] src, Func<TIn, TOut> f)
    {
        var dst = new TOut[src.Length];
        Range(src.Length, (lo, hi) => { for (int i = lo; i < hi; i++) dst[i] = f(src[i]); });
        return dst;
    }

    /// <summary>Parallel map-reduce without materializing the mapped array: per-chunk partial sums.</summary>
    public static R MapSum<T, R>(T[] src, Func<T, R> f) where R : INumber<R>
    {
        int chunks = Math.Min(Math.Max(1, src.Length / 4096), Workers * 4);
        if (chunks <= 1)
        {
            R s = R.Zero;
            foreach (var x in src) s += f(x);
            return s;
        }
        var partial = ParJob<R>.Run(src.Length, chunks, (lo, hi) =>
        {
            R s = R.Zero;
            for (int i = lo; i < hi; i++) s += f(src[i]);
            return s;
        });
        R total = R.Zero;
        foreach (var p in partial) total += p;
        return total;
    }

    public static T Sum<T>(T[] src) where T : unmanaged, INumber<T>
    {
        if (src.Length < 65_536) return Simd.Sum<T>(src);
        var lockObj = new object(); T total = T.Zero;
        Range(src.Length, (lo, hi) =>
        {
            var s = Simd.Sum<T>(src.AsSpan(lo, hi - lo));
            lock (lockObj) total += s;
        });
        return total;
    }
}
