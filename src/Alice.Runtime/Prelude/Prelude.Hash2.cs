namespace Alice.Runtime;

/// <summary>
/// A Hash with a default for missing keys: Ruby's `Hash.new(0)` / `Hash.new { |h, k| h[k] = ... }`.
/// A plain <see cref="Dictionary{K, V}"/> (the static type stays usable everywhere a Hash is); only reads through this
/// type's indexer see the default, so `h[k] += 1` counts from the default. The default is not stored (Ruby), the block
/// stores only what it assigns.
/// </summary>
public sealed class DefaultHash<K, V> : Dictionary<K, V> where K : notnull
{
    readonly V fallback = default!;
    readonly Func<Dictionary<K, V>, K, V>? block;

    public DefaultHash(V fallback) => this.fallback = fallback;
    public DefaultHash(Func<Dictionary<K, V>, K, V> block) => this.block = block;

    /// <summary>The value for a missing key: the block's result, else the default value.</summary>
    public V Missing(K key) => block != null ? block(this, key) : fallback;

    public new V this[K key]
    {
        get => TryGetValue(key, out var v) ? v : Missing(key);
        set => base[key] = value;
    }
}

/// <summary>Ruby Hash methods beyond the basics (Dictionary extensions). Nil-returning lookups are split by value kind.</summary>
public static partial class Prelude
{
    /// <summary>`Hash(K, V).new(default)`: missing keys read as <paramref name="fallback"/>.</summary>
    public static DefaultHash<K, V> hash_new<K, V>(V fallback) where K : notnull => new(fallback);

    /// <summary>`Hash(K, V).new { |h, k| ... }`: missing keys read as the block's value.</summary>
    public static DefaultHash<K, V> hash_new<K, V>(Func<Dictionary<K, V>, K, V> block) where K : notnull => new(block);

    /// <summary>`h[k]` honoring a <see cref="DefaultHash{K, V}"/> default whatever the static type; KeyNotFound otherwise.</summary>
    public static V hash_get<K, V>(Dictionary<K, V> d, K k) where K : notnull =>
        d.TryGetValue(k, out var v) ? v : d is DefaultHash<K, V> dh ? dh.Missing(k) : throw new KeyNotFoundException($"key not found: {inspect(k)}");

    /// <summary>`fetch(k) { |k| ... }`: the block's value when the key is missing.</summary>
    public static V fetch<K, V>(this Dictionary<K, V> d, K k, Func<K, V> f) where K : notnull => d.TryGetValue(k, out var v) ? v : f(k);
    public static V fetch<K, V>(this Dictionary<K, V> d, K k, Func<V> f) where K : notnull => d.TryGetValue(k, out var v) ? v : f();

    /// <summary>Removes the key; its value, or nil when absent (reference values; value types: see HashValues).</summary>
    public static V? delete<K, V>(this Dictionary<K, V> d, K k) where K : notnull where V : class => d.Remove(k, out var v) ? v : null;
    public static V delete<K, V>(this Dictionary<K, V> d, K k, Func<K, V> f) where K : notnull => d.Remove(k, out var v) ? v : f(k);

    public static void each_key<K, V>(this Dictionary<K, V> d, Action<K> f) where K : notnull { foreach (var k in d.Keys) f(k); }
    public static void each_value<K, V>(this Dictionary<K, V> d, Action<V> f) where K : notnull { foreach (var v in d.Values) f(v); }

    /// <summary>`dig(a, b)` / `dig(a, b, c)` through nested hashes: nil when any key is missing.</summary>
    public static V? dig<K1, K2, V>(this Dictionary<K1, Dictionary<K2, V>> d, K1 a, K2 b) where K1 : notnull where K2 : notnull where V : class =>
        d.TryGetValue(a, out var inner) && inner.TryGetValue(b, out var v) ? v : null;
    public static V? dig<K1, K2, K3, V>(this Dictionary<K1, Dictionary<K2, Dictionary<K3, V>>> d, K1 a, K2 b, K3 c)
        where K1 : notnull where K2 : notnull where K3 : notnull where V : class =>
        d.TryGetValue(a, out var i1) && i1.TryGetValue(b, out var i2) && i2.TryGetValue(c, out var v) ? v : null;

