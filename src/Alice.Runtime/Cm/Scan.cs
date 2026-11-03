using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Alice.Runtime;

/// <summary>
/// Work-efficient parallel prefix scan (Blelloch's chunked form, reduce-then-scan): pass 1 reduces each chunk,
/// a serial scan of at most 1024 chunk totals gives every chunk its carry-in, pass 2 rescans each chunk from its
/// carry. Memory traffic is 2 reads + 1 write per element. The chunk-local scan is a SIMD lane-shift scan
/// (log2(lanes) shift+op steps per vector, then a broadcast carry): AVX-512 valignd/valignq, AVX2
/// vpermd/vpermq, Vector128 shuffles, scalar for other element sizes.
/// </summary>
public static unsafe partial class Cm
{
    /// <summary><c>dst[i] = src[0] op ... op src[i]</c>. <paramref name="dst"/> may be <paramref name="src"/>.</summary>
    public static void InclusiveScan<T>(ReadOnlySpan<T> src, Span<T> dst, CmOp op = CmOp.Add) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
        => Scan(src, dst, op, false);

    /// <summary><c>dst[0] = identity, dst[i] = src[0] op ... op src[i-1]</c> (identity: 0, 1, all bits, MaxValue or
    /// +Inf for min, MinValue or -Inf for max). <paramref name="dst"/> may be <paramref name="src"/>.</summary>
    public static void ExclusiveScan<T>(ReadOnlySpan<T> src, Span<T> dst, CmOp op = CmOp.Add) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
        => Scan(src, dst, op, true);

