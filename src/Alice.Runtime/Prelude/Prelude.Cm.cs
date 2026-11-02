using System.Numerics;

namespace Alice.Runtime;

/// <summary>
/// Connection-Machine primitives as Ruby-style array methods (ADR 0039, kernels in <see cref="Cm"/>). Each returns a
/// new array except <c>scatter_add</c>, which updates its target. Integer results are exact; float results use a
/// chunking fixed by the length, so they are bit-identical for any worker count but may differ from a serial loop in
/// the last bits. Blocks run on several threads at once: they must be pure; <c>scan</c>'s block must be associative.
/// </summary>
public static partial class Prelude
{
    /// <summary>Inclusive prefix sum: <c>[1, 2, 3].prefix_sum == [1, 3, 6]</c>.</summary>
    [Pure]
    public static T[] prefix_sum<T>(this T[] xs) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        T[] r = xs.Length == 0 ? [] : GC.AllocateUninitializedArray<T>(xs.Length);
        Cm.InclusiveScan<T>(xs, r);
        return r;
    }

    /// <summary>Inclusive scan with an associative block: <c>xs.scan { |a, b| a.max(b) }</c>. One block call per
    /// element and no SIMD: slower than <c>prefix_sum</c> (use it for sums).</summary>
    public static T[] scan<T>(this T[] xs, Func<T, T, T> f)
    {
        T[] r = xs.Length == 0 ? [] : new T[xs.Length];
        Cm.InclusiveScan(xs, f, r);
        return r;
    }

    /// <summary>One sum per segment; <c>flags[i]</c> true starts a segment at i (index 0 always does):
    /// <c>[1, 2, 3, 4].segmented_sum([true, false, true, false]) == [3, 7]</c>.</summary>
    [Pure]
    public static T[] segmented_sum<T>(this T[] xs, bool[] flags) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        if (flags.Length != xs.Length) throw new ArgumentException("segment flags and values differ in length");
        var r = new T[Cm.SegmentCount(flags)];
        Cm.SegmentedReduce<T>(xs, flags, r);
        return r;
    }

    /// <summary>Prefix sum restarted at every flag: <c>[1, 2, 3, 4].segmented_prefix_sum([true, false, true, false]) == [1, 3, 3, 7]</c>.</summary>
    [Pure]
    public static T[] segmented_prefix_sum<T>(this T[] xs, bool[] flags) where T : unmanaged, IBinaryNumber<T>, IMinMaxValue<T>
    {
        T[] r = xs.Length == 0 ? [] : GC.AllocateUninitializedArray<T>(xs.Length);
        Cm.SegmentedScan<T>(xs, flags, r);
        return r;
    }

    /// <summary>Counts per key in [0, k): <c>[0, 2, 2].histogram(3) == [1, 0, 2]</c>. A key outside raises.</summary>
    [Pure]
    public static int[] histogram(this int[] keys, int k)
    {
        var bins = new int[k];
        Cm.Histogram<int>(keys, bins);
        return bins;
    }

    /// <inheritdoc cref="histogram(int[], int)"/>
    [Pure]
    public static int[] histogram(this uint[] keys, int k)
    {
        var bins = new int[k];
        Cm.Histogram<int>(keys, bins);
        return bins;
    }

    /// <summary>The elements for which the block holds, in order (a parallel <c>select</c>): <c>xs.compact { |x| x &gt; 0 }</c>.</summary>
    public static T[] compact<T>(this T[] xs, Func<T, bool> keep) => Cm.Compact(xs, keep);

    /// <summary><c>target[idx[i]] += vals[i]</c> for every i, in parallel; returns <paramref name="target"/>. Also callable
    /// as <c>scatter_add(target, idx, vals)</c>.</summary>
    public static T[] scatter_add<T>(this T[] target, int[] idx, T[] vals) where T : unmanaged, INumber<T>
    {
        Cm.ScatterAdd<T>(target, idx, vals);
        return target;
    }

    /// <summary><c>out[perm[i]] = xs[i]</c>; <paramref name="perm"/> must be a permutation of 0...xs.size (checked).</summary>
    [Pure]
    public static T[] permute<T>(this T[] xs, int[] perm)
    {
        T[] r = xs.Length == 0 ? [] : new T[xs.Length];
        Cm.Permute(xs, perm, r);
        return r;
    }

    /// <summary>Stable radix sort by key: <c>keys.sort_by_key(vals)</c> returns the sorted keys and the values in the
    /// same order (both new arrays). Values of any type: the keys are sorted with their indices, then the values gathered.</summary>
    [Pure]
    public static (uint[] keys, V[] values) sort_by_key<V>(this uint[] keys, V[] vals)
    {
        if (keys.Length != vals.Length) throw new ArgumentException("sort_by_key keys and values differ in length");
        var k = (uint[])keys.Clone();
        return (k, SortGather(k, vals));
    }

    /// <inheritdoc cref="sort_by_key{V}(uint[], V[])"/>
    [Pure]
    public static (int[] keys, V[] values) sort_by_key<V>(this int[] keys, V[] vals)
    {
        if (keys.Length != vals.Length) throw new ArgumentException("sort_by_key keys and values differ in length");
        var k = new uint[keys.Length];
        for (int i = 0; i < k.Length; i++) k[i] = (uint)keys[i] ^ 0x8000_0000u;   // order-preserving for signed keys
        var v = SortGather(k, vals);
        var r = new int[k.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (int)(k[i] ^ 0x8000_0000u);
        return (r, v);
    }

    static V[] SortGather<V>(uint[] k, V[] vals)
    {
        var order = new int[k.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Cm.SortByKey<int>(k, order);
        var v = new V[vals.Length];
        for (int i = 0; i < v.Length; i++) v[i] = vals[order[i]];
        return v;
    }
}
