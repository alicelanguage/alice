using System.Collections;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Alice.Runtime;

/// <summary>Ruby Array/Enumerable methods beyond the basics, Range of strings/chars/Comparable, copies.</summary>
public static partial class Prelude
{
    /// <summary>Typed nesting (`Array(Array(Int32))`): one level is all there is, so any depth >= 1 is `flatten`.</summary>
    [Pure]
    public static T[] flatten<T>(this IEnumerable<IEnumerable<T>> a, int depth) =>
        depth != 0 ? a.SelectMany(x => x).ToArray() : throw new ArgumentOutOfRangeException(nameof(depth), "flatten(0) of a typed nested array: use dup");

    /// <summary>Mixed nesting (`[1, [2, [3]]]`, an Array(Object)): flattens <paramref name="depth"/> levels, all when negative.
    /// Strings are values, not arrays.</summary>
    [Pure]
    public static object?[] flatten(this IEnumerable<object?> a, int depth = -1)
    {
        var r = new List<object?>();
        void Go(IEnumerable xs, int d)
        {
            foreach (var x in xs)
                if (d != 0 && x is IEnumerable inner and not string && x is not IDictionary) Go(inner, d - 1);
                else r.Add(x);
        }
        Go(a, depth);
        return r.ToArray();
    }

    /// <summary>`max(n)` / `min(n)`: the n largest (descending) / smallest (ascending) elements.</summary>
    [Pure] public static T[] max<T>(this IEnumerable<T> a, int n) => a.OrderByDescending(x => x).Take(n).ToArray();
    [Pure] public static T[] min<T>(this IEnumerable<T> a, int n) => a.OrderBy(x => x).Take(n).ToArray();
    public static T[] max_by<T, K>(this IEnumerable<T> a, int n, Func<T, K> f) => a.OrderByDescending(f).Take(n).ToArray();
    public static T[] min_by<T, K>(this IEnumerable<T> a, int n, Func<T, K> f) => a.OrderBy(f).Take(n).ToArray();

    /// <summary>`slice_when { |a, b| ... }`: splits between neighbours where the block is true (inverse of chunk_while).</summary>
    public static T[][] slice_when<T>(this IEnumerable<T> a, Func<T, T, bool> f) => a.chunk_while((x, y) => !f(x, y));

    [Pure] public static HashSet<T> to_set<T>(this IEnumerable<T> a) => new(a);

    /// <summary>`grep(/re/)` / `grep_v(/re/)`: the strings that match / do not match. (Type and range filters: `select`.)</summary>
    [Pure] public static string[] grep(this IEnumerable<string> a, Alice.Core.Text.Regex re) => a.Where(s => re.IsMatch(s)).ToArray();
    [Pure] public static string[] grep_v(this IEnumerable<string> a, Alice.Core.Text.Regex re) => a.Where(s => !re.IsMatch(s)).ToArray();
    public static R[] grep<R>(this IEnumerable<string> a, Alice.Core.Text.Regex re, Func<string, R> f) => a.Where(s => re.IsMatch(s)).Select(f).ToArray();
    [Pure] public static string[] grep(this IEnumerable<string> a, Regex re) => a.Where(s => re.IsMatch(s)).ToArray();
    [Pure] public static string[] grep_v(this IEnumerable<string> a, Regex re) => a.Where(s => !re.IsMatch(s)).ToArray();
    public static R[] grep<R>(this IEnumerable<string> a, Regex re, Func<string, R> f) => a.Where(s => re.IsMatch(s)).Select(f).ToArray();

    /// <summary>`find_index(value)` / `index(value)`: first position of an equal element, or -1.</summary>
    [Pure]
    public static int find_index<T>(this IEnumerable<T> a, T x)
    {
        var eq = EqualityComparer<T>.Default;
        int i = 0;
        foreach (var y in a) { if (eq.Equals(x, y)) return i; i++; }
        return -1;
    }

