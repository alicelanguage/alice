using System.Numerics;

namespace Alice.Runtime;

/// <summary>Sparse vector with sorted indices.</summary>
public sealed class SparseVector<T> where T : unmanaged, INumber<T>
{
    readonly int[] _idx;
    readonly T[] _vals;

    public int Length { get; }
    public int NonZeros => _vals.Length;
    public ReadOnlySpan<int> Indices => _idx;
    public ReadOnlySpan<T> Values => _vals;

    SparseVector(int length, int[] idx, T[] vals) { Length = length; _idx = idx; _vals = vals; }

    public static SparseVector<T> FromDense(ReadOnlySpan<T> d)
    {
        var ix = new List<int>(); var vs = new List<T>();
        for (int i = 0; i < d.Length; i++) if (!T.IsZero(d[i])) { ix.Add(i); vs.Add(d[i]); }
        return new(d.Length, [.. ix], [.. vs]);
    }

    public T Dot(ReadOnlySpan<T> dense)
    {
        Simd.SameLength(dense.Length, Length);
        T s = T.Zero;
        for (int k = 0; k < _idx.Length; k++) s += _vals[k] * dense[_idx[k]];
        return s;
    }

    /// <summary>Sorted-merge intersection: O(nnz(a)+nnz(b)).</summary>
    public T Dot(SparseVector<T> o)
    {
        Simd.SameLength(o.Length, Length);
        T s = T.Zero; int i = 0, j = 0;
        while (i < _idx.Length && j < o._idx.Length)
        {
            if (_idx[i] == o._idx[j]) s += _vals[i++] * o._vals[j++];
            else if (_idx[i] < o._idx[j]) i++;
            else j++;
        }
        return s;
    }
}
