namespace Alice.Runtime;

/// <summary>
/// Pooled lists of owned locals (ADR 0016, 0.12.3): a local `List(T)` of numbers that the compiler proves is the only
/// reference to its list is rented here and released at its last use, so a method that builds a scratch list per call
/// keeps the grown backing array instead of reallocating it (and its log2(n) growth steps) every call. Per-thread
/// slots, no locks; a list never released is ordinary garbage.
/// </summary>
public static class OwnList<T> where T : unmanaged
{
    /// <summary>Lists whose backing array exceeds this are left to the GC instead of being kept by an idle thread.</summary>
    const long MaxBytes = 16L << 20;

    [ThreadStatic] static List<T>? s0, s1;

    /// <summary>An empty list (capacity kept from its last use).</summary>
    public static List<T> Rent()
    {
        var l = s0;
        if (l != null) { s0 = s1; s1 = null; return l; }
        return new List<T>();
    }

    /// <summary>An empty list with room for at least <paramref name="capacity"/> elements.</summary>
    public static List<T> Rent(int capacity)
    {
        var l = Rent();
        if (l.Capacity < capacity) l.Capacity = capacity;
        return l;
    }

    /// <summary>Returns a list the caller owned; it must not be used afterwards.</summary>
    public static void Release(List<T>? l)
    {
        if (l == null || (long)l.Capacity * System.Runtime.CompilerServices.Unsafe.SizeOf<T>() > MaxBytes) return;
        l.Clear();   // unmanaged elements: no references to clear, Count drops to 0
        s1 = s0;
        s0 = l;
    }
}
