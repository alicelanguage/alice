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

    public static void For(int n, Action<int> body) => Range(n, (lo, hi) => { for (int i = lo; i < hi; i++) body(i); });

    public static TOut[] Map<TIn, TOut>(TIn[] src, Func<TIn, TOut> f)
    {
        var dst = new TOut[src.Length];
        Range(src.Length, (lo, hi) => { for (int i = lo; i < hi; i++) dst[i] = f(src[i]); });
        return dst;
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
