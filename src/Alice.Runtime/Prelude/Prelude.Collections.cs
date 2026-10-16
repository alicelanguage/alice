using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

public static partial class Prelude
{
    public static int size<T>(this T[] a) => a.Length;
    public static int size<T>(this List<T> a) => a.Count;
    public static int size<K, V>(this Dictionary<K, V> a) where K : notnull => a.Count;
    public static int size<T>(this IEnumerable<T> a) => a.Count();
    public static int length<T>(this T[] a) => a.Length;
    public static int length<T>(this IEnumerable<T> a) => a.Count();
    public static int count<T>(this IEnumerable<T> a) => a.Count();
    public static int count<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Count(f);
    public static bool empty_q<T>(this IEnumerable<T> a) => !a.Any();
    public static bool any_q<T>(this IEnumerable<T> a) => a.Any();
    public static bool any_q<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Any(f);
    public static bool all_q<T>(this IEnumerable<T> a, Func<T, bool> f) => a.All(f);
    public static bool includes_q<T>(this IEnumerable<T> a, T x) => a.Contains(x);
    public static T first<T>(this IEnumerable<T> a) => a.First();
    public static T last<T>(this IEnumerable<T> a) => a.Last();
    public static T? find<T>(this IEnumerable<T> a, Func<T, bool> f) => a.FirstOrDefault(f);
    public static int index<T>(this IList<T> a, T x) => a.IndexOf(x);

    public static void each<T>(this T[] a, Action<T> f) { for (int i = 0; i < a.Length; i++) f(a[i]); }
    public static void each<T>(this IEnumerable<T> a, Action<T> f) { foreach (var x in a) f(x); }
    public static void each<K, V>(this Dictionary<K, V> a, Action<K, V> f) where K : notnull { foreach (var kv in a) f(kv.Key, kv.Value); }
    public static void each_with_index<T>(this IEnumerable<T> a, Action<T, int> f) { int i = 0; foreach (var x in a) f(x, i++); }

    public static R[] map<T, R>(this T[] a, Func<T, R> f)
    {
        var r = new R[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = f(a[i]);
        return r;
    }

    public static R[] map<T, R>(this IEnumerable<T> a, Func<T, R> f) => a.Select(f).ToArray();
    public static R[] map_with_index<T, R>(this IEnumerable<T> a, Func<T, int, R> f) => a.Select(f).ToArray();
    public static T[] select<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Where(f).ToArray();
    public static T[] reject<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Where(x => !f(x)).ToArray();
    public static R reduce<T, R>(this IEnumerable<T> a, R seed, Func<R, T, R> f) => a.Aggregate(seed, f);
    public static T reduce<T>(this IEnumerable<T> a, Func<T, T, T> f) => a.Aggregate(f);
    public static T sum<T>(this T[] a) where T : unmanaged, INumber<T> => Simd.Sum<T>(a);
    public static T sum<T>(this IEnumerable<T> a) where T : INumber<T> { T s = T.Zero; foreach (var x in a) s += x; return s; }
    public static T max<T>(this T[] a) where T : unmanaged, INumber<T> => Simd.Max<T>(a);
    public static T max<T>(this IEnumerable<T> a) => a.Max()!;
    public static T min<T>(this T[] a) where T : unmanaged, INumber<T> => Simd.Min<T>(a);
    public static T min<T>(this IEnumerable<T> a) => a.Min()!;
    public static T max_by<T, K>(this IEnumerable<T> a, Func<T, K> f) => a.MaxBy(f)!;
    public static T min_by<T, K>(this IEnumerable<T> a, Func<T, K> f) => a.MinBy(f)!;
    public static T[] sort<T>(this IEnumerable<T> a) { var r = a.ToArray(); Array.Sort(r); return r; }
    public static T[] sort_by<T, K>(this IEnumerable<T> a, Func<T, K> f) => a.OrderBy(f).ToArray();
    public static T[] reverse<T>(this IEnumerable<T> a) { var r = a.ToArray(); Array.Reverse(r); return r; }
    public static T[] uniq<T>(this IEnumerable<T> a) => a.Distinct().ToArray();
    public static T[] to_a<T>(this IEnumerable<T> a) => a.ToArray();
    public static List<T> to_list<T>(this IEnumerable<T> a) => a.ToList();
    public static T[] take<T>(this IEnumerable<T> a, int n) => a.Take(n).ToArray();
    public static T[] drop<T>(this IEnumerable<T> a, int n) => a.Skip(n).ToArray();
    public static string join<T>(this IEnumerable<T> a, string sep = "") => string.Join(sep, a.Select(x => str(x)));
    public static void push<T>(this List<T> a, T x) => a.Add(x);
    public static T pop<T>(this List<T> a) { var x = a[^1]; a.RemoveAt(a.Count - 1); return x; }
    public static K[] keys<K, V>(this Dictionary<K, V> d) where K : notnull => d.Keys.ToArray();
    public static V[] values<K, V>(this Dictionary<K, V> d) where K : notnull => d.Values.ToArray();
    public static bool has_key_q<K, V>(this Dictionary<K, V> d, K k) where K : notnull => d.ContainsKey(k);
}
