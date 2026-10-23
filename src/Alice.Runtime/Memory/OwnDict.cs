namespace Alice.Runtime;

/// <summary>
/// Pooled dictionaries of owned locals (ADR 0016, 0.12.3): a local `Dictionary(K, V)` of numbers that the compiler
/// proves is the only reference to it is rented here and released at its last use; `Clear` keeps the buckets, so a
/// per-call counting or memo table stops allocating once warm. Per-thread slots, no locks.
/// </summary>
public static class OwnDict<TKey, TValue> where TKey : unmanaged where TValue : unmanaged
{
    /// <summary>Tables with more slots than this are left to the GC instead of being kept by an idle thread.</summary>
    const int MaxCapacity = 1 << 20;

    [ThreadStatic] static Dictionary<TKey, TValue>? s0, s1;

    public static Dictionary<TKey, TValue> Rent()
    {
        var d = s0;
        if (d != null) { s0 = s1; s1 = null; return d; }
        return new Dictionary<TKey, TValue>();
    }

    public static Dictionary<TKey, TValue> Rent(int capacity)
    {
        var d = Rent();
        d.EnsureCapacity(capacity);
        return d;
    }

    /// <summary>Returns a dictionary the caller owned; it must not be used afterwards.</summary>
    public static void Release(Dictionary<TKey, TValue>? d)
    {
        if (d == null || d.Comparer != EqualityComparer<TKey>.Default || d.EnsureCapacity(0) > MaxCapacity) return;
        d.Clear();
        s1 = s0;
        s0 = d;
    }
}
