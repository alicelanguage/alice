using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Alice.Runtime;

/// <summary>
/// Combining send (the CM router's <c>a[idx[i]] (+)= v[i]</c>): histogram and scatter-add.
/// <list type="bullet">
/// <item>Private tables when <c>k * sizeof(T) &lt;= 256 KiB</c>: the input is cut into B blocks, block b counts into its
/// own zeroed table, then every column is merged as <c>target[j] + t0[j] + t1[j] + ...</c> in block order. Integers use
/// B = workers (exact under any order); floats use B = min(64, n / 16384), a function of n only, so the bit pattern
/// never depends on the worker count. B is also capped at 4n/k so zeroing never dominates.</item>
/// <item>Larger integer tables: <c>Interlocked.Add</c> straight into the target.</item>
/// <item>Larger float tables, and inputs under 16K elements: one serial pass in index order (the ordered path).</item>
/// </list>
/// An index outside [0, k) raises <see cref="ArgumentOutOfRangeException"/>; the private-table path leaves the target
/// untouched then, the atomic and serial paths may have applied part of the input.
/// </summary>
public static unsafe partial class Cm
{
    internal const int TableBytes = 256 * 1024;
    const int MinBlock = 16384;

    /// <summary><c>bins[keys[i]] += 1</c> for every i (counts accumulate into <paramref name="bins"/>).</summary>
    public static void Histogram<TCount>(ReadOnlySpan<int> keys, Span<TCount> bins) where TCount : unmanaged, IBinaryInteger<TCount>
    {
        fixed (int* k = keys) fixed (TCount* b = bins) Combine<TCount, Ones<TCount>>(b, bins.Length, k, null, keys.Length);
    }

    /// <inheritdoc cref="Histogram{TCount}(ReadOnlySpan{int}, Span{TCount})"/>
    public static void Histogram<TCount>(ReadOnlySpan<uint> keys, Span<TCount> bins) where TCount : unmanaged, IBinaryInteger<TCount>
        => Histogram(MemoryMarshal.Cast<uint, int>(keys), bins);

    /// <summary><c>target[idx[i]] += vals[i]</c> for every i.</summary>
    public static void ScatterAdd<T>(Span<T> target, ReadOnlySpan<int> idx, ReadOnlySpan<T> vals) where T : unmanaged, INumber<T>
    {
        Check(idx.Length == vals.Length, "scatter_add indices and values differ in length");
        fixed (int* k = idx) fixed (T* t = target, v = vals) Combine<T, Given<T>>(t, target.Length, k, v, idx.Length);
    }

    interface IVals<T> where T : unmanaged { static abstract T Get(T* vals, int i); }
    struct Ones<T> : IVals<T> where T : unmanaged, INumber<T> { public static T Get(T* vals, int i) => T.One; }
    struct Given<T> : IVals<T> where T : unmanaged { public static T Get(T* vals, int i) => vals[i]; }

    struct CombineCtx<T> where T : unmanaged
    {
        public T* Target, Vals, Tables;
        public int* Idx;
        public int N, K, Blocks, Stride, Chunk, Bad;
    }

    static void Combine<T, TV>(T* target, int k, int* idx, T* vals, int n) where T : unmanaged, INumber<T> where TV : struct, IVals<T>
    {
        if (n == 0) return;
        bool isInt = !IsFloat<T>();
        long tableBytes = (long)k * sizeof(T);
        int nb = (n + MinBlock - 1) / MinBlock;
        var ctx = new CombineCtx<T> { Target = target, Vals = vals, Idx = idx, N = n, K = k };
        if (tableBytes <= TableBytes && k > 0)
        {
            int blocks = isInt ? Math.Min(KernelPool.Threads, nb) : Math.Min(64, nb);
            blocks = (int)Math.Min(blocks, Math.Max(1, 4L * n / k));
            if (blocks > 1)
            {
                int stride = (int)((tableBytes + 63) & ~63L) / sizeof(T);
                byte* scratch = Rent((nuint)((long)blocks * stride * sizeof(T)));
                try
                {
                    ctx.Tables = (T*)scratch; ctx.Blocks = blocks; ctx.Stride = stride;
                    KernelPool.Run(blocks, 1, &CombineBlocks<T, TV>, &ctx);
                    if (ctx.Bad != 0) throw BadIndex(k);
                    KernelPool.Run(k, 4096, &MergeColumns<T>, &ctx);
                }
                finally { Return(scratch); }
                return;
            }
        }
        else if (isInt && (sizeof(T) == 4 || sizeof(T) == 8) && n >= 2 * MinBlock)
        {
            ctx.Chunk = MinBlock;
            KernelPool.Run(nb, 1, &CombineAtomic<T, TV>, &ctx);
            if (ctx.Bad != 0) throw BadIndex(k);
            return;
        }
        for (int i = 0; i < n; i++)
        {
            int j = idx[i];
            if ((uint)j >= (uint)k) throw BadIndex(k);
            target[j] += TV.Get(vals, i);
        }
    }

