using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Stable LSD radix sort of UInt32 keys carrying values: the CM counting sort. Four 8-bit digit passes, each one
/// histogram per block (<see cref="Histogram{TCount}(ReadOnlySpan{int}, Span{TCount})"/>'s private-table idea), an
/// exclusive scan in (digit, block) order that gives each block its write offset per digit, and a stable scatter.
/// A pass whose digit is the same for every key is skipped. Blocks are at least 64K keys (at most 256), so the result
/// (unique anyway, the sort is stable) is computed the same way for any worker count. Scratch: n keys + n values +
/// 256 KiB of counts, native and cached.
/// </summary>
public static unsafe partial class Cm
{
    const int RadixBlock = 65536, MaxRadixBlocks = 256;

    /// <summary>Sorts <paramref name="keys"/> ascending in place and applies the same (stable) permutation to
    /// <paramref name="vals"/>.</summary>
    public static void SortByKey<TV>(Span<uint> keys, Span<TV> vals) where TV : unmanaged
    {
        Check(keys.Length == vals.Length, "sort_by_key keys and values differ in length");
        int n = keys.Length;
        if (n <= 1) return;
        int blocks = Math.Clamp((n + RadixBlock - 1) / RadixBlock, 1, MaxRadixBlocks);
        long keyBytes = ((long)n * 4 + 63) & ~63L, valBytes = ((long)n * sizeof(TV) + 63) & ~63L;
        byte* scratch = Rent((nuint)(keyBytes + valBytes + (long)blocks * 256 * 4));
        try
        {
            fixed (uint* k = keys) fixed (TV* v = vals)
            {
                var ctx = new RadixCtx<TV>
                {
                    KeysIn = k, ValsIn = v, KeysOut = (uint*)scratch, ValsOut = (TV*)(scratch + keyBytes),
                    Hist = (int*)(scratch + keyBytes + valBytes), N = n, Blocks = blocks,
                };
                int* totals = stackalloc int[256];
                for (int shift = 0; shift < 32; shift += 8)
                {
                    ctx.Shift = shift;
                    KernelPool.Run(blocks, 1, &RadixCount<TV>, &ctx);
                    if (!RadixOffsets(ctx.Hist, blocks, n, totals)) continue;   // one digit value only: nothing moves
                    KernelPool.Run(blocks, 1, &RadixScatter<TV>, &ctx);
                    uint* tk = ctx.KeysIn; ctx.KeysIn = ctx.KeysOut; ctx.KeysOut = tk;
                    TV* tv = ctx.ValsIn; ctx.ValsIn = ctx.ValsOut; ctx.ValsOut = tv;
                }
                if (ctx.KeysIn != k)
                {
                    ctx.KeysOut = k; ctx.ValsOut = v;
                    KernelPool.Run(blocks, 1, &RadixCopyBack<TV>, &ctx);
                }
            }
        }
        finally { Return(scratch); }
    }

    struct RadixCtx<TV> where TV : unmanaged
    {
        public uint* KeysIn, KeysOut;
        public TV* ValsIn, ValsOut;
        public int* Hist;
        public int N, Blocks, Shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int BlockStart(int b, int n, int blocks) => (int)((long)b * n / blocks);

    static void RadixCount<TV>(void* p, int lo, int hi) where TV : unmanaged
    {
        var c = (RadixCtx<TV>*)p;
        for (int b = lo; b < hi; b++)
        {
            int* h = c->Hist + b * 256;
            new Span<int>(h, 256).Clear();
            int a = BlockStart(b, c->N, c->Blocks), e = BlockStart(b + 1, c->N, c->Blocks), s = c->Shift;
            uint* keys = c->KeysIn;
            for (int i = a; i < e; i++) h[(keys[i] >> s) & 0xFF]++;
        }
    }

    /// <summary>Turns per-block counts into write offsets (digit-major, block-minor). False when one digit holds
    /// every key, so the pass would be the identity.</summary>
    static bool RadixOffsets(int* hist, int blocks, int n, int* totals)
    {
        for (int d = 0; d < 256; d++)
        {
            int t = 0;
            for (int b = 0; b < blocks; b++) t += hist[b * 256 + d];
            if (t == n) return false;
            totals[d] = t;
        }
        int running = 0;
        for (int d = 0; d < 256; d++)
        {
            int r = running;
            for (int b = 0; b < blocks; b++) { int x = hist[b * 256 + d]; hist[b * 256 + d] = r; r += x; }
            running += totals[d];
        }
        return true;
    }

    static void RadixScatter<TV>(void* p, int lo, int hi) where TV : unmanaged
    {
        var c = (RadixCtx<TV>*)p;
        for (int b = lo; b < hi; b++)
        {
            int* h = c->Hist + b * 256;
            int a = BlockStart(b, c->N, c->Blocks), e = BlockStart(b + 1, c->N, c->Blocks), s = c->Shift;
            uint* ki = c->KeysIn, ko = c->KeysOut;
            TV* vi = c->ValsIn, vo = c->ValsOut;
            for (int i = a; i < e; i++)
            {
                uint key = ki[i];
                int o = h[(key >> s) & 0xFF]++;
                ko[o] = key;
                vo[o] = vi[i];
            }
        }
    }

    static void RadixCopyBack<TV>(void* p, int lo, int hi) where TV : unmanaged
    {
        var c = (RadixCtx<TV>*)p;
        int a = BlockStart(lo, c->N, c->Blocks), e = BlockStart(hi, c->N, c->Blocks);
        new ReadOnlySpan<uint>(c->KeysIn + a, e - a).CopyTo(new Span<uint>(c->KeysOut + a, e - a));
        new ReadOnlySpan<TV>(c->ValsIn + a, e - a).CopyTo(new Span<TV>(c->ValsOut + a, e - a));
    }
}
