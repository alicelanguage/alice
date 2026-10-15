using System.Numerics;

namespace Alice.Runtime;

/// <summary>Compressed Sparse Row matrix. Immutable; SpMV is cache-linear and optionally row-parallel.</summary>
public sealed class CsrMatrix<T> where T : unmanaged, INumber<T>
{
    readonly int[] _rowPtr, _cols;
    readonly T[] _vals;

    public int Rows { get; }
    public int Columns { get; }
    public int NonZeros => _vals.Length;
    public ReadOnlySpan<int> RowPointers => _rowPtr;
    public ReadOnlySpan<int> ColumnIndices => _cols;
    public ReadOnlySpan<T> Values => _vals;

    CsrMatrix(int rows, int cols, int[] rowPtr, int[] colIdx, T[] vals)
    {
        Rows = rows; Columns = cols; _rowPtr = rowPtr; _cols = colIdx; _vals = vals;
    }

    public static CsrMatrix<T> FromDense(T[,] d)
    {
        int r = d.GetLength(0), c = d.GetLength(1);
        var rp = new int[r + 1];
        var ci = new List<int>(); var vs = new List<T>();
        for (int i = 0; i < r; i++)
        {
            for (int j = 0; j < c; j++)
                if (!T.IsZero(d[i, j])) { ci.Add(j); vs.Add(d[i, j]); }
            rp[i + 1] = vs.Count;
        }
        return new(r, c, rp, [.. ci], [.. vs]);
    }

    /// <summary>Builds from (row, col, value) entries in any order; duplicates are summed, zeros dropped.</summary>
    public static CsrMatrix<T> FromTriplets(int rows, int cols, ReadOnlySpan<(int Row, int Col, T Value)> entries)
    {
        var sorted = entries.ToArray();
        foreach (var e in sorted)
            if ((uint)e.Row >= (uint)rows || (uint)e.Col >= (uint)cols)
                throw new ArgumentOutOfRangeException(nameof(entries), $"Entry ({e.Row},{e.Col}) outside {rows}x{cols}.");
        Array.Sort(sorted, static (x, y) => x.Row != y.Row ? x.Row.CompareTo(y.Row) : x.Col.CompareTo(y.Col));
        var rp = new int[rows + 1];
        var ci = new List<int>(sorted.Length); var vs = new List<T>(sorted.Length);
        int k = 0;
        for (int i = 0; i < rows; i++)
        {
            while (k < sorted.Length && sorted[k].Row == i)
            {
                int col = sorted[k].Col; T sum = T.Zero;
                while (k < sorted.Length && sorted[k].Row == i && sorted[k].Col == col) sum += sorted[k++].Value;
                if (!T.IsZero(sum)) { ci.Add(col); vs.Add(sum); }
            }
            rp[i + 1] = vs.Count;
        }
        return new(rows, cols, rp, [.. ci], [.. vs]);
    }

    public T this[int row, int col]
    {
        get
        {
            int idx = Array.BinarySearch(_cols, _rowPtr[row], _rowPtr[row + 1] - _rowPtr[row], col);
            return idx >= 0 ? _vals[idx] : T.Zero;
        }
    }

    /// <summary>y = A·x.</summary>
    public void Multiply(ReadOnlySpan<T> x, Span<T> y, bool parallel = false)
    {
        Simd.SameLength(x.Length, Columns);
        Simd.SameLength(y.Length, Rows);
        if (!parallel || NonZeros < 32_768) { RowRange(x, y, 0, Rows); return; }
        var xs = x.ToArray(); var ys = new T[Rows];
        Par.Range(Rows, (lo, hi) => RowRange(xs, ys, lo, hi));
        ys.CopyTo(y);
    }

    void RowRange(ReadOnlySpan<T> x, Span<T> y, int lo, int hi)
    {
        var rp = _rowPtr; var ci = _cols; var vs = _vals;
        for (int i = lo; i < hi; i++)
        {
            T s = T.Zero;
            for (int p = rp[i]; p < rp[i + 1]; p++) s += vs[p] * x[ci[p]];
            y[i] = s;
        }
    }

    public T[,] ToDense()
    {
        var d = new T[Rows, Columns];
        for (int i = 0; i < Rows; i++)
            for (int p = _rowPtr[i]; p < _rowPtr[i + 1]; p++) d[i, _cols[p]] = _vals[p];
        return d;
    }
}

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
