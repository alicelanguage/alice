using System.Numerics;
using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>Numeric kernels: stats, norms, cache-blocked parallel SIMD matmul.</summary>
public static class AMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Clamp<T>(T x, T lo, T hi) where T : INumber<T> => T.Clamp(x, lo, hi);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Lerp<T>(T a, T b, T t) where T : IFloatingPoint<T> => a + (b - a) * t;

    public static T Mean<T>(ReadOnlySpan<T> x) where T : unmanaged, INumber<T>
        => x.IsEmpty ? throw new ArgumentException("Sequence is empty.") : Simd.Sum(x) / T.CreateChecked(x.Length);

    /// <summary>Population variance.</summary>
    public static T Variance<T>(ReadOnlySpan<T> x) where T : unmanaged, INumber<T>
    {
        T m = Mean(x), s = T.Zero;
        foreach (var v in x) { var d = v - m; s += d * d; }
        return s / T.CreateChecked(x.Length);
    }

    public static double Norm(ReadOnlySpan<double> x) => Math.Sqrt(Simd.Dot(x, x));

    /// <summary>C[n×m] = A[n×k]·B[k×m], row-major. i-k-j order so the inner loop is a SIMD axpy over contiguous rows.</summary>
    public static void MatMul<T>(T[] a, T[] b, T[] c, int n, int k, int m) where T : unmanaged, INumber<T>
    {
        if (a.Length != n * k || b.Length != k * m || c.Length != n * m)
            throw new ArgumentException("Matrix dimensions do not match buffers.");
        const int KB = 256;
        Action<int, int> rows = (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                var ci = c.AsSpan(i * m, m);
                ci.Clear();
                for (int p0 = 0; p0 < k; p0 += KB)
                {
                    int p1 = Math.Min(k, p0 + KB);
                    for (int p = p0; p < p1; p++) Axpy(a[i * k + p], b.AsSpan(p * m, m), ci);
                }
            }
        };
        if ((long)n * k * m < 1 << 18) rows(0, n); else Par.Range(n, rows);
    }

    /// <summary>y += alpha·x.</summary>
    public static void Axpy<T>(T alpha, ReadOnlySpan<T> x, Span<T> y) where T : unmanaged, INumber<T>
    {
        Simd.SameLength(x.Length, y.Length);
        int i = 0, w = Vector<T>.Count;
        if (Vector.IsHardwareAccelerated && x.Length >= w)
        {
            var va = new Vector<T>(alpha);
            for (; i <= x.Length - w; i += w)
                (new Vector<T>(y[i..]) + va * new Vector<T>(x[i..])).CopyTo(y[i..]);
        }
        for (; i < x.Length; i++) y[i] += alpha * x[i];
    }
}
