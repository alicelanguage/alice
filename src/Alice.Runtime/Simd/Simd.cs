using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>Hardware-width SIMD kernels (Vector&lt;T&gt; = SSE/AVX2/AVX-512/NEON as available) with scalar tails.</summary>
public static class Simd
{
    public static bool IsAccelerated => Vector.IsHardwareAccelerated;

    public static T Sum<T>(ReadOnlySpan<T> a) where T : unmanaged, INumber<T>
    {
        int i = 0, w = Vector<T>.Count;
        T s = T.Zero;
        if (Vector.IsHardwareAccelerated && a.Length >= w)
        {
            var acc = Vector<T>.Zero;
            var v = MemoryMarshal.Cast<T, Vector<T>>(a);
            foreach (var x in v) acc += x;
            s = Vector.Sum(acc);
            i = v.Length * w;
        }
        for (; i < a.Length; i++) s += a[i];
        return s;
    }

    public static T Dot<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, INumber<T>
    {
        SameLength(a.Length, b.Length);
        int i = 0, w = Vector<T>.Count;
        T s = T.Zero;
        if (Vector.IsHardwareAccelerated && a.Length >= w)
        {
            var va = MemoryMarshal.Cast<T, Vector<T>>(a);
            var vb = MemoryMarshal.Cast<T, Vector<T>>(b);
            var acc = Vector<T>.Zero;
            for (int k = 0; k < va.Length; k++) acc += va[k] * vb[k];
            s = Vector.Sum(acc);
            i = va.Length * w;
        }
        for (; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public static void Add<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> dst) where T : unmanaged, INumber<T>
        => Binary(a, b, dst, static (x, y) => x + y, static (x, y) => x + y);

    public static void Multiply<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> dst) where T : unmanaged, INumber<T>
        => Binary(a, b, dst, static (x, y) => x * y, static (x, y) => x * y);

    public static void Scale<T>(ReadOnlySpan<T> a, T k, Span<T> dst) where T : unmanaged, INumber<T>
    {
        SameLength(a.Length, dst.Length);
        int i = 0, w = Vector<T>.Count;
        if (Vector.IsHardwareAccelerated && a.Length >= w)
        {
            var va = MemoryMarshal.Cast<T, Vector<T>>(a);
            var vd = MemoryMarshal.Cast<T, Vector<T>>(dst);
            for (int j = 0; j < va.Length; j++) vd[j] = va[j] * k;
            i = va.Length * w;
        }
        for (; i < a.Length; i++) dst[i] = a[i] * k;
    }

    public static T Max<T>(ReadOnlySpan<T> a) where T : unmanaged, INumber<T> => Reduce(a, Vector.Max, T.Max);

    public static T Min<T>(ReadOnlySpan<T> a) where T : unmanaged, INumber<T> => Reduce(a, Vector.Min, T.Min);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Binary<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> dst,
        Func<Vector<T>, Vector<T>, Vector<T>> vop, Func<T, T, T> sop) where T : unmanaged, INumber<T>
    {
        SameLength(a.Length, b.Length);
        SameLength(a.Length, dst.Length);
        int i = 0, w = Vector<T>.Count;
        if (Vector.IsHardwareAccelerated && a.Length >= w)
        {
            var va = MemoryMarshal.Cast<T, Vector<T>>(a);
            var vb = MemoryMarshal.Cast<T, Vector<T>>(b);
            var vd = MemoryMarshal.Cast<T, Vector<T>>(dst);
            for (int j = 0; j < va.Length; j++) vd[j] = vop(va[j], vb[j]);
            i = va.Length * w;
        }
        for (; i < a.Length; i++) dst[i] = sop(a[i], b[i]);
    }

    static T Reduce<T>(ReadOnlySpan<T> a, Func<Vector<T>, Vector<T>, Vector<T>> vop, Func<T, T, T> sop)
        where T : unmanaged, INumber<T>
    {
        if (a.IsEmpty) throw new ArgumentException("Sequence is empty.", nameof(a));
        int i = 0, w = Vector<T>.Count;
        T r = a[0];
        if (Vector.IsHardwareAccelerated && a.Length >= w)
        {
            var v = MemoryMarshal.Cast<T, Vector<T>>(a);
            var acc = v[0];
            for (int j = 1; j < v.Length; j++) acc = vop(acc, v[j]);
            for (int j = 0; j < w; j++) r = sop(r, acc[j]);
            i = v.Length * w;
        }
        for (; i < a.Length; i++) r = sop(r, a[i]);
        return r;
    }

    internal static void SameLength(int a, int b)
    {
        if (a != b) throw new ArgumentException($"Length mismatch: {a} != {b}.");
    }
}
