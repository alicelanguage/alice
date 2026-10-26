using System.Numerics;
using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>
/// Array/List overloads that walk a span instead of the IEnumerable fallbacks: no boxed enumerator, no LINQ.
/// Semantics match the LINQ versions they shadow (first extreme wins, Comparer.Default ordering, empty throws for value types).
/// </summary>
public static partial class Prelude
{
    public static T sum<T>(this List<T> a) where T : INumber<T>
    {
        T s = T.Zero;
        foreach (var x in CollectionsMarshal.AsSpan(a)) s += x;
        return s;
    }

    public static T max_by<T, K>(this T[] a, Func<T, K> f) => ExtremeBy(a, f, 1);
    public static T min_by<T, K>(this T[] a, Func<T, K> f) => ExtremeBy(a, f, -1);
    public static T max_by<T, K>(this List<T> a, Func<T, K> f) => ExtremeBy(CollectionsMarshal.AsSpan(a), f, 1);
    public static T min_by<T, K>(this List<T> a, Func<T, K> f) => ExtremeBy(CollectionsMarshal.AsSpan(a), f, -1);

    /// <summary>sign 1: the first element with the greatest key; sign -1: the first with the least.</summary>
    static T ExtremeBy<T, K>(ReadOnlySpan<T> a, Func<T, K> f, int sign)
    {
        if (a.Length == 0)
        {
            if (default(T) is null) return default!;   // LINQ MaxBy/MinBy on an empty reference sequence
            throw new InvalidOperationException("Sequence contains no elements");
        }
        var cmp = Comparer<K>.Default;
        T best = a[0];
        K bk = f(best);
        for (int i = 1; i < a.Length; i++)
        {
            K k = f(a[i]);
            int c = cmp.Compare(k, bk);
            if (sign > 0 ? c > 0 : c < 0) { best = a[i]; bk = k; }
        }
        return best;
    }
}