    [Pure] public static bool include_q<K, V>(this Dictionary<K, V> d, K k) where K : notnull => d.ContainsKey(k);
    [Pure] public static bool includes_q<K, V>(this Dictionary<K, V> d, K k) where K : notnull => d.ContainsKey(k);
    [Pure] public static bool member_q<K, V>(this Dictionary<K, V> d, K k) where K : notnull => d.ContainsKey(k);
    [Pure] public static bool has_value_q<K, V>(this Dictionary<K, V> d, V v) where K : notnull => d.ContainsValue(v);
    [Pure] public static int length<K, V>(this Dictionary<K, V> d) where K : notnull => d.Count;
    [Pure] public static bool empty_q<K, V>(this Dictionary<K, V> d) where K : notnull => d.Count == 0;

    /// <summary>The first key whose value equals <paramref name="v"/>, or nil (reference keys; value types: see HashValues).</summary>
    public static K? key<K, V>(this Dictionary<K, V> d, V v) where K : class
    {
        var eq = EqualityComparer<V>.Default;
        foreach (var kv in d) if (eq.Equals(kv.Value, v)) return kv.Key;
        return null;
    }

    /// <summary>`store(k, v)`: `h[k] = v`, returns v.</summary>
    public static V store<K, V>(this Dictionary<K, V> d, K k, V v) where K : notnull { d[k] = v; return v; }

    /// <summary>`update(other)` (Ruby `merge!`): copies other's pairs into the receiver, returns it.</summary>
    public static Dictionary<K, V> update<K, V>(this Dictionary<K, V> d, Dictionary<K, V> other) where K : notnull
    {
        foreach (var kv in other) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>`update(other) { |k, old, new| ... }`: the block resolves keys present in both.</summary>
    public static Dictionary<K, V> update<K, V>(this Dictionary<K, V> d, Dictionary<K, V> other, Func<K, V, V, V> f) where K : notnull
    {
        foreach (var kv in other) d[kv.Key] = d.TryGetValue(kv.Key, out var old) ? f(kv.Key, old, kv.Value) : kv.Value;
        return d;
    }

    /// <summary>`merge(other) { |k, old, new| ... }`: a new hash; the block resolves keys present in both.</summary>
    public static Dictionary<K, V> merge<K, V>(this Dictionary<K, V> d, Dictionary<K, V> other, Func<K, V, V, V> f) where K : notnull =>
        new Dictionary<K, V>(d).update(other, f);

    /// <summary>`filter_map { |k, v| ... }`: the block's non-nil values.</summary>
    public static R[] filter_map<K, V, R>(this Dictionary<K, V> d, Func<K, V, R?> f) where K : notnull where R : class
    {
        var r = new List<R>();
        foreach (var kv in d) if (f(kv.Key, kv.Value) is R x) r.Add(x);
        return r.ToArray();
    }

    /// <summary>`to_h { |k, v| {k2, v2} }`: a new hash from the block's pairs.</summary>
    public static Dictionary<K2, V2> to_h<K, V, K2, V2>(this Dictionary<K, V> d, Func<K, V, (K2, V2)> f) where K : notnull where K2 : notnull
    {
        var r = new Dictionary<K2, V2>(d.Count);
        foreach (var kv in d) { var (k, v) = f(kv.Key, kv.Value); r[k] = v; }
        return r;
    }

    [Pure] public static Dictionary<K, V> to_h<K, V>(this Dictionary<K, V> d) where K : notnull => d;
    [Pure] public static Dictionary<K, V> to_h<K, V>(this IEnumerable<KeyValuePair<K, V>> a) where K : notnull
    {
        var d = new Dictionary<K, V>();
        foreach (var kv in a) d[kv.Key] = kv.Value;
        return d;
    }

    [Pure] public static V[] values_at<K, V>(this Dictionary<K, V> d, params K[] ks) where K : notnull
    {
        var r = new V[ks.Length];
        for (int i = 0; i < ks.Length; i++) r[i] = d[ks[i]];
        return r;
    }

    [Pure] public static Dictionary<K, V> slice<K, V>(this Dictionary<K, V> d, params K[] ks) where K : notnull
    {
        var r = new Dictionary<K, V>();
        foreach (var k in ks) if (d.TryGetValue(k, out var v)) r[k] = v;
        return r;
    }

    [Pure] public static Dictionary<K, V> except<K, V>(this Dictionary<K, V> d, params K[] ks) where K : notnull
    {
        var r = new Dictionary<K, V>(d);
        foreach (var k in ks) r.Remove(k);
        return r;
    }

    public static Dictionary<K, V> filter<K, V>(this Dictionary<K, V> d, Func<K, V, bool> f) where K : notnull => d.select(f);

    /// <summary>`clear`: removes every pair, returns the (now empty) receiver.</summary>
    public static Dictionary<K, V> clear<K, V>(this Dictionary<K, V> d) where K : notnull { d.Clear(); return d; }

    // ---- KeyValuePair as Ruby's [k, v] pair (Crystal's {k, v} tuple) ----

    [Pure] public static K first<K, V>(this KeyValuePair<K, V> kv) => kv.Key;
    [Pure] public static V last<K, V>(this KeyValuePair<K, V> kv) => kv.Value;
    [Pure] public static K key<K, V>(this KeyValuePair<K, V> kv) => kv.Key;
    [Pure] public static V value<K, V>(this KeyValuePair<K, V> kv) => kv.Value;
    [Pure] public static (K, V) to_a<K, V>(this KeyValuePair<K, V> kv) => (kv.Key, kv.Value);
    [Pure] public static (K, V) to_t<K, V>(this KeyValuePair<K, V> kv) => (kv.Key, kv.Value);
}

/// <summary>
/// Hash lookups that return nil for value-type values/keys (`Int32?`). They live outside Prelude because C# cannot
/// overload on constraints in one class; overload resolution drops the candidate whose constraint fails.
/// </summary>
public static class HashValues
{
    /// <summary>`filter_map { |x| ... }` whose block returns a nilable value type (`x.odd? ? x * 10 : nil`).</summary>
    public static R[] filter_map<T, R>(this IEnumerable<T> a, Func<T, R?> f) where R : struct
    {
        var r = new List<R>();
        foreach (var x in a) if (f(x) is R y) r.Add(y);
        return r.ToArray();
    }