    /// <summary>`transpose`: rows become columns; rows must have equal length (IndexError otherwise, as in Ruby).</summary>
    [Pure]
    public static T[][] transpose<T>(this IEnumerable<IEnumerable<T>> a)
    {
        var rows = a.Select(r => r.ToArray()).ToArray();
        if (rows.Length == 0) return [];
        int w = rows[0].Length;
        var r = new T[w][];
        for (int j = 0; j < w; j++)
        {
            r[j] = new T[rows.Length];
            for (int i = 0; i < rows.Length; i++)
                r[j][i] = rows[i].Length == w ? rows[i][j] : throw new IndexOutOfRangeException($"element size differs ({rows[i].Length} should be {w})");
        }
        return r;
    }

    /// <summary>`[[k, v], ...].to_h`: pairs given as two-element arrays.</summary>
    [Pure]
    public static Dictionary<T, T> to_h<T>(this IEnumerable<T[]> a) where T : notnull
    {
        var d = new Dictionary<T, T>();
        foreach (var p in a) d[p.Length == 2 ? p[0] : throw new ArgumentException($"wrong array length (expected 2, was {p.Length})")] = p[1];
        return d;
    }

    /// <summary>`to_h { |x| {k, v} }`: a hash from the block's pairs.</summary>
    public static Dictionary<K, V> to_h<T, K, V>(this IEnumerable<T> a, Func<T, (K, V)> f) where K : notnull
    {
        var d = new Dictionary<K, V>();
        foreach (var x in a) { var (k, v) = f(x); d[k] = v; }
        return d;
    }

    /// <summary>`sum(init)`: the sum starting from init (`[0.5, 1.5].sum(0.0)`, `[] of Int32 .sum(10)`).</summary>
    [Pure] public static T sum<T>(this IEnumerable<T> a, T init) where T : INumber<T> { T s = init; foreach (var x in a) s += x; return s; }

    /// <summary>`filter_map { |x| ... }`: the block's non-nil values (reference results; value types: see HashValues).</summary>
    public static R[] filter_map<T, R>(this IEnumerable<T> a, Func<T, R?> f) where R : class
    {
        var r = new List<R>();
        foreach (var x in a) if (f(x) is R y) r.Add(y);
        return r.ToArray();
    }

    /// <summary>`[1, 2] * 2` (repeat) and `[1, 2] * ","` (join): the operator's runtime half.</summary>
    [Pure]
    public static T[] arr_mul<T>(IEnumerable<T> a, int n)
    {
        if (n < 0) throw new ArgumentException("negative argument");
        var xs = a.ToArray();
        var r = new T[xs.Length * n];
        for (int k = 0; k < n; k++) xs.CopyTo(r, k * xs.Length);
        return r;
    }

    [Pure] public static string arr_mul<T>(IEnumerable<T> a, string sep) => a.join(sep);

    // ---- shallow copies (Ruby dup/clone): a new container, the same elements ----

    [Pure] public static T[] dup<T>(this T[] a) => (T[])a.Clone();
    [Pure] public static T[] clone<T>(this T[] a) => (T[])a.Clone();
    [Pure] public static List<T> dup<T>(this List<T> a) => new(a);
    [Pure] public static List<T> clone<T>(this List<T> a) => new(a);
    [Pure] public static HashSet<T> dup<T>(this HashSet<T> a) => new(a, a.Comparer);
    [Pure] public static HashSet<T> clone<T>(this HashSet<T> a) => new(a, a.Comparer);
    [Pure] public static Dictionary<K, V> dup<K, V>(this Dictionary<K, V> d) where K : notnull => new(d, d.Comparer);
    [Pure] public static Dictionary<K, V> clone<K, V>(this Dictionary<K, V> d) where K : notnull => new(d, d.Comparer);

    /// <summary>`eql?` on arrays: same length and element-wise equal (value equality, unlike `equal?`).</summary>
    [Pure] public static bool eql_q<T>(this T[] a, T[]? b) => b != null && a.AsSpan().SequenceEqual(b, EqualityComparer<T>.Default);
    [Pure] public static bool eql_q<T>(this List<T> a, List<T>? b) => b != null && a.SequenceEqual(b);
    [Pure] public static int hash<T>(this T[] a) { var h = new HashCode(); foreach (var x in a) h.Add(x); return h.ToHashCode(); }

