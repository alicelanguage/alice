namespace Alice.Runtime;

/// <summary>
/// Buffers of owned arrays (ADR 0016). The compiler proves an owned local is the only reference to its array, so
/// at reassignment and method exit the array returns here and the next `Array(T).new(n)` of the same length reuses
/// it: a loop that builds a fresh row per iteration stops allocating once warm. Per-thread slots, exact lengths
/// (`.size` stays the requested length), no locks. A buffer that is never released (an exception unwound past
/// the release) is ordinary garbage, so the GC remains the safety net.
/// </summary>
public static class Own<T> where T : unmanaged
{
    /// <summary>Arrays above this many bytes are left to the GC instead of being retained by an idle thread.</summary>
    const long MaxBytes = 64L << 20;

    [ThreadStatic] static T[]? s0, s1, s2, s3;

    /// <summary>A zeroed array of exactly n elements, reused when this thread released one of that length.</summary>
    public static T[] Rent(int n)
    {
        var a = Take(n);
        if (a == null) return new T[n];
        a.AsSpan().Clear();
        return a;
    }

    /// <summary>An array of exactly n elements, every one set to v.</summary>
    public static T[] Rent(int n, T v)
    {
        var a = Take(n) ?? GC.AllocateUninitializedArray<T>(n);
        a.AsSpan().Fill(v);
        return a;
    }

    /// <summary>Returns an array the caller owned; it must not be used afterwards.</summary>
    public static void Release(T[]? a)
    {
        if (a == null || a.Length == 0 || (long)a.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>() > MaxBytes) return;
        if (s0 == null) s0 = a;
        else if (s1 == null) s1 = a;
        else if (s2 == null) s2 = a;
        else if (s3 == null) s3 = a;
        else { s3 = s2; s2 = s1; s1 = s0; s0 = a; }   // keep the most recent four
    }

    static T[]? Take(int n)
    {
        T[]? a;
        if ((a = s0) != null && a.Length == n) { s0 = null; return a; }
        if ((a = s1) != null && a.Length == n) { s1 = null; return a; }
        if ((a = s2) != null && a.Length == n) { s2 = null; return a; }
        if ((a = s3) != null && a.Length == n) { s3 = null; return a; }
        return null;
    }
}
