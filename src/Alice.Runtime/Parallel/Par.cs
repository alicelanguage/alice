using System.Numerics;

namespace Alice.Runtime;

/// <summary>Chunked data-parallel primitives. Chunking amortizes scheduling over many cheap iterations.</summary>
public static class Par
{
    public static int Workers => Environment.ProcessorCount;

    /// <summary>Calls body(lo, hi) over disjoint contiguous ranges covering [0, n).</summary>
    public static void Range(int n, Action<int, int> body)
    {
        if (n <= 0) return;
        int chunks = Math.Min(n, Workers * 4);
        if (chunks <= 1) { body(0, n); return; }
        Parallel.For(0, chunks, c => body((int)((long)n * c / chunks), (int)((long)n * (c + 1) / chunks)));
    }

    /// <summary>Chunked map-reduce over [0, n): `chunk(lo, hi)` runs in parallel, partials are combined in order.</summary>
    public static T Reduce<T>(int n, Func<int, int, T> chunk, Func<T, T, T> combine)
    {
        int chunks = Math.Min(Math.Max(1, n / 8192), Workers * 4);
        if (chunks <= 1) return chunk(0, n);
        var partial = new T[chunks];
        Parallel.For(0, chunks, c => partial[c] = chunk((int)((long)n * c / chunks), (int)((long)n * (c + 1) / chunks)));
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
        var partial = new R[chunks];
        Parallel.For(0, chunks, c =>
        {
            int lo = (int)((long)src.Length * c / chunks), hi = (int)((long)src.Length * (c + 1) / chunks);
            R s = R.Zero;
            for (int i = lo; i < hi; i++) s += f(src[i]);
            partial[c] = s;
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