    // ---- Range: strings and chars iterate with succ; other Comparables test membership ----

    /// <summary>`("a".."e")`: succ from a until b (or until longer than b, as in Ruby).</summary>
    public static IEnumerable<string> range(string a, string b, bool exclusive)
    {
        if (a.Length > b.Length || (a.Length == b.Length && string.CompareOrdinal(a, b) > 0)) yield break;
        for (var s = a; s.Length <= b.Length; s = s.succ())
        {
            if (s == b) { if (!exclusive) yield return s; yield break; }
            yield return s;
        }
    }

    public static IEnumerable<char> range(char a, char b, bool exclusive)
    {
        int end = exclusive ? b : b + 1;
        for (int c = a; c < end; c++) yield return (char)c;
    }

    /// <summary>`(1.0..2.0)`, `(d1..d2)`: a Range of any Comparable; iterating needs succ (Ruby TypeError), membership does not.</summary>
    public static ComparableRange<T> range<T>(T a, T b, bool exclusive) where T : IComparable<T> => new(a, b, exclusive);

    [Pure] public static bool include_q<T>(this ComparableRange<T> r, T x) where T : IComparable<T> => r.Covers(x);
    [Pure] public static bool includes_q<T>(this ComparableRange<T> r, T x) where T : IComparable<T> => r.Covers(x);
    [Pure] public static bool cover_q<T>(this ComparableRange<T> r, T x) where T : IComparable<T> => r.Covers(x);

    /// <summary>`(1.0..2.0).step(0.5)`: begin, begin + by, ... up to end (n * by, no accumulated error).</summary>
    [Pure]
    public static double[] step(this ComparableRange<double> r, double by)
    {
        if (by <= 0) throw new ArgumentException("step must be positive");
        var xs = new List<double>();
        for (long i = 0; ; i++)
        {
            double v = r.Begin + i * by;
            if (v > r.End || (r.Exclusive && v >= r.End)) break;
            xs.Add(v);
        }
        return xs.ToArray();
    }

    /// <summary>`reverse_each` without a block: the elements last to first.</summary>
    [Pure] public static IEnumerable<T> reverse_each<T>(this IEnumerable<T> a) => a.Reverse();

    /// <summary>`1.step(10, 3)` without a block: the counted sequence.</summary>
    [Pure]
    public static IEnumerable<int> step(this int a, int limit, int by)
    {
        if (by == 0) throw new ArgumentException("step can't be 0");
        return Seq(a, limit, by);
        static IEnumerable<int> Seq(int a, int limit, int by) { for (int i = a; by > 0 ? i <= limit : i >= limit; i += by) yield return i; }
    }

    [Pure]
    public static IEnumerable<double> step(this double a, double limit, double by)
    {
        if (by == 0) throw new ArgumentException("step can't be 0");
        return Seq(a, limit, by);
        static IEnumerable<double> Seq(double a, double limit, double by)
        {
            long n = (long)Math.Floor((limit - a) / by + 1e-9);
            for (long i = 0; i <= n; i++) yield return a + i * by;
        }
    }
}

/// <summary>A Range over a Comparable without succ (Float64, DateTime, ...): bounds and membership.</summary>
public sealed class ComparableRange<T> : IEnumerable<T> where T : IComparable<T>
{
    public ComparableRange(T begin, T end, bool exclusive) { Begin = begin; End = end; Exclusive = exclusive; }

    public T Begin { get; }
    public T End { get; }
    public bool Exclusive { get; }

    public bool Covers(T x) => Begin.CompareTo(x) <= 0 && (Exclusive ? x.CompareTo(End) < 0 : x.CompareTo(End) <= 0);

    public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException($"can't iterate from {typeof(T).Name}");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => Prelude.inspect(Begin) + (Exclusive ? "..." : "..") + Prelude.inspect(End);
}
