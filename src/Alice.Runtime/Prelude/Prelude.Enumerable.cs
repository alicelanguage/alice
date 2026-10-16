namespace Alice.Runtime;

/// <summary>Ruby Enumerable/Array methods. Results are fresh arrays (Ruby arrays are values here).</summary>
public static partial class Prelude
{
    public static void each_slice<T>(this IEnumerable<T> a, int n, Action<T[]> f) { foreach (var c in a.Chunk(n)) f(c); }
    public static T[][] each_slice<T>(this IEnumerable<T> a, int n) => a.Chunk(n).ToArray();

    public static void each_cons<T>(this IEnumerable<T> a, int n, Action<T[]> f) { foreach (var w in a.each_cons(n)) f(w); }
    public static T[][] each_cons<T>(this IEnumerable<T> a, int n)
    {
        var xs = a.ToArray();
        var r = new T[Math.Max(0, xs.Length - n + 1)][];
        for (int i = 0; i < r.Length; i++) r[i] = xs[i..(i + n)];
        return r;
    }

    public static (T, U)[] zip<T, U>(this IEnumerable<T> a, IEnumerable<U> b) => a.Zip(b).ToArray();
    public static T[] flatten<T>(this IEnumerable<IEnumerable<T>> a) => a.SelectMany(x => x).ToArray();
    public static T[] compact<T>(this IEnumerable<T?> a) where T : class => a.Where(x => x != null).Select(x => x!).ToArray();
    public static T[] compact<T>(this IEnumerable<T?> a) where T : struct => a.Where(x => x.HasValue).Select(x => x!.Value).ToArray();

    public static Dictionary<K, T[]> group_by<T, K>(this IEnumerable<T> a, Func<T, K> f) where K : notnull =>
        a.GroupBy(f).ToDictionary(g => g.Key, g => g.ToArray());

    public static T[][] partition<T>(this IEnumerable<T> a, Func<T, bool> f)
    {
        var yes = new List<T>();
        var no = new List<T>();
        foreach (var x in a) (f(x) ? yes : no).Add(x);
        return new[] { yes.ToArray(), no.ToArray() };
    }

    public static Dictionary<T, int> tally<T>(this IEnumerable<T> a) where T : notnull
    {
        var d = new Dictionary<T, int>();
        foreach (var x in a) d[x] = d.GetValueOrDefault(x) + 1;
        return d;
    }

    public static T[][] chunk_while<T>(this IEnumerable<T> a, Func<T, T, bool> f)
    {
        var r = new List<T[]>();
        var cur = new List<T>();
        foreach (var x in a)
        {
            if (cur.Count > 0 && !f(cur[^1], x)) { r.Add(cur.ToArray()); cur.Clear(); }
            cur.Add(x);
        }
        if (cur.Count > 0) r.Add(cur.ToArray());
        return r.ToArray();
    }

    public static R[] flat_map<T, R>(this IEnumerable<T> a, Func<T, IEnumerable<R>> f) => a.SelectMany(f).ToArray();
    public static T[] minmax<T>(this IEnumerable<T> a) { var xs = a.ToArray(); return new[] { xs.Min()!, xs.Max()! }; }

    public static T[][] product<T>(this IEnumerable<T> a, IEnumerable<T> b)
    {
        var bs = b.ToArray();
        return a.SelectMany(x => bs.Select(y => new[] { x, y })).ToArray();
    }

    public static T[] shuffle<T>(this IEnumerable<T> a, Random? rng = null) { var r = a.ToArray(); (rng ?? Random.Shared).Shuffle(r); return r; }
    public static T sample<T>(this IEnumerable<T> a, Random? rng = null) { var xs = a as IList<T> ?? a.ToArray(); return xs[(rng ?? Random.Shared).Next(xs.Count)]; }

    public static T[] rotate<T>(this IEnumerable<T> a, int n = 1)
    {
        var xs = a.ToArray();
        if (xs.Length == 0) return xs;
        int k = ((n % xs.Length) + xs.Length) % xs.Length;
        return xs[k..].Concat(xs[..k]).ToArray();
    }

