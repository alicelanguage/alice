using System.Buffers;

namespace Alice.Runtime;

/// <summary>
/// Fork/join merge sort for large arrays of primitives (ADR 0015): halves sort in parallel through
/// <see cref="Par.Fork{A, B}"/> near the root, leaves sort in place on spans, and every merge works in one
/// pooled scratch buffer, so a sort allocates its result and nothing per level. Primitives only: equal
/// elements are indistinguishable, so the parallel result is identical to <see cref="Array.Sort(Array)"/>.
/// </summary>
static class ParSort
{
    /// <summary>Below this a parallel split costs more than it saves.</summary>
    public const int MinLength = 1 << 16;

    const int Leaf = 1 << 14;

    public static void Sort<T>(T[] a)
    {
        var tmp = ArrayPool<T>.Shared.Rent(a.Length);
        try { Rec(a, tmp, 0, a.Length); }
        finally { ArrayPool<T>.Shared.Return(tmp); }
    }

    static void Rec<T>(T[] a, T[] tmp, int lo, int hi)
    {
        int n = hi - lo;
        if (n <= Leaf || !Par.Forkable)
        {
            a.AsSpan(lo, n).Sort();
            return;
        }
        int mid = lo + n / 2;
        Par.Fork(() => { Rec(a, tmp, lo, mid); return 0; }, () => { Rec(a, tmp, mid, hi); return 0; });
        Merge(a, tmp, lo, mid, hi);
    }

    static void Merge<T>(T[] a, T[] tmp, int lo, int mid, int hi)
    {
        var cmp = Comparer<T>.Default;
        if (cmp.Compare(a[mid - 1], a[mid]) <= 0) return;   // halves already in order
        a.AsSpan(lo, hi - lo).CopyTo(tmp.AsSpan(lo));
        int i = lo, j = mid, k = lo;
        while (i < mid && j < hi) a[k++] = cmp.Compare(tmp[j], tmp[i]) < 0 ? tmp[j++] : tmp[i++];   // ties take the left: stable merge
        while (i < mid) a[k++] = tmp[i++];
        while (j < hi) a[k++] = tmp[j++];
    }
}