    /// <summary>Reduction with the same chunking as the scans (so it equals the last inclusive-scan carry-in plus
    /// the last chunk's total, and is bit-identical for any worker count). Empty input gives the identity.</summary>
    public static T Reduce<T>(ReadOnlySpan<T> src, CmOp op = CmOp.Add) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        CheckOp<T>(op);
        return op switch
        {
            CmOp.Add => ReduceAll<T, AddOp<T>>(src),
            CmOp.Mul => ReduceAll<T, MulOp<T>>(src),
            CmOp.And => ReduceAll<T, AndOp<T>>(src),
            CmOp.Or => ReduceAll<T, OrOp<T>>(src),
            CmOp.Xor => ReduceAll<T, XorOp<T>>(src),
            CmOp.Min => ReduceAll<T, MinOp<T>>(src),
            _ => ReduceAll<T, MaxOp<T>>(src),
        };
    }

    static void Scan<T>(ReadOnlySpan<T> src, Span<T> dst, CmOp op, bool exclusive) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        CheckOp<T>(op);
        Check(dst.Length >= src.Length, "scan destination is shorter than the source");
        switch (op)
        {
            case CmOp.Add: ScanAll<T, AddOp<T>>(src, dst, exclusive); break;
            case CmOp.Mul: ScanAll<T, MulOp<T>>(src, dst, exclusive); break;
            case CmOp.And: ScanAll<T, AndOp<T>>(src, dst, exclusive); break;
            case CmOp.Or: ScanAll<T, OrOp<T>>(src, dst, exclusive); break;
            case CmOp.Xor: ScanAll<T, XorOp<T>>(src, dst, exclusive); break;
            case CmOp.Min: ScanAll<T, MinOp<T>>(src, dst, exclusive); break;
            default: ScanAll<T, MaxOp<T>>(src, dst, exclusive); break;
        }
    }

    struct ScanCtx<T> where T : unmanaged
    {
        public T* Src, Dst, Totals;
        public int N, Chunk, Exclusive;
    }

    static void ScanAll<T, TOp>(ReadOnlySpan<T> src, Span<T> dst, bool exclusive) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        int n = src.Length;
        if (n == 0) return;
        int chunk = ChunkSize(n), chunks = (n + chunk - 1) / chunk;
        T* totals = stackalloc T[chunks];
        fixed (T* s = src, d = dst)
        {
            var ctx = new ScanCtx<T> { Src = s, Dst = d, Totals = totals, N = n, Chunk = chunk, Exclusive = exclusive ? 1 : 0 };
            if (chunks == 1) totals[0] = TOp.Identity;
            else
            {
                KernelPool.Run(chunks, ChunkGrain(chunk), &ReduceChunks<T, TOp>, &ctx);
                ExclusiveTotals<T, TOp>(totals, chunks);
            }
            KernelPool.Run(chunks, ChunkGrain(chunk), &ScanChunks<T, TOp>, &ctx);
        }
    }

    static T ReduceAll<T, TOp>(ReadOnlySpan<T> src) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        int n = src.Length;
        if (n == 0) return TOp.Identity;
        int chunk = ChunkSize(n), chunks = (n + chunk - 1) / chunk;
        T* totals = stackalloc T[chunks];
        fixed (T* s = src)
        {
            var ctx = new ScanCtx<T> { Src = s, Totals = totals, N = n, Chunk = chunk };
            KernelPool.Run(chunks, ChunkGrain(chunk), &ReduceChunks<T, TOp>, &ctx);
            return ExclusiveTotals<T, TOp>(totals, chunks);
        }
    }

    static void ReduceChunks<T, TOp>(void* p, int lo, int hi) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        var c = (ScanCtx<T>*)p;
        for (int k = lo; k < hi; k++)
        {
            int a = k * c->Chunk, len = Math.Min(c->Chunk, c->N - a);
            c->Totals[k] = LocalReduce<T, TOp>(c->Src + a, len);
        }
    }

    static void ScanChunks<T, TOp>(void* p, int lo, int hi) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        var c = (ScanCtx<T>*)p;
        for (int k = lo; k < hi; k++)
        {
            int a = k * c->Chunk, len = Math.Min(c->Chunk, c->N - a);
            if (c->Exclusive != 0) LocalScan<T, TOp, Exclusive>(c->Src + a, c->Dst + a, len, c->Totals[k]);
            else LocalScan<T, TOp, Inclusive>(c->Src + a, c->Dst + a, len, c->Totals[k]);
        }
    }

    // ---------------- chunk-local kernels ----------------

    internal interface IMode { static abstract bool IsExclusive { get; } }
    internal struct Inclusive : IMode { public static bool IsExclusive => false; }
    internal struct Exclusive : IMode { public static bool IsExclusive => true; }

    static bool Avx512Tier<T>() where T : unmanaged =>
        Avx512F.IsSupported && Vector512.IsHardwareAccelerated && (sizeof(T) == 4 || sizeof(T) == 8);

    static bool Avx2Tier<T>() where T : unmanaged =>
        Avx2.IsSupported && Vector256.IsHardwareAccelerated && (sizeof(T) == 4 || sizeof(T) == 8);

    static bool V128Tier<T>() where T : unmanaged =>
        Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && (sizeof(T) == 4 || sizeof(T) == 8);

    /// <summary>Scans one chunk from <paramref name="carry"/>: out = carry op prefix (inclusive) or the prefix before
    /// each element (exclusive). In place is fine: each vector is loaded before it is stored.</summary>
    internal static void LocalScan<T, TOp, TMode>(T* s, T* d, int n, T carry)
        where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T> where TMode : struct, IMode
    {
        int i = 0;
        if (Avx512Tier<T>() && n >= Vector512<T>.Count)
        {
            int w = Vector512<T>.Count;
            var id = Vector512.Create(TOp.Identity);
            var c = Vector512.Create(carry);
            for (; i + w <= n; i += w)
            {
                var r = TOp.Apply(c, Scan512<T, TOp>(Vector512.Load(s + i), id));
                if (TMode.IsExclusive) Shift512(r, c, 1).Store(d + i);
                else r.Store(d + i);
                c = Last512(r);
            }
            carry = c.ToScalar();
        }
        else if (Avx2Tier<T>() && n >= Vector256<T>.Count)
        {
            int w = Vector256<T>.Count;
            var id = Vector256.Create(TOp.Identity);
            var c = Vector256.Create(carry);
            for (; i + w <= n; i += w)
            {
                var r = TOp.Apply(c, Scan256<T, TOp>(Vector256.Load(s + i), id));
                if (TMode.IsExclusive) Shift256(r, c, 1).Store(d + i);
                else r.Store(d + i);
                c = Last256(r);
            }
            carry = c.ToScalar();
        }
        else if (V128Tier<T>() && n >= Vector128<T>.Count)
        {
            int w = Vector128<T>.Count;
            var id = Vector128.Create(TOp.Identity);
            var c = Vector128.Create(carry);
            for (; i + w <= n; i += w)
            {
                var r = TOp.Apply(c, Scan128<T, TOp>(Vector128.Load(s + i), id));
                if (TMode.IsExclusive) Shift128(r, c, 1).Store(d + i);
                else r.Store(d + i);
                c = Last128(r);
            }
            carry = c.ToScalar();
        }
        for (; i < n; i++)
        {
            T x = s[i];
            T r = TOp.Apply(carry, x);
            d[i] = TMode.IsExclusive ? carry : r;
            carry = r;
        }
    }

    /// <summary>Chunk total with 4 vector accumulators, folded lane by lane in a fixed order.</summary>
    internal static T LocalReduce<T, TOp>(T* s, int n) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        int i = 0;
        T acc = TOp.Identity;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported && n >= 4 * Vector512<T>.Count)
        {
            int w = Vector512<T>.Count;
            Vector512<T> a0 = Vector512.Load(s), a1 = Vector512.Load(s + w), a2 = Vector512.Load(s + 2 * w), a3 = Vector512.Load(s + 3 * w);
            for (i = 4 * w; i + 4 * w <= n; i += 4 * w)
            {
                a0 = TOp.Apply(a0, Vector512.Load(s + i));
                a1 = TOp.Apply(a1, Vector512.Load(s + i + w));
                a2 = TOp.Apply(a2, Vector512.Load(s + i + 2 * w));
                a3 = TOp.Apply(a3, Vector512.Load(s + i + 3 * w));
            }
            acc = Fold<T, TOp>(TOp.Apply(TOp.Apply(a0, a1), TOp.Apply(a2, a3)));
        }
        else if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported && n >= 4 * Vector256<T>.Count)
        {
            int w = Vector256<T>.Count;
            Vector256<T> a0 = Vector256.Load(s), a1 = Vector256.Load(s + w), a2 = Vector256.Load(s + 2 * w), a3 = Vector256.Load(s + 3 * w);
            for (i = 4 * w; i + 4 * w <= n; i += 4 * w)
            {
                a0 = TOp.Apply(a0, Vector256.Load(s + i));
                a1 = TOp.Apply(a1, Vector256.Load(s + i + w));
                a2 = TOp.Apply(a2, Vector256.Load(s + i + 2 * w));
                a3 = TOp.Apply(a3, Vector256.Load(s + i + 3 * w));
            }
            acc = Fold<T, TOp>(TOp.Apply(TOp.Apply(a0, a1), TOp.Apply(a2, a3)));
        }
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && n >= 4 * Vector128<T>.Count)
        {
            int w = Vector128<T>.Count;
            Vector128<T> a0 = Vector128.Load(s), a1 = Vector128.Load(s + w), a2 = Vector128.Load(s + 2 * w), a3 = Vector128.Load(s + 3 * w);
            for (i = 4 * w; i + 4 * w <= n; i += 4 * w)
            {
                a0 = TOp.Apply(a0, Vector128.Load(s + i));
                a1 = TOp.Apply(a1, Vector128.Load(s + i + w));
                a2 = TOp.Apply(a2, Vector128.Load(s + i + 2 * w));
                a3 = TOp.Apply(a3, Vector128.Load(s + i + 3 * w));
            }
            acc = Fold<T, TOp>(TOp.Apply(TOp.Apply(a0, a1), TOp.Apply(a2, a3)));
        }
        for (; i < n; i++) acc = TOp.Apply(acc, s[i]);
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static T Fold<T, TOp>(Vector512<T> v) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
        => Fold<T, TOp>(TOp.Apply(v.GetLower(), v.GetUpper()));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static T Fold<T, TOp>(Vector256<T> v) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
        => Fold<T, TOp>(TOp.Apply(v.GetLower(), v.GetUpper()));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static T Fold<T, TOp>(Vector128<T> v) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        T a = v.GetElement(0);
        for (int j = 1; j < Vector128<T>.Count; j++) a = TOp.Apply(a, v.GetElement(j));
        return a;
    }

    // ---------------- lane shifts: lane j of Shift(x, fill, k) is x[j - k], or fill for j < k ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector512<T> Scan512<T, TOp>(Vector512<T> x, Vector512<T> id) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        x = TOp.Apply(x, Shift512(x, id, 1));
        x = TOp.Apply(x, Shift512(x, id, 2));
        x = TOp.Apply(x, Shift512(x, id, 4));
        if (sizeof(T) == 4) x = TOp.Apply(x, Shift512(x, id, 8));
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector512<T> Shift512<T>(Vector512<T> x, Vector512<T> fill, int k) where T : unmanaged
    {
        if (sizeof(T) == 4)
        {
            Vector512<uint> a = x.AsUInt32(), f = fill.AsUInt32();
            return (k switch
            {
                1 => Avx512F.AlignRight32(a, f, 15),
                2 => Avx512F.AlignRight32(a, f, 14),
                4 => Avx512F.AlignRight32(a, f, 12),
                _ => Avx512F.AlignRight32(a, f, 8),
            }).As<uint, T>();
        }
        else
        {
            Vector512<ulong> a = x.AsUInt64(), f = fill.AsUInt64();
            return (k switch
            {
                1 => Avx512F.AlignRight64(a, f, 7),
                2 => Avx512F.AlignRight64(a, f, 6),
                _ => Avx512F.AlignRight64(a, f, 4),
            }).As<ulong, T>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector512<T> Last512<T>(Vector512<T> x) where T : unmanaged =>
        sizeof(T) == 4
            ? Avx512F.PermuteVar16x32(x.AsUInt32(), Vector512.Create(15u)).As<uint, T>()
            : Avx512F.PermuteVar8x64(x.AsUInt64(), Vector512.Create(7ul)).As<ulong, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<T> Scan256<T, TOp>(Vector256<T> x, Vector256<T> id) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        x = TOp.Apply(x, Shift256(x, id, 1));
        x = TOp.Apply(x, Shift256(x, id, 2));
        if (sizeof(T) == 4) x = TOp.Apply(x, Shift256(x, id, 4));
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<T> Shift256<T>(Vector256<T> x, Vector256<T> fill, int k) where T : unmanaged
    {
        if (sizeof(T) == 4)
        {
            Vector256<uint> a = x.AsUInt32();
            Vector256<uint> m, s;
            switch (k)
            {
                case 1: s = Avx2.PermuteVar8x32(a, Vector256.Create(0u, 0, 1, 2, 3, 4, 5, 6)); m = Vector256.Create(~0u, 0, 0, 0, 0, 0, 0, 0); break;
                case 2: s = Avx2.PermuteVar8x32(a, Vector256.Create(0u, 0, 0, 1, 2, 3, 4, 5)); m = Vector256.Create(~0u, ~0u, 0, 0, 0, 0, 0, 0); break;
                default: s = Avx2.PermuteVar8x32(a, Vector256.Create(0u, 0, 0, 0, 0, 1, 2, 3)); m = Vector256.Create(~0u, ~0u, ~0u, ~0u, 0, 0, 0, 0); break;
            }
            return Vector256.ConditionalSelect(m, fill.AsUInt32(), s).As<uint, T>();
        }
        else
        {
            Vector256<ulong> a = x.AsUInt64();
            Vector256<ulong> m, s;
            if (k == 1) { s = Avx2.Permute4x64(a, 0x90); m = Vector256.Create(~0ul, 0, 0, 0); }
            else { s = Avx2.Permute4x64(a, 0x40); m = Vector256.Create(~0ul, ~0ul, 0, 0); }
            return Vector256.ConditionalSelect(m, fill.AsUInt64(), s).As<ulong, T>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<T> Last256<T>(Vector256<T> x) where T : unmanaged =>
        sizeof(T) == 4
            ? Avx2.PermuteVar8x32(x.AsUInt32(), Vector256.Create(7u)).As<uint, T>()
            : Avx2.Permute4x64(x.AsUInt64(), 0xFF).As<ulong, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<T> Scan128<T, TOp>(Vector128<T> x, Vector128<T> id) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        x = TOp.Apply(x, Shift128(x, id, 1));
        if (sizeof(T) == 4) x = TOp.Apply(x, Shift128(x, id, 2));
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<T> Shift128<T>(Vector128<T> x, Vector128<T> fill, int k) where T : unmanaged
    {
        if (sizeof(T) == 4)
        {
            Vector128<uint> a = x.AsUInt32();
            Vector128<uint> s = k == 1 ? Vector128.Shuffle(a, Vector128.Create(0u, 0, 1, 2)) : Vector128.Shuffle(a, Vector128.Create(0u, 0, 0, 1));
            Vector128<uint> m = k == 1 ? Vector128.Create(~0u, 0, 0, 0) : Vector128.Create(~0u, ~0u, 0, 0);
            return Vector128.ConditionalSelect(m, fill.AsUInt32(), s).As<uint, T>();
        }
        else
        {
            Vector128<ulong> s = Vector128.Shuffle(x.AsUInt64(), Vector128.Create(0ul, 0));
            return Vector128.ConditionalSelect(Vector128.Create(~0ul, 0), fill.AsUInt64(), s).As<ulong, T>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<T> Last128<T>(Vector128<T> x) where T : unmanaged =>
        sizeof(T) == 4
            ? Vector128.Shuffle(x.AsUInt32(), Vector128.Create(3u)).As<uint, T>()
            : Vector128.Shuffle(x.AsUInt64(), Vector128.Create(1ul)).As<ulong, T>();
}
