namespace Alice.Runtime;

/// <summary>
/// Ruby slicing, the lowering of `xs[a..b]`, `xs[a...b]`, `xs[a..]`, `xs[..b]` and `xs[start, length]` on arrays, lists
/// and strings: negative bounds count from the end; a start past the end gives an empty result (Ruby: nil).
/// </summary>
public static partial class Prelude
{
    /// <summary>(start, length) of `a..b` / `a...b` over `size` items.</summary>
    static (int Start, int Length) RangeBounds(int size, int a, int b, bool excl)
    {
        if (a < 0) a += size;
        if (b < 0) b += size;
        if (a < 0 || a > size) return (0, 0);
        int end = Math.Min(excl ? b : b + 1, size);
        return (a, Math.Max(0, end - a));
    }

    /// <summary>(start, length) of `[start, n]` over `size` items.</summary>
    static (int Start, int Length) LengthBounds(int size, int start, int n)
    {
        if (start < 0) start += size;
        if (start < 0 || start > size || n < 0) return (0, 0);
        return (start, Math.Min(n, size - start));
    }

    [Pure] public static T[] slice_range<T>(T[] a, int from, int to, bool excl) { var (s, n) = RangeBounds(a.Length, from, to, excl); return a.AsSpan(s, n).ToArray(); }
    [Pure] public static T[] slice_range<T>(Span<T> a, int from, int to, bool excl) { var (s, n) = RangeBounds(a.Length, from, to, excl); return a.Slice(s, n).ToArray(); }
    [Pure] public static T[] slice_range<T>(ReadOnlySpan<T> a, int from, int to, bool excl) { var (s, n) = RangeBounds(a.Length, from, to, excl); return a.Slice(s, n).ToArray(); }
    [Pure] public static List<T> slice_range<T>(List<T> a, int from, int to, bool excl) { var (s, n) = RangeBounds(a.Count, from, to, excl); return a.GetRange(s, n); }
    [Pure] public static string slice_range(string a, int from, int to, bool excl) { var (s, n) = RangeBounds(a.Length, from, to, excl); return a.Substring(s, n); }

    [Pure] public static T[] slice_len<T>(T[] a, int start, int n) { var (s, k) = LengthBounds(a.Length, start, n); return a.AsSpan(s, k).ToArray(); }
    [Pure] public static T[] slice_len<T>(Span<T> a, int start, int n) { var (s, k) = LengthBounds(a.Length, start, n); return a.Slice(s, k).ToArray(); }
    [Pure] public static T[] slice_len<T>(ReadOnlySpan<T> a, int start, int n) { var (s, k) = LengthBounds(a.Length, start, n); return a.Slice(s, k).ToArray(); }
    [Pure] public static List<T> slice_len<T>(List<T> a, int start, int n) { var (s, k) = LengthBounds(a.Count, start, n); return a.GetRange(s, k); }
    [Pure] public static string slice_len(string a, int start, int n) { var (s, k) = LengthBounds(a.Length, start, n); return a.Substring(s, k); }
}
