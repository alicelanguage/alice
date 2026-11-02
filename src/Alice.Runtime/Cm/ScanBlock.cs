using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// Inclusive scan with a user operator (the <c>xs.scan { |a, b| ... }</c> block form): the same chunking and
/// reduce-then-scan passes as <see cref="Cm.InclusiveScan{T}"/>, but one delegate call per element and no SIMD, so
/// it is several times slower than the <see cref="CmOp"/> forms. The operator must be associative (the result equals
/// the serial fold only then) and pure (it runs on several threads at once). The first exception it throws is
/// rethrown after the pass.
/// </summary>
public static unsafe partial class Cm
{
    /// <summary><c>dst[0] = src[0], dst[i] = f(dst[i-1], src[i])</c> up to reassociation at chunk boundaries.</summary>
    public static void InclusiveScan<T>(T[] src, Func<T, T, T> f, T[] dst)
    {
        Check(dst.Length >= src.Length, "scan destination is shorter than the source");
        int n = src.Length;
        if (n == 0) return;
        int chunk = ChunkSize(n), chunks = (n + chunk - 1) / chunk;
        var ctx = new BlockScanCtx<T> { Src = src, Dst = dst, F = f, N = n, Chunk = chunk };
        void* p = Unsafe.AsPointer(ref ctx);
        if (chunks > 1)
        {
            ctx.Carry = new T[chunks];
            KernelPool.Run(chunks - 1, ChunkGrain(chunk), &BlockTotals<T>, p);   // the last chunk's total is not needed
            if (ctx.Error != null) ExceptionDispatchInfo.Throw(ctx.Error);
            T[] carry = ctx.Carry;
            for (int k = 2; k < chunks; k++) carry[k - 1] = f(carry[k - 2], carry[k - 1]);
            // carry[k] now holds the fold of chunks 0..k; chunk k + 1 starts from it
        }
        KernelPool.Run(chunks, ChunkGrain(chunk), &BlockScanChunks<T>, p);
        if (ctx.Error != null) ExceptionDispatchInfo.Throw(ctx.Error);
    }

    struct BlockScanCtx<T>
    {
        public T[] Src, Dst;
        public T[]? Carry;
        public Func<T, T, T> F;
        public Exception? Error;
        public int N, Chunk;
    }

    static void BlockTotals<T>(void* p, int lo, int hi)
    {
        ref var c = ref Unsafe.AsRef<BlockScanCtx<T>>(p);
        try
        {
            for (int k = lo; k < hi; k++)
            {
                int a = k * c.Chunk, e = Math.Min(c.N, a + c.Chunk);
                T acc = c.Src[a];
                for (int i = a + 1; i < e; i++) acc = c.F(acc, c.Src[i]);
                c.Carry![k] = acc;
            }
        }
        catch (Exception e) { Interlocked.CompareExchange(ref c.Error, e, null); }
    }

    static void BlockScanChunks<T>(void* p, int lo, int hi)
    {
        ref var c = ref Unsafe.AsRef<BlockScanCtx<T>>(p);
        T[] s = c.Src, d = c.Dst;
        var f = c.F;
        try
        {
            for (int k = lo; k < hi; k++)
            {
                int a = k * c.Chunk, e = Math.Min(c.N, a + c.Chunk);
                T acc = k == 0 ? s[0] : f(c.Carry![k - 1], s[a]);
                d[a] = acc;
                for (int i = a + 1; i < e; i++) d[i] = acc = f(acc, s[i]);
            }
        }
        catch (Exception e) { Interlocked.CompareExchange(ref c.Error, e, null); }
    }
}
