namespace Alice.Runtime;

/// <summary>Ruby Hash methods on Dictionary. Two-parameter blocks receive (key, value).</summary>
public static partial class Prelude
{
    public static void each_pair<K, V>(this Dictionary<K, V> d, Action<K, V> f) where K : notnull { foreach (var kv in d) f(kv.Key, kv.Value); }
    public static R[] map<K, V, R>(this Dictionary<K, V> d, Func<K, V, R> f) where K : notnull => d.Select(kv => f(kv.Key, kv.Value)).ToArray();
    public static Dictionary<K, V> select<K, V>(this Dictionary<K, V> d, Func<K, V, bool> f) where K : notnull =>
        d.Where(kv => f(kv.Key, kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
    public static Dictionary<K, V> reject<K, V>(this Dictionary<K, V> d, Func<K, V, bool> f) where K : notnull =>
        d.Where(kv => !f(kv.Key, kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
    public static V fetch<K, V>(this Dictionary<K, V> d, K k, V fallback) where K : notnull => d.TryGetValue(k, out var v) ? v : fallback;
    public static V fetch<K, V>(this Dictionary<K, V> d, K k) where K : notnull => d.TryGetValue(k, out var v) ? v : throw new KeyNotFoundException($"key not found: {inspect(k)}");
    public static V? dig<K, V>(this Dictionary<K, V> d, K k) where K : notnull where V : class => d.GetValueOrDefault(k);

    public static Dictionary<K, V> merge<K, V>(this Dictionary<K, V> d, Dictionary<K, V> other) where K : notnull
    {
        var r = new Dictionary<K, V>(d);
        foreach (var kv in other) r[kv.Key] = kv.Value;
        return r;
    }

    public static bool key_q<K, V>(this Dictionary<K, V> d, K k) where K : notnull => d.ContainsKey(k);
    public static bool value_q<K, V>(this Dictionary<K, V> d, V v) where K : notnull => d.ContainsValue(v);
    public static Dictionary<V, K> invert<K, V>(this Dictionary<K, V> d) where K : notnull where V : notnull
    {
        var r = new Dictionary<V, K>();
        foreach (var kv in d) r[kv.Value] = kv.Key;
        return r;
    }

    public static (K, V)[] to_a<K, V>(this Dictionary<K, V> d) where K : notnull => d.Select(kv => (kv.Key, kv.Value)).ToArray();
    public static KeyValuePair<K, V> max_by<K, V, R>(this Dictionary<K, V> d, Func<K, V, R> f) where K : notnull => d.MaxBy(kv => f(kv.Key, kv.Value));
    public static KeyValuePair<K, V> min_by<K, V, R>(this Dictionary<K, V> d, Func<K, V, R> f) where K : notnull => d.MinBy(kv => f(kv.Key, kv.Value));
    public static KeyValuePair<K, V>[] sort_by<K, V, R>(this Dictionary<K, V> d, Func<K, V, R> f) where K : notnull => d.OrderBy(kv => f(kv.Key, kv.Value)).ToArray();
    public static Dictionary<K, R> transform_values<K, V, R>(this Dictionary<K, V> d, Func<V, R> f) where K : notnull => d.ToDictionary(kv => kv.Key, kv => f(kv.Value));
    public static Dictionary<R, V> transform_keys<K, V, R>(this Dictionary<K, V> d, Func<K, R> f) where K : notnull where R : notnull => d.ToDictionary(kv => f(kv.Key), kv => kv.Value);
    public static Dictionary<R, KeyValuePair<K, V>[]> group_by<K, V, R>(this Dictionary<K, V> d, Func<K, V, R> f) where K : notnull where R : notnull =>
        d.GroupBy(kv => f(kv.Key, kv.Value)).ToDictionary(g => g.Key, g => g.ToArray());
    public static R sum<K, V, R>(this Dictionary<K, V> d, Func<K, V, R> f) where K : notnull where R : System.Numerics.INumber<R>
    {
        R s = R.Zero;
        foreach (var kv in d) s += f(kv.Key, kv.Value);
        return s;
    }

    public static int count<K, V>(this Dictionary<K, V> d, Func<K, V, bool> f) where K : notnull => d.Count(kv => f(kv.Key, kv.Value));
    public static bool any_q<K, V>(this Dictionary<K, V> d, Func<K, V, bool> f) where K : notnull => d.Any(kv => f(kv.Key, kv.Value));
    public static bool all_q<K, V>(this Dictionary<K, V> d, Func<K, V, bool> f) where K : notnull => d.All(kv => f(kv.Key, kv.Value));
}
