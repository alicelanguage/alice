using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Alice.Core.Text;

/// <summary>
/// Teddy (Hyperscan's packed multi-literal search, as in Rust's aho-corasick): literals go to up to 8 buckets; the
/// first 1-4 bytes of each literal set bucket bits in nibble tables, and `vpshufb` looks up 32 (AVX2) or 64
/// (AVX-512) haystack bytes per step. A non-zero lane is a candidate; the literals of its buckets are verified in
/// pattern order, so the reported literal is the leftmost-first one at the leftmost position. Without SSSE3-class
/// shuffles it falls back to a first-byte `SearchValues` scan with the same verification.
/// </summary>
internal sealed class Teddy
{
    readonly byte[][] lits;
    readonly int[][] buckets;     // bucket -> literal indices (ascending = pattern order)
    readonly int masks;
    readonly Vector256<byte>[] lo256, hi256;
    readonly Vector512<byte>[] lo512, hi512;
    readonly SearchValues<byte> firsts;
    readonly int minLen;

    public Teddy(List<byte[]> literals)
    {
        lits = literals.ToArray();
        minLen = lits.Min(l => l.Length);
        masks = Math.Clamp(minLen, 1, 4);
        // bucket assignment: sort by fingerprint, cut into 8 contiguous groups (similar fingerprints share a bucket)
        var order = Enumerable.Range(0, lits.Length).OrderBy(i => Convert.ToHexString(lits[i], 0, masks)).ToArray();
        int nb = Math.Min(8, lits.Length);
        var bl = new List<int>[nb];
        for (int b = 0; b < nb; b++) bl[b] = [];
        for (int k = 0; k < order.Length; k++) bl[(int)((long)k * nb / order.Length)].Add(order[k]);
        buckets = bl.Select(l => l.OrderBy(x => x).ToArray()).ToArray();
        var lo = new byte[masks, 16];
        var hi = new byte[masks, 16];
        for (int b = 0; b < nb; b++)
            foreach (int li in buckets[b])
                for (int k = 0; k < masks; k++)
                {
                    lo[k, lits[li][k] & 0xF] |= (byte)(1 << b);
                    hi[k, lits[li][k] >> 4] |= (byte)(1 << b);
                }
        lo256 = new Vector256<byte>[4]; hi256 = new Vector256<byte>[4];
        lo512 = new Vector512<byte>[4]; hi512 = new Vector512<byte>[4];
        Span<byte> t = stackalloc byte[64];
        for (int k = 0; k < 4; k++)
        {
            if (k >= masks)
            {
                lo256[k] = hi256[k] = Vector256<byte>.AllBitsSet;
                lo512[k] = hi512[k] = Vector512<byte>.AllBitsSet;
                continue;
            }
            for (int j = 0; j < 64; j++) t[j] = lo[k, j & 15];
            lo256[k] = Vector256.Create<byte>(t[..32]); lo512[k] = Vector512.Create<byte>(t);
            for (int j = 0; j < 64; j++) t[j] = hi[k, j & 15];
            hi256[k] = Vector256.Create<byte>(t[..32]); hi512[k] = Vector512.Create<byte>(t);
        }
        firsts = SearchValues.Create(lits.Select(l => l[0]).Distinct().ToArray());
    }

    // the lowest-index literal of the given buckets that occurs at p, or -1
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int Verify(ReadOnlySpan<byte> h, int p, uint bucketBits)
    {
        int best = int.MaxValue;
        var rest = h[p..];
        while (bucketBits != 0)
        {
            int b = BitOperations.TrailingZeroCount(bucketBits);
            bucketBits &= bucketBits - 1;
            foreach (int li in buckets[b])
            {
                if (li >= best) break;
                if (rest.StartsWith(lits[li])) { best = li; break; }
            }
        }
        return best == int.MaxValue ? -1 : best;
    }

    int VerifyAll(ReadOnlySpan<byte> h, int p)
    {
        var rest = h[p..];
        for (int li = 0; li < lits.Length; li++) if (rest.StartsWith(lits[li])) return li;
        return -1;
    }

