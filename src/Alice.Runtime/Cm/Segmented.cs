using System.Numerics;

namespace Alice.Runtime;

/// <summary>
/// Segmented scan and reduce: <c>flags[i]</c> true starts a new segment at <c>i</c> (index 0 always starts one).
/// The scan operator works on (flag, value) pairs, so the chunked reduce-then-scan of <see cref="Cm.InclusiveScan{T}"/>
/// carries over: a chunk's carry-out is its tail since its last flag, or carry-in op the whole chunk when it has none.
/// Chunk-local passes are scalar (the flag test is per element).
/// </summary>
public static unsafe partial class Cm
{
    /// <summary>Inclusive scan restarted at every flag: <c>dst[i] = src[s] op ... op src[i]</c>, s = last flag &lt;= i.</summary>
    public static void SegmentedScan<T>(ReadOnlySpan<T> src, ReadOnlySpan<bool> flags, Span<T> dst, CmOp op = CmOp.Add) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        CheckOp<T>(op);
        Check(flags.Length == src.Length, "segment flags and values differ in length");
        Check(dst.Length >= src.Length, "scan destination is shorter than the source");
        switch (op)
        {
            case CmOp.Add: SegScanAll<T, AddOp<T>>(src, flags, dst); break;
            case CmOp.Mul: SegScanAll<T, MulOp<T>>(src, flags, dst); break;
            case CmOp.And: SegScanAll<T, AndOp<T>>(src, flags, dst); break;
            case CmOp.Or: SegScanAll<T, OrOp<T>>(src, flags, dst); break;
            case CmOp.Xor: SegScanAll<T, XorOp<T>>(src, flags, dst); break;
            case CmOp.Min: SegScanAll<T, MinOp<T>>(src, flags, dst); break;
            default: SegScanAll<T, MaxOp<T>>(src, flags, dst); break;
        }
    }

    /// <summary>Number of segments <paramref name="flags"/> describes: 0 when empty, else 1 + flags set after index 0.</summary>
    [Pure]
    public static int SegmentCount(ReadOnlySpan<bool> flags) => flags.Length == 0 ? 0 : 1 + flags[1..].Count(true);

    /// <summary>One result per segment, in order, into <paramref name="results"/> (length &gt;= <see cref="SegmentCount"/>);
    /// returns the segment count. <paramref name="results"/> is not touched when it is too short.</summary>
    public static int SegmentedReduce<T>(ReadOnlySpan<T> src, ReadOnlySpan<bool> flags, Span<T> results, CmOp op = CmOp.Add) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        CheckOp<T>(op);
        Check(flags.Length == src.Length, "segment flags and values differ in length");
        return op switch
        {
            CmOp.Add => SegReduceAll<T, AddOp<T>>(src, flags, results),
            CmOp.Mul => SegReduceAll<T, MulOp<T>>(src, flags, results),
            CmOp.And => SegReduceAll<T, AndOp<T>>(src, flags, results),
            CmOp.Or => SegReduceAll<T, OrOp<T>>(src, flags, results),
            CmOp.Xor => SegReduceAll<T, XorOp<T>>(src, flags, results),
            CmOp.Min => SegReduceAll<T, MinOp<T>>(src, flags, results),
            _ => SegReduceAll<T, MaxOp<T>>(src, flags, results),
        };
    }

    struct SegCtx<T> where T : unmanaged
    {
        public T* Src, Dst, Carry;
        public bool* Flags;
        public int* Seg;     // per chunk: flags after index 0 (pass 1), then the chunk's first segment index
        public byte* Has;    // per chunk: any flag in the chunk
        public int N, Chunk;
    }

    static void SegScanAll<T, TOp>(ReadOnlySpan<T> src, ReadOnlySpan<bool> flags, Span<T> dst) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        int n = src.Length;
        if (n == 0) return;
        int chunk = ChunkSize(n), chunks = (n + chunk - 1) / chunk;
        T* carry = stackalloc T[chunks];
        int* seg = stackalloc int[chunks];
        byte* has = stackalloc byte[chunks];
        fixed (T* s = src, d = dst)
        fixed (bool* f = flags)
        {
            var ctx = new SegCtx<T> { Src = s, Dst = d, Carry = carry, Flags = f, Seg = seg, Has = has, N = n, Chunk = chunk };
            SegCarries<T, TOp>(&ctx, chunks);
            KernelPool.Run(chunks, ChunkGrain(chunk), &SegScanChunks<T, TOp>, &ctx);
        }
    }

    static int SegReduceAll<T, TOp>(ReadOnlySpan<T> src, ReadOnlySpan<bool> flags, Span<T> results) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        int n = src.Length;
        if (n == 0) return 0;
        int chunk = ChunkSize(n), chunks = (n + chunk - 1) / chunk;
        T* carry = stackalloc T[chunks];
        int* seg = stackalloc int[chunks];
        byte* has = stackalloc byte[chunks];
        fixed (T* s = src, r = results)
        fixed (bool* f = flags)
        {
            var ctx = new SegCtx<T> { Src = s, Dst = r, Carry = carry, Flags = f, Seg = seg, Has = has, N = n, Chunk = chunk };
            int segments = SegCarries<T, TOp>(&ctx, chunks);
            if (!(results.Length >= segments)) throw new ArgumentException($"segmented reduce needs {segments} results, got {results.Length}");
            KernelPool.Run(chunks, ChunkGrain(chunk), &SegReduceChunks<T, TOp>, &ctx);
            return segments;
        }
    }

    /// <summary>Pass 1 plus the serial chunk scan: Carry[c] = the open segment's value entering chunk c, Seg[c] = the
    /// segment index at chunk c's first element. Returns the segment count.</summary>
    static int SegCarries<T, TOp>(SegCtx<T>* ctx, int chunks) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        if (chunks > 1) KernelPool.Run(chunks, ChunkGrain(ctx->Chunk), &SegTotals<T, TOp>, ctx);
        else
        {
            int count = 0;
            for (int i = 1; i < ctx->N; i++) count += ctx->Flags[i] ? 1 : 0;
            ctx->Seg[0] = count; ctx->Has[0] = 0;
        }
        T acc = TOp.Identity;
        int segs = 0;
        for (int c = 0; c < chunks; c++)
        {
            T total = ctx->Carry[c];
            int count = ctx->Seg[c];
            ctx->Carry[c] = acc;
            ctx->Seg[c] = segs;
            segs += count;
            if (c + 1 < chunks) acc = ctx->Has[c] != 0 ? total : TOp.Apply(acc, total);
        }
        return segs + 1;
    }

    static void SegTotals<T, TOp>(void* p, int lo, int hi) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        var c = (SegCtx<T>*)p;
        for (int k = lo; k < hi; k++)
        {
            int a = k * c->Chunk, b = Math.Min(c->N, a + c->Chunk);
            T acc = TOp.Identity;
            int count = 0;
            byte has = 0;
            for (int i = a; i < b; i++)
            {
                if (c->Flags[i]) { acc = c->Src[i]; has = 1; if (i > 0) count++; }
                else acc = TOp.Apply(acc, c->Src[i]);
            }
            c->Carry[k] = acc; c->Seg[k] = count; c->Has[k] = has;
        }
    }

    static void SegScanChunks<T, TOp>(void* p, int lo, int hi) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        var c = (SegCtx<T>*)p;
        for (int k = lo; k < hi; k++)
        {
            int a = k * c->Chunk, b = Math.Min(c->N, a + c->Chunk);
            T acc = c->Carry[k];
            T* s = c->Src, d = c->Dst;
            bool* f = c->Flags;
            for (int i = a; i < b; i++)
            {
                T x = s[i];
                acc = f[i] ? x : TOp.Apply(acc, x);
                d[i] = acc;
            }
        }
    }

    static void SegReduceChunks<T, TOp>(void* p, int lo, int hi) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        var c = (SegCtx<T>*)p;
        int n = c->N;
        for (int k = lo; k < hi; k++)
        {
            int a = k * c->Chunk, b = Math.Min(n, a + c->Chunk);
            T acc = c->Carry[k];
            int seg = c->Seg[k];
            T* s = c->Src;
            bool* f = c->Flags;
            for (int i = a; i < b; i++)
            {
                T x = s[i];
                if (f[i]) { if (i > 0) seg++; acc = x; }
                else acc = TOp.Apply(acc, x);
                if (i + 1 == n || f[i + 1]) c->Dst[seg] = acc;
            }
        }
    }
}
