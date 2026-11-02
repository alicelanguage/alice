using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Alice.Runtime;

/// <summary>Associative operator of a CM scan or reduce. Bitwise operators need an integer element type.</summary>
public enum CmOp { Add, Mul, And, Or, Xor, Min, Max }

/// <summary>
/// Connection-Machine data-parallel primitives (ADR 0039): scan, segmented scan/reduce, combine (histogram,
/// scatter-add), compaction, permutation and radix sort by key. Every kernel is chunked by the input length only
/// (<see cref="ChunkSize"/>), never by the worker count, so integer results are exact and float results are
/// bit-identical for any <c>DOTNET_PROCESSOR_COUNT</c> on the same machine (the float association inside a chunk
/// follows the SIMD tier, so another ISA may round differently). Work is dispatched through
/// <see cref="KernelPool.Run"/> with a context on the caller's stack: no allocation in steady state. Scratch memory
/// (combine tables, flag bytes, bit sets, radix buffers) is native and cached for reuse (<see cref="TrimScratch"/>).
/// </summary>
public static unsafe partial class Cm
{
    /// <summary>Elements per chunk: max(4096, ceil(n / 1024)) rounded up to 64 (a whole number of 512-bit vectors
    /// of any element size). A function of <paramref name="n"/> only, so at most 1024 chunks.</summary>
    [Pure]
    public static int ChunkSize(int n)
    {
        long c = Math.Max(4096L, ((long)n + 1023) / 1024);
        return (int)((c + 63) & ~63L);
    }

    internal const int MaxChunks = 1024;

    /// <summary>Chunks handed to one pool task: about 64K elements each, so dispatch is amortized.</summary>
    internal static int ChunkGrain(int chunk) => Math.Max(1, 65536 / chunk);

    internal static void Check(bool ok, string message)
    {
        if (!ok) throw new ArgumentException(message);
    }

    internal static bool IsFloat<T>() => typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T) == typeof(Half);

    internal static void CheckOp<T>(CmOp op)
    {
        if (op is CmOp.And or CmOp.Or or CmOp.Xor && IsFloat<T>())
            throw new ArgumentException($"CM {op} needs an integer element type, not {typeof(T).Name}");
        if ((uint)op > (uint)CmOp.Max) throw new ArgumentOutOfRangeException(nameof(op));
    }

    // ---------------- cached native scratch ----------------

    static nint _scratch;   // header: nuint size at offset 0, payload at +64

    /// <summary>Takes the cached scratch block, or a new one of at least <paramref name="bytes"/>. Pair with
    /// <see cref="Return"/>. Concurrent callers each get their own block; only one is kept.</summary>
    internal static byte* Rent(nuint bytes)
    {
        nint p = Interlocked.Exchange(ref _scratch, 0);
        if (p != 0)
        {
            if (*(nuint*)p >= bytes) return (byte*)p + 64;
            NativeMemory.AlignedFree((void*)p);
        }
        nuint size = Math.Max(bytes, (nuint)(1 << 16));
        size = (nuint)BitOperations.RoundUpToPowerOf2((ulong)size);
        p = (nint)NativeMemory.AlignedAlloc(size + 64, 64);
        *(nuint*)p = size;
        return (byte*)p + 64;
    }

    internal static void Return(byte* payload)
    {
        nint p = (nint)(payload - 64);
        nint old = Interlocked.Exchange(ref _scratch, p);
        if (old == 0) return;
        // keep the larger block
        if (*(nuint*)old > *(nuint*)p)
        {
            nint back = Interlocked.Exchange(ref _scratch, old);
            NativeMemory.AlignedFree((void*)back);
        }
        else NativeMemory.AlignedFree((void*)old);
    }

    /// <summary>Frees the cached scratch block (it holds the largest combine/compact/permute/sort buffer seen).</summary>
    public static void TrimScratch()
    {
        nint p = Interlocked.Exchange(ref _scratch, 0);
        if (p != 0) NativeMemory.AlignedFree((void*)p);
    }

    /// <summary>Exclusive scan of <paramref name="count"/> chunk totals in place, in chunk order; returns the total.</summary>
    internal static T ExclusiveTotals<T, TOp>(T* t, int count) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T> where TOp : struct, ICmOp<T>
    {
        T acc = TOp.Identity;
        for (int c = 0; c < count; c++)
        {
            T x = t[c];
            t[c] = acc;
            acc = TOp.Apply(acc, x);
        }
        return acc;
    }

    internal static int ExclusiveCounts(int* t, int count)
    {
        int acc = 0;
        for (int c = 0; c < count; c++) { int x = t[c]; t[c] = acc; acc += x; }
        return acc;
    }
}

