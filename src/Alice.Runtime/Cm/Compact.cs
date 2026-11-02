using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Alice.Runtime;

/// <summary>
/// Parallel compaction (stream compaction / pack): count the set flags per chunk, exclusive-scan the counts, then every
/// chunk copies its kept elements to its own offset. Order is preserved, so the result equals the serial filter.
/// Works for any element type (arrays of references too). The predicate form evaluates the block once per element
/// into native flag bytes; the block runs on several threads at once and must be pure.
/// </summary>
public static unsafe partial class Cm
{
    /// <summary>Copies <c>src[i]</c> with <c>flags[i]</c> set to the front of <paramref name="dst"/>, in order; returns
    /// how many. <paramref name="dst"/> is not touched when it is shorter than that count.</summary>
    public static int Compact<T>(T[] src, ReadOnlySpan<bool> flags, T[] dst)
    {
        Check(flags.Length == src.Length, "compact flags and values differ in length");
        fixed (bool* f = flags) return CompactCore(src, f, dst, null, out _);
    }

    /// <summary>Copies the elements for which <paramref name="keep"/> holds to the front of <paramref name="dst"/>, in
    /// order; returns how many (dst untouched when too short). Uses cached native scratch: no GC allocation.</summary>
    public static int Compact<T>(T[] src, Func<T, bool> keep, T[] dst)
    {
        byte* scratch = Rent((nuint)Math.Max(1, src.Length));
        try { return CompactCore(src, (bool*)scratch, dst, keep, out _); }
        finally { Return(scratch); }
    }

    /// <summary>The elements for which <paramref name="keep"/> holds, in order, as a new array.</summary>
    public static T[] Compact<T>(T[] src, Func<T, bool> keep)
    {
        byte* scratch = Rent((nuint)Math.Max(1, src.Length));
        try
        {
            CompactCore(src, (bool*)scratch, null, keep, out var result);
            return result!;
        }
        finally { Return(scratch); }
    }

    struct CompactCtx<T>
    {
        public T[] Src;
        public T[]? Dst;
        public Func<T, bool>? Keep;
        public Exception? Error;
        public bool* Flags;
        public int* Offsets;
        public int N, Chunk;
    }

    /// <summary>dst null: allocate the result (returned in <paramref name="result"/>) once the count is known.</summary>
    static int CompactCore<T>(T[] src, bool* flags, T[]? dst, Func<T, bool>? keep, out T[]? result)
    {
        int n = src.Length;
        int chunk = ChunkSize(n), chunks = Math.Max(1, (n + chunk - 1) / chunk);
        int* offsets = stackalloc int[chunks];
        var ctx = new CompactCtx<T> { Src = src, Dst = dst, Keep = keep, Flags = flags, Offsets = offsets, N = n, Chunk = chunk };
        void* p = Unsafe.AsPointer(ref ctx);
        if (n == 0) offsets[0] = 0;
        else KernelPool.Run(chunks, ChunkGrain(chunk), &CompactCount<T>, p);
        if (ctx.Error != null) ExceptionDispatchInfo.Throw(ctx.Error);
        int total = ExclusiveCounts(offsets, chunks);
        if (dst == null) ctx.Dst = dst = total == 0 ? [] : new T[total];
        if (!(dst.Length >= total)) throw new ArgumentException($"compact needs {total} destination slots, got {dst.Length}");
        if (total > 0) KernelPool.Run(chunks, ChunkGrain(chunk), &CompactScatter<T>, p);
        result = dst;
        return total;
    }

    static void CompactCount<T>(void* p, int lo, int hi)
    {
        ref var c = ref Unsafe.AsRef<CompactCtx<T>>(p);
        for (int k = lo; k < hi; k++)
        {
            int a = k * c.Chunk, len = Math.Min(c.Chunk, c.N - a);
            if (c.Keep is { } keep)
            {
                int count = 0;
                T[] src = c.Src;
                bool* f = c.Flags;
                try
                {
                    for (int i = a; i < a + len; i++) { bool b = keep(src[i]); f[i] = b; count += b ? 1 : 0; }
                }
                catch (Exception e) { Interlocked.CompareExchange(ref c.Error, e, null); }
                c.Offsets[k] = count;
            }
            else c.Offsets[k] = new ReadOnlySpan<bool>(c.Flags + a, len).Count(true);
        }
    }

    static void CompactScatter<T>(void* p, int lo, int hi)
    {
        ref var c = ref Unsafe.AsRef<CompactCtx<T>>(p);
        T[] src = c.Src, dst = c.Dst!;
        bool* f = c.Flags;
        for (int k = lo; k < hi; k++)
        {
            int a = k * c.Chunk, e = Math.Min(c.N, a + c.Chunk), o = c.Offsets[k];
            for (int i = a; i < e; i++)
                if (f[i]) dst[o++] = src[i];
        }
    }
}
