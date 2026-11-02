using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>
/// Permute (the CM router's send without combining): <c>dst[perm[i]] = src[i]</c>. One parallel pass checks each target
/// index against the destination length and claims it in a bit set with an atomic OR; only the first claimer of a target
/// writes it, so a duplicate never races. Out-of-range or duplicate targets raise after the pass (the destination then
/// holds the writes of the valid, first-claimed targets).
/// </summary>
public static unsafe partial class Cm
{
    /// <summary><c>dst[perm[i]] = src[i]</c>; <paramref name="perm"/> must hold distinct indices into <paramref name="dst"/>.</summary>
    public static void Permute<T>(T[] src, ReadOnlySpan<int> perm, T[] dst)
    {
        Check(perm.Length == src.Length, "permute indices and values differ in length");
        int n = src.Length, m = dst.Length;
        if (n == 0) return;
        nuint words = (nuint)((m + 63L) / 64);
        byte* scratch = Rent(Math.Max(1, words) * 8);
        try
        {
            NativeMemory.Clear(scratch, words * 8);
            fixed (int* pp = perm)
            {
                int chunk = ChunkSize(n), chunks = (n + chunk - 1) / chunk;
                var ctx = new PermuteCtx<T> { Src = src, Dst = dst, Perm = pp, Bits = (ulong*)scratch, N = n, Chunk = chunk };
                KernelPool.Run(chunks, ChunkGrain(chunk), &PermuteChunks<T>, Unsafe.AsPointer(ref ctx));
                if (ctx.Bad != 0) throw new ArgumentOutOfRangeException(nameof(perm), $"permute index outside [0, {m})");
                if (ctx.Dup != 0) throw new ArgumentException("permute indices are not distinct", nameof(perm));
            }
        }
        finally { Return(scratch); }
    }

    struct PermuteCtx<T>
    {
        public T[] Src, Dst;
        public int* Perm;
        public ulong* Bits;
        public int N, Chunk, Bad, Dup;
    }

    static void PermuteChunks<T>(void* p, int lo, int hi)
    {
        ref var c = ref Unsafe.AsRef<PermuteCtx<T>>(p);
        T[] src = c.Src, dst = c.Dst;
        int* perm = c.Perm;
        ulong* bits = c.Bits;
        uint m = (uint)dst.Length;
        int a = lo * c.Chunk, e = (int)Math.Min(c.N, (long)hi * c.Chunk);
        for (int i = a; i < e; i++)
        {
            int j = perm[i];
            if ((uint)j >= m) { Volatile.Write(ref c.Bad, 1); continue; }
            ulong bit = 1ul << (j & 63);
            if ((Interlocked.Or(ref *(long*)&bits[j >> 6], (long)bit) & (long)bit) != 0) { Volatile.Write(ref c.Dup, 1); continue; }
            dst[j] = src[i];
        }
    }
}