/// <summary>An associative operator with its identity, scalar and vector forms (monomorphized per type).</summary>
internal interface ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    /// <summary>op(Identity, x) == x bit for bit (float addition uses -0.0).</summary>
    static abstract T Identity { get; }
    static abstract T Apply(T a, T b);
    static abstract Vector128<T> Apply(Vector128<T> a, Vector128<T> b);
    static abstract Vector256<T> Apply(Vector256<T> a, Vector256<T> b);
    static abstract Vector512<T> Apply(Vector512<T> a, Vector512<T> b);
}

internal struct AddOp<T> : ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    public static T Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => T.CreateTruncating(-0.0); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Apply(T a, T b) => a + b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector128<T> Apply(Vector128<T> a, Vector128<T> b) => a + b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<T> Apply(Vector256<T> a, Vector256<T> b) => a + b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector512<T> Apply(Vector512<T> a, Vector512<T> b) => a + b;
}

internal struct MulOp<T> : ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    public static T Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => T.One; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Apply(T a, T b) => a * b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector128<T> Apply(Vector128<T> a, Vector128<T> b) => a * b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<T> Apply(Vector256<T> a, Vector256<T> b) => a * b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector512<T> Apply(Vector512<T> a, Vector512<T> b) => a * b;
}

internal struct AndOp<T> : ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    public static T Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => T.AllBitsSet; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Apply(T a, T b) => a & b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector128<T> Apply(Vector128<T> a, Vector128<T> b) => a & b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<T> Apply(Vector256<T> a, Vector256<T> b) => a & b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector512<T> Apply(Vector512<T> a, Vector512<T> b) => a & b;
}

internal struct OrOp<T> : ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    public static T Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => T.Zero; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Apply(T a, T b) => a | b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector128<T> Apply(Vector128<T> a, Vector128<T> b) => a | b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<T> Apply(Vector256<T> a, Vector256<T> b) => a | b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector512<T> Apply(Vector512<T> a, Vector512<T> b) => a | b;
}

internal struct XorOp<T> : ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    public static T Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => T.Zero; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Apply(T a, T b) => a ^ b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector128<T> Apply(Vector128<T> a, Vector128<T> b) => a ^ b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<T> Apply(Vector256<T> a, Vector256<T> b) => a ^ b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector512<T> Apply(Vector512<T> a, Vector512<T> b) => a ^ b;
}

/// <summary>IEEE 754-2019 minimum for floats (NaN propagates, -0 &lt; +0), the same in scalar and vector form.</summary>
internal struct MinOp<T> : ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    public static T Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => T.CreateSaturating(double.PositiveInfinity); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Apply(T a, T b) => T.Min(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector128<T> Apply(Vector128<T> a, Vector128<T> b) => Vector128.Min(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<T> Apply(Vector256<T> a, Vector256<T> b) => Vector256.Min(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector512<T> Apply(Vector512<T> a, Vector512<T> b) => Vector512.Min(a, b);
}

internal struct MaxOp<T> : ICmOp<T> where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
{
    public static T Identity { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => T.CreateSaturating(double.NegativeInfinity); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static T Apply(T a, T b) => T.Max(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector128<T> Apply(Vector128<T> a, Vector128<T> b) => Vector128.Max(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<T> Apply(Vector256<T> a, Vector256<T> b) => Vector256.Max(a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector512<T> Apply(Vector512<T> a, Vector512<T> b) => Vector512.Max(a, b);
}