    public static V? delete<K, V>(this Dictionary<K, V> d, K k) where K : notnull where V : struct => d.Remove(k, out var v) ? v : null;
    public static V? dig<K, V>(this Dictionary<K, V> d, K k) where K : notnull where V : struct => d.TryGetValue(k, out var v) ? v : null;
    public static V? dig<K1, K2, V>(this Dictionary<K1, Dictionary<K2, V>> d, K1 a, K2 b) where K1 : notnull where K2 : notnull where V : struct =>
        d.TryGetValue(a, out var inner) && inner.TryGetValue(b, out var v) ? v : null;
    public static V? dig<K1, K2, K3, V>(this Dictionary<K1, Dictionary<K2, Dictionary<K3, V>>> d, K1 a, K2 b, K3 c)
        where K1 : notnull where K2 : notnull where K3 : notnull where V : struct =>
        d.TryGetValue(a, out var i1) && i1.TryGetValue(b, out var i2) && i2.TryGetValue(c, out var v) ? v : null;

    public static K? key<K, V>(this Dictionary<K, V> d, V v) where K : struct
    {
        var eq = EqualityComparer<V>.Default;
        foreach (var kv in d) if (eq.Equals(kv.Value, v)) return kv.Key;
        return null;
    }

    public static R[] filter_map<K, V, R>(this Dictionary<K, V> d, Func<K, V, R?> f) where K : notnull where R : struct
    {
        var r = new List<R>();
        foreach (var kv in d) if (f(kv.Key, kv.Value) is R x) r.Add(x);
        return r.ToArray();
    }
}