    /// <summary>The leftmost position at or after <paramref name="pos"/> where a literal occurs; <paramref name="lit"/> its index.</summary>
    public int Find(ReadOnlySpan<byte> h, int pos, out int lit)
    {
        int n = h.Length;
        int i = pos;
        ref byte h0 = ref MemoryMarshal.GetReference(h);
        if (Avx512BW.IsSupported && n - i >= 64 + 3)
        {
            var nib = Vector512.Create((byte)0x0F);
            Vector512<byte> l0 = lo512[0], l1 = lo512[1], l2 = lo512[2], l3 = lo512[3];
            Vector512<byte> g0 = hi512[0], g1 = hi512[1], g2 = hi512[2], g3 = hi512[3];
            while (i + 64 + 3 <= n)
            {
                var v0 = Vector512.LoadUnsafe(ref h0, (nuint)i);
                var v1 = Vector512.LoadUnsafe(ref h0, (nuint)(i + 1));
                var v2 = Vector512.LoadUnsafe(ref h0, (nuint)(i + 2));
                var v3 = Vector512.LoadUnsafe(ref h0, (nuint)(i + 3));
                var acc = Avx512BW.Shuffle(l0, v0 & nib) & Avx512BW.Shuffle(g0, Vector512.ShiftRightLogical(v0.AsUInt16(), 4).AsByte() & nib)
                        & Avx512BW.Shuffle(l1, v1 & nib) & Avx512BW.Shuffle(g1, Vector512.ShiftRightLogical(v1.AsUInt16(), 4).AsByte() & nib)
                        & Avx512BW.Shuffle(l2, v2 & nib) & Avx512BW.Shuffle(g2, Vector512.ShiftRightLogical(v2.AsUInt16(), 4).AsByte() & nib)
                        & Avx512BW.Shuffle(l3, v3 & nib) & Avx512BW.Shuffle(g3, Vector512.ShiftRightLogical(v3.AsUInt16(), 4).AsByte() & nib);
                ulong m = ~Vector512.Equals(acc, Vector512<byte>.Zero).ExtractMostSignificantBits();
                while (m != 0)
                {
                    int j = BitOperations.TrailingZeroCount(m);
                    m &= m - 1;
                    int r = Verify(h, i + j, acc.GetElement(j));
                    if (r >= 0) { lit = r; return i + j; }
                }
                i += 64;
            }
        }
        else if (Avx2.IsSupported && n - i >= 32 + 3)
        {
            var nib = Vector256.Create((byte)0x0F);
            Vector256<byte> l0 = lo256[0], l1 = lo256[1], l2 = lo256[2], l3 = lo256[3];
            Vector256<byte> g0 = hi256[0], g1 = hi256[1], g2 = hi256[2], g3 = hi256[3];
            while (i + 32 + 3 <= n)
            {
                var v0 = Vector256.LoadUnsafe(ref h0, (nuint)i);
                var v1 = Vector256.LoadUnsafe(ref h0, (nuint)(i + 1));
                var v2 = Vector256.LoadUnsafe(ref h0, (nuint)(i + 2));
                var v3 = Vector256.LoadUnsafe(ref h0, (nuint)(i + 3));
                var acc = Avx2.Shuffle(l0, v0 & nib) & Avx2.Shuffle(g0, Vector256.ShiftRightLogical(v0.AsUInt16(), 4).AsByte() & nib)
                        & Avx2.Shuffle(l1, v1 & nib) & Avx2.Shuffle(g1, Vector256.ShiftRightLogical(v1.AsUInt16(), 4).AsByte() & nib)
                        & Avx2.Shuffle(l2, v2 & nib) & Avx2.Shuffle(g2, Vector256.ShiftRightLogical(v2.AsUInt16(), 4).AsByte() & nib)
                        & Avx2.Shuffle(l3, v3 & nib) & Avx2.Shuffle(g3, Vector256.ShiftRightLogical(v3.AsUInt16(), 4).AsByte() & nib);
                uint m = ~Vector256.Equals(acc, Vector256<byte>.Zero).ExtractMostSignificantBits();
                while (m != 0)
                {
                    int j = BitOperations.TrailingZeroCount(m);
                    m &= m - 1;
                    int r = Verify(h, i + j, acc.GetElement(j));
                    if (r >= 0) { lit = r; return i + j; }
                }
                i += 32;
            }
        }
        // portable path and tail: first-byte scan + verification
        while (i < n)
        {
            int r = h[i..].IndexOfAny(firsts);
            if (r < 0) break;
            i += r;
            int li = VerifyAll(h, i);
            if (li >= 0) { lit = li; return i; }
            i++;
        }
        lit = -1;
        return -1;
    }
}