    public static T[] first<T>(this IEnumerable<T> a, int n) => a.Take(n).ToArray();
    public static T[] last<T>(this IEnumerable<T> a, int n) => a.TakeLast(n).ToArray();
    public static T[] take_while<T>(this IEnumerable<T> a, Func<T, bool> f) => a.TakeWhile(f).ToArray();
    public static T[] drop_while<T>(this IEnumerable<T> a, Func<T, bool> f) => a.SkipWhile(f).ToArray();
    public static R inject<T, R>(this IEnumerable<T> a, R seed, Func<R, T, R> f) => a.Aggregate(seed, f);
    public static T inject<T>(this IEnumerable<T> a, Func<T, T, T> f) => a.Aggregate(f);
    public static M each_with_object<T, M>(this IEnumerable<T> a, M memo, Action<T, M> f) { foreach (var x in a) f(x, memo); return memo; }

    /// <summary>Every n-th element starting with the first: `(1..10).step(3)` -> 1, 4, 7, 10.</summary>
    public static T[] step<T>(this IEnumerable<T> a, int n) => a.Where((_, i) => i % n == 0).ToArray();

    public static int find_index<T>(this IEnumerable<T> a, Func<T, bool> f) { int i = 0; foreach (var x in a) { if (f(x)) return i; i++; } return -1; }
    public static T[] uniq<T, K>(this IEnumerable<T> a, Func<T, K> f) => a.DistinctBy(f).ToArray();
    public static T[] sort<T>(this IEnumerable<T> a, Func<T, T, int> cmp) { var r = a.ToArray(); Array.Sort(r, Comparer<T>.Create((x, y) => cmp(x, y))); return r; }
    public static void reverse_each<T>(this IEnumerable<T> a, Action<T> f) { var xs = a.ToArray(); for (int i = xs.Length - 1; i >= 0; i--) f(xs[i]); }
    public static void cycle<T>(this IEnumerable<T> a, int n, Action<T> f) { var xs = a.ToArray(); for (int k = 0; k < n; k++) foreach (var x in xs) f(x); }

    public static T[][] combination<T>(this IEnumerable<T> a, int k)
    {
        var xs = a.ToArray();
        var r = new List<T[]>();
        var cur = new T[k];
        void Go(int start, int depth)
        {
            if (depth == k) { r.Add((T[])cur.Clone()); return; }
            for (int i = start; i < xs.Length; i++) { cur[depth] = xs[i]; Go(i + 1, depth + 1); }
        }
        if (k >= 0 && k <= xs.Length) Go(0, 0);
        return r.ToArray();
    }

    public static T[][] permutation<T>(this IEnumerable<T> a, int k)
    {
        var xs = a.ToArray();
        var r = new List<T[]>();
        var cur = new T[k];
        var used = new bool[xs.Length];
        void Go(int depth)
        {
            if (depth == k) { r.Add((T[])cur.Clone()); return; }
            for (int i = 0; i < xs.Length; i++)
            {
                if (used[i]) continue;
                used[i] = true; cur[depth] = xs[i]; Go(depth + 1); used[i] = false;
            }
        }
        if (k >= 0 && k <= xs.Length) Go(0);
        return r.ToArray();
    }

    public static Dictionary<K, V> to_h<K, V>(this IEnumerable<(K, V)> a) where K : notnull
    {
        var d = new Dictionary<K, V>();
        foreach (var (k, v) in a) d[k] = v;
        return d;
    }

    public static bool include_q<T>(this IEnumerable<T> a, T x) => a.Contains(x);
    public static bool include_q(this string s, string x) => s.Contains(x, StringComparison.Ordinal);
    public static bool cover_q<T>(this IEnumerable<T> a, T x) where T : IComparable<T> =>
        a.Any() && a.First().CompareTo(x) <= 0 && a.Last().CompareTo(x) >= 0;
}
