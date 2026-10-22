using System.Numerics;

namespace Alice.Runtime;

/// <summary>
/// Fork/join reductions behind `sum`, `min` and `max` of large primitive arrays (ADR 0015): the array splits by
/// index halves through <see cref="Par.Fork{A, B}"/> near the root, each leaf is one SIMD pass over its span, and
/// partial results combine in index order. Callers see the same method; small arrays never leave the SIMD path.
/// </summary>
static class ParReduce
{
    /// <summary>A SIMD pass over fewer elements is faster than forking (memory-bound below this).</summary>
    public const int MinLength = 1 << 20;

    const int Leaf = 1 << 18;

    public static T Sum<T>(T[] a) where T : unmanaged, INumber<T> => Rec(a, 0, a.Length, static s => Simd.Sum<T>(s), static (x, y) => x + y);

    public static T Max<T>(T[] a) where T : unmanaged, INumber<T> => Rec(a, 0, a.Length, static s => Simd.Max<T>(s), T.Max);

    public static T Min<T>(T[] a) where T : unmanaged, INumber<T> => Rec(a, 0, a.Length, static s => Simd.Min<T>(s), T.Min);

    delegate T Pass<T>(ReadOnlySpan<T> span);

    static T Rec<T>(T[] a, int lo, int hi, Pass<T> leaf, Func<T, T, T> combine)
    {
        if (hi - lo <= Leaf || !Par.Forkable) return leaf(a.AsSpan(lo, hi - lo));
        int mid = lo + (hi - lo) / 2;
        var (x, y) = Par.Fork(() => Rec(a, lo, mid, leaf, combine), () => Rec(a, mid, hi, leaf, combine));
        return combine(x, y);
    }
}
