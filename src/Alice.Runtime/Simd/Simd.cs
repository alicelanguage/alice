using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Alice.Runtime;

/// <summary>
/// SIMD kernels with explicit width tiers: Vector512 (AVX-512) when accelerated and the input holds a full vector,
/// else Vector256 (AVX2), else Vector128 (SSE/NEON/WASM SIMD), else scalar. `IsHardwareAccelerated` is a JIT/AOT
/// constant, so each call keeps only one tier. Sum/Dot use four accumulators (hides add latency) and a scalar tail;
/// Max/Min/elementwise ops finish with one overlapping final vector (idempotent), so there is no scalar tail.
/// Floating-point Sum/Dot reassociate (as all Alice vectorized float sums do, docs/performance.md).
/// </summary>
public static class Simd
{
    public static bool IsAccelerated => Vector128.IsHardwareAccelerated;

    public static T Sum<T>(ReadOnlySpan<T> a) where T : unmanaged, INumber<T>
    {
        ref T p = ref MemoryMarshal.GetReference(a);
        nuint n = (nuint)a.Length, i = 0;
        T s = T.Zero;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported && n >= (nuint)Vector512<T>.Count)
        {
            nuint w = (nuint)Vector512<T>.Count;
            Vector512<T> a0 = default, a1 = default, a2 = default, a3 = default;
            for (; i + 4 * w <= n; i += 4 * w)
            {
                a0 += Vector512.LoadUnsafe(ref p, i);
                a1 += Vector512.LoadUnsafe(ref p, i + w);
                a2 += Vector512.LoadUnsafe(ref p, i + 2 * w);
                a3 += Vector512.LoadUnsafe(ref p, i + 3 * w);
            }
            for (; i + w <= n; i += w) a0 += Vector512.LoadUnsafe(ref p, i);
            s = Vector512.Sum((a0 + a1) + (a2 + a3));
        }
        else if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported && n >= (nuint)Vector256<T>.Count)
        {
            nuint w = (nuint)Vector256<T>.Count;
            Vector256<T> a0 = default, a1 = default, a2 = default, a3 = default;
            for (; i + 4 * w <= n; i += 4 * w)
            {
                a0 += Vector256.LoadUnsafe(ref p, i);
                a1 += Vector256.LoadUnsafe(ref p, i + w);
                a2 += Vector256.LoadUnsafe(ref p, i + 2 * w);
                a3 += Vector256.LoadUnsafe(ref p, i + 3 * w);
            }
            for (; i + w <= n; i += w) a0 += Vector256.LoadUnsafe(ref p, i);
            s = Vector256.Sum((a0 + a1) + (a2 + a3));
        }
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && n >= (nuint)Vector128<T>.Count)
        {
            nuint w = (nuint)Vector128<T>.Count;
            Vector128<T> a0 = default, a1 = default, a2 = default, a3 = default;
            for (; i + 4 * w <= n; i += 4 * w)
            {
                a0 += Vector128.LoadUnsafe(ref p, i);
                a1 += Vector128.LoadUnsafe(ref p, i + w);
                a2 += Vector128.LoadUnsafe(ref p, i + 2 * w);
                a3 += Vector128.LoadUnsafe(ref p, i + 3 * w);
            }
            for (; i + w <= n; i += w) a0 += Vector128.LoadUnsafe(ref p, i);
            s = Vector128.Sum((a0 + a1) + (a2 + a3));
        }
        for (; i < n; i++) s += Unsafe.Add(ref p, i);
        return s;
    }

    public static T Dot<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, INumber<T>
    {
        SameLength(a.Length, b.Length);
        ref T pa = ref MemoryMarshal.GetReference(a);
        ref T pb = ref MemoryMarshal.GetReference(b);
        nuint n = (nuint)a.Length, i = 0;
        T s = T.Zero;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported && n >= (nuint)Vector512<T>.Count)
        {
            nuint w = (nuint)Vector512<T>.Count;
            Vector512<T> a0 = default, a1 = default;
            for (; i + 2 * w <= n; i += 2 * w)
            {
                a0 += Vector512.LoadUnsafe(ref pa, i) * Vector512.LoadUnsafe(ref pb, i);
                a1 += Vector512.LoadUnsafe(ref pa, i + w) * Vector512.LoadUnsafe(ref pb, i + w);
            }
            for (; i + w <= n; i += w) a0 += Vector512.LoadUnsafe(ref pa, i) * Vector512.LoadUnsafe(ref pb, i);
            s = Vector512.Sum(a0 + a1);
        }
        else if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported && n >= (nuint)Vector256<T>.Count)
        {
            nuint w = (nuint)Vector256<T>.Count;
            Vector256<T> a0 = default, a1 = default;
            for (; i + 2 * w <= n; i += 2 * w)
            {
                a0 += Vector256.LoadUnsafe(ref pa, i) * Vector256.LoadUnsafe(ref pb, i);
                a1 += Vector256.LoadUnsafe(ref pa, i + w) * Vector256.LoadUnsafe(ref pb, i + w);
            }
            for (; i + w <= n; i += w) a0 += Vector256.LoadUnsafe(ref pa, i) * Vector256.LoadUnsafe(ref pb, i);
            s = Vector256.Sum(a0 + a1);
        }
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && n >= (nuint)Vector128<T>.Count)
        {
            nuint w = (nuint)Vector128<T>.Count;
            Vector128<T> a0 = default, a1 = default;
            for (; i + 2 * w <= n; i += 2 * w)
            {
                a0 += Vector128.LoadUnsafe(ref pa, i) * Vector128.LoadUnsafe(ref pb, i);
                a1 += Vector128.LoadUnsafe(ref pa, i + w) * Vector128.LoadUnsafe(ref pb, i + w);
            }
            for (; i + w <= n; i += w) a0 += Vector128.LoadUnsafe(ref pa, i) * Vector128.LoadUnsafe(ref pb, i);
            s = Vector128.Sum(a0 + a1);
        }
        for (; i < n; i++) s += Unsafe.Add(ref pa, i) * Unsafe.Add(ref pb, i);
        return s;
    }

    public static void Add<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> dst) where T : unmanaged, INumber<T>
    {
        SameLength(a.Length, b.Length);
        SameLength(a.Length, dst.Length);
        if (!Separate(a, dst) || !Separate(b, dst)) { for (int i = 0; i < a.Length; i++) dst[i] = a[i] + b[i]; return; }
        ref T pa = ref MemoryMarshal.GetReference(a);
        ref T pb = ref MemoryMarshal.GetReference(b);
        ref T pd = ref MemoryMarshal.GetReference(dst);
        nuint n = (nuint)a.Length;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported && n >= (nuint)Vector512<T>.Count)
        {
            nuint w = (nuint)Vector512<T>.Count;
            var last = Vector512.LoadUnsafe(ref pa, n - w) + Vector512.LoadUnsafe(ref pb, n - w);   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector512.LoadUnsafe(ref pa, i) + Vector512.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported && n >= (nuint)Vector256<T>.Count)
        {
            nuint w = (nuint)Vector256<T>.Count;
            var last = Vector256.LoadUnsafe(ref pa, n - w) + Vector256.LoadUnsafe(ref pb, n - w);   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector256.LoadUnsafe(ref pa, i) + Vector256.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && n >= (nuint)Vector128<T>.Count)
        {
            nuint w = (nuint)Vector128<T>.Count;
            var last = Vector128.LoadUnsafe(ref pa, n - w) + Vector128.LoadUnsafe(ref pb, n - w);   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector128.LoadUnsafe(ref pa, i) + Vector128.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else for (nuint i = 0; i < n; i++) Unsafe.Add(ref pd, i) = Unsafe.Add(ref pa, i) + Unsafe.Add(ref pb, i);
    }

    public static void Multiply<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> dst) where T : unmanaged, INumber<T>
    {
        SameLength(a.Length, b.Length);
        SameLength(a.Length, dst.Length);
        if (!Separate(a, dst) || !Separate(b, dst)) { for (int i = 0; i < a.Length; i++) dst[i] = a[i] * b[i]; return; }
        ref T pa = ref MemoryMarshal.GetReference(a);
        ref T pb = ref MemoryMarshal.GetReference(b);
        ref T pd = ref MemoryMarshal.GetReference(dst);
        nuint n = (nuint)a.Length;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported && n >= (nuint)Vector512<T>.Count)
        {
            nuint w = (nuint)Vector512<T>.Count;
            var last = Vector512.LoadUnsafe(ref pa, n - w) * Vector512.LoadUnsafe(ref pb, n - w);   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector512.LoadUnsafe(ref pa, i) * Vector512.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported && n >= (nuint)Vector256<T>.Count)
        {
            nuint w = (nuint)Vector256<T>.Count;
            var last = Vector256.LoadUnsafe(ref pa, n - w) * Vector256.LoadUnsafe(ref pb, n - w);   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector256.LoadUnsafe(ref pa, i) * Vector256.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && n >= (nuint)Vector128<T>.Count)
        {
            nuint w = (nuint)Vector128<T>.Count;
            var last = Vector128.LoadUnsafe(ref pa, n - w) * Vector128.LoadUnsafe(ref pb, n - w);   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector128.LoadUnsafe(ref pa, i) * Vector128.LoadUnsafe(ref pb, i)).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else for (nuint i = 0; i < n; i++) Unsafe.Add(ref pd, i) = Unsafe.Add(ref pa, i) * Unsafe.Add(ref pb, i);
    }

    public static void Scale<T>(ReadOnlySpan<T> a, T k, Span<T> dst) where T : unmanaged, INumber<T>
    {
        SameLength(a.Length, dst.Length);
        if (!Separate(a, dst)) { for (int i = 0; i < a.Length; i++) dst[i] = a[i] * k; return; }
        ref T pa = ref MemoryMarshal.GetReference(a);
        ref T pd = ref MemoryMarshal.GetReference(dst);
        nuint n = (nuint)a.Length;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported && n >= (nuint)Vector512<T>.Count)
        {
            nuint w = (nuint)Vector512<T>.Count;
            var last = Vector512.LoadUnsafe(ref pa, n - w) * k;   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector512.LoadUnsafe(ref pa, i) * k).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported && n >= (nuint)Vector256<T>.Count)
        {
            nuint w = (nuint)Vector256<T>.Count;
            var last = Vector256.LoadUnsafe(ref pa, n - w) * k;   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector256.LoadUnsafe(ref pa, i) * k).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && n >= (nuint)Vector128<T>.Count)
        {
            nuint w = (nuint)Vector128<T>.Count;
            var last = Vector128.LoadUnsafe(ref pa, n - w) * k;   // final vector read before stores: exact in-place aliasing stays correct
            for (nuint i = 0; i + w <= n; i += w) (Vector128.LoadUnsafe(ref pa, i) * k).StoreUnsafe(ref pd, i);
            last.StoreUnsafe(ref pd, n - w);   // overlapping final vector
        }
        else for (nuint i = 0; i < n; i++) Unsafe.Add(ref pd, i) = Unsafe.Add(ref pa, i) * k;
    }

    public static T Max<T>(ReadOnlySpan<T> a) where T : unmanaged, INumber<T> => Extreme(a, max: true);

    public static T Min<T>(ReadOnlySpan<T> a) where T : unmanaged, INumber<T> => Extreme(a, max: false);

    /// <summary>Index of the first largest element (scalar `T.Max` order: NaN wins for floats); -1 when empty.</summary>
    public static int IndexOfMax<T>(ReadOnlySpan<T> a) where T : unmanaged, INumber<T>
    {
        if (a.IsEmpty) return -1;
        T m = Max(a);
        if (T.IsNaN(m))
        {
            for (int i = 0; i < a.Length; i++) if (T.IsNaN(a[i])) return i;
        }
        return a.IndexOf(m) is var k and >= 0 ? k : FirstEqual(a, m);
    }

    static int FirstEqual<T>(ReadOnlySpan<T> a, T m) where T : unmanaged, INumber<T>
    {
        for (int i = 0; i < a.Length; i++) if (a[i] == m) return i;   // +0/-0 compare equal; IndexOf is bitwise
        return 0;
    }

    /// <summary>Max/min with an overlapping final vector: re-reading lanes does not change an extreme.</summary>
    static T Extreme<T>(ReadOnlySpan<T> a, bool max) where T : unmanaged, INumber<T>
    {
        if (a.IsEmpty) throw new ArgumentException("Sequence is empty.", nameof(a));
        ref T p = ref MemoryMarshal.GetReference(a);
        nuint n = (nuint)a.Length;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported && n >= (nuint)Vector512<T>.Count)
        {
            nuint w = (nuint)Vector512<T>.Count;
            var acc = Vector512.LoadUnsafe(ref p);
            for (nuint i = w; i < n; i += w)
            {
                if (i > n - w) i = n - w;
                var v = Vector512.LoadUnsafe(ref p, i);
                acc = max ? Vector512.Max(acc, v) : Vector512.Min(acc, v);
            }
            var h = max ? Vector256.Max(acc.GetLower(), acc.GetUpper()) : Vector256.Min(acc.GetLower(), acc.GetUpper());
            return Lanes(h.GetLower(), h.GetUpper(), max);
        }
        if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported && n >= (nuint)Vector256<T>.Count)
        {
            nuint w = (nuint)Vector256<T>.Count;
            var acc = Vector256.LoadUnsafe(ref p);
            for (nuint i = w; i < n; i += w)
            {
                if (i > n - w) i = n - w;
                var v = Vector256.LoadUnsafe(ref p, i);
                acc = max ? Vector256.Max(acc, v) : Vector256.Min(acc, v);
            }
            return Lanes(acc.GetLower(), acc.GetUpper(), max);
        }
        if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && n >= (nuint)Vector128<T>.Count)
        {
            nuint w = (nuint)Vector128<T>.Count;
            var acc = Vector128.LoadUnsafe(ref p);
            for (nuint i = w; i < n; i += w)
            {
                if (i > n - w) i = n - w;
                var v = Vector128.LoadUnsafe(ref p, i);
                acc = max ? Vector128.Max(acc, v) : Vector128.Min(acc, v);
            }
            return Lanes(acc, acc, max);
        }
        T r = a[0];
        for (int i = 1; i < a.Length; i++) r = max ? T.Max(r, a[i]) : T.Min(r, a[i]);
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static T Lanes<T>(Vector128<T> x, Vector128<T> y, bool max) where T : unmanaged, INumber<T>
    {
        var v = max ? Vector128.Max(x, y) : Vector128.Min(x, y);
        T r = v.GetElement(0);
        for (int j = 1; j < Vector128<T>.Count; j++) r = max ? T.Max(r, v.GetElement(j)) : T.Min(r, v.GetElement(j));
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Separate<T>(ReadOnlySpan<T> src, Span<T> dst) => !src.Overlaps(dst) || Unsafe.AreSame(ref MemoryMarshal.GetReference(src), ref MemoryMarshal.GetReference(dst));

    internal static void SameLength(int a, int b)
    {
        if (a != b) throw new ArgumentException($"Length mismatch: {a} != {b}.");
    }
}