    static ArgumentOutOfRangeException BadIndex(int k) => new("idx", $"combine index outside [0, {k})");

    static void CombineBlocks<T, TV>(void* p, int lo, int hi) where T : unmanaged, INumber<T> where TV : struct, IVals<T>
    {
        var c = (CombineCtx<T>*)p;
        for (int b = lo; b < hi; b++)
        {
            T* t = c->Tables + (long)b * c->Stride;
            new Span<T>(t, c->K).Clear();
            int a = (int)((long)b * c->N / c->Blocks), e = (int)((long)(b + 1) * c->N / c->Blocks);
            uint k = (uint)c->K;
            int* idx = c->Idx;
            T* vals = c->Vals;
            for (int i = a; i < e; i++)
            {
                int j = idx[i];
                if ((uint)j >= k) { Volatile.Write(ref c->Bad, 1); break; }
                t[j] += TV.Get(vals, i);
            }
        }
    }

    static void MergeColumns<T>(void* p, int lo, int hi) where T : unmanaged, INumber<T>
    {
        var c = (CombineCtx<T>*)p;
        T* d = c->Target + lo;
        int len = hi - lo;
        for (int b = 0; b < c->Blocks; b++) AddInto(d, c->Tables + (long)b * c->Stride + lo, len);
    }

    /// <summary><c>d[j] = d[j] + s[j]</c>; elementwise, so the vector width does not change any result.</summary>
    static void AddInto<T>(T* d, T* s, int n) where T : unmanaged, INumber<T>
    {
        int i = 0;
        if (Vector512.IsHardwareAccelerated && Vector512<T>.IsSupported)
            for (int w = Vector512<T>.Count; i + w <= n; i += w) (Vector512.Load(d + i) + Vector512.Load(s + i)).Store(d + i);
        else if (Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported)
            for (int w = Vector256<T>.Count; i + w <= n; i += w) (Vector256.Load(d + i) + Vector256.Load(s + i)).Store(d + i);
        else if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported)
            for (int w = Vector128<T>.Count; i + w <= n; i += w) (Vector128.Load(d + i) + Vector128.Load(s + i)).Store(d + i);
        for (; i < n; i++) d[i] += s[i];
    }

    static void CombineAtomic<T, TV>(void* p, int lo, int hi) where T : unmanaged, INumber<T> where TV : struct, IVals<T>
    {
        var c = (CombineCtx<T>*)p;
        uint k = (uint)c->K;
        int a = lo * c->Chunk, e = (int)Math.Min(c->N, (long)hi * c->Chunk);
        int* idx = c->Idx;
        T* t = c->Target, vals = c->Vals;
        for (int i = a; i < e; i++)
        {
            int j = idx[i];
            if ((uint)j >= k) { Volatile.Write(ref c->Bad, 1); continue; }
            T v = TV.Get(vals, i);
            if (sizeof(T) == 4) Interlocked.Add(ref *(int*)(t + j), Unsafe.BitCast<T, int>(v));
            else Interlocked.Add(ref *(long*)(t + j), Unsafe.BitCast<T, long>(v));
        }
    }
}
