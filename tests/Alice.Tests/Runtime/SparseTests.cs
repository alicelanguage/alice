using Alice.Runtime;

namespace Alice.Tests.Runtime;

public class SparseTests
{
    static readonly double[,] Dense =
    {
        { 1, 0, 0, 2 },
        { 0, 0, 0, 0 },
        { 0, 3, 4, 0 },
    };

    [Fact]
    public void Csr_from_dense_keeps_only_nonzeros()
    {
        var m = CsrMatrix<double>.FromDense(Dense);
        Assert.Equal(3, m.Rows);
        Assert.Equal(4, m.Columns);
        Assert.Equal(4, m.NonZeros);
        Assert.Equal([0, 2, 2, 4], m.RowPointers.ToArray());
        Assert.Equal([0, 3, 1, 2], m.ColumnIndices.ToArray());
        Assert.Equal(4.0, m[2, 2]);
        Assert.Equal(0.0, m[1, 3]);
    }

    [Fact]
    public void Csr_from_triplets_sums_duplicates_and_sorts()
    {
        var m = CsrMatrix<double>.FromTriplets(3, 4, [(2, 2, 4), (0, 3, 1), (2, 1, 3), (0, 0, 1), (0, 3, 1)]);
        Assert.Equal(CsrMatrix<double>.FromDense(Dense).ToDense(), m.ToDense());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Csr_multiply_vector(bool parallel)
    {
        var m = CsrMatrix<double>.FromDense(Dense);
        var y = new double[3];
        m.Multiply([1, 2, 3, 4], y, parallel);
        Assert.Equal([9.0, 0.0, 18.0], y);
    }

    [Fact]
    public void Large_csr_parallel_equals_serial()
    {
        var rnd = new Random(42);
        var t = Enumerable.Range(0, 50_000).Select(_ => (rnd.Next(2000), rnd.Next(2000), rnd.NextDouble())).ToArray();
        var m = CsrMatrix<double>.FromTriplets(2000, 2000, t);
        var x = Enumerable.Range(0, 2000).Select(i => (double)i).ToArray();
        var a = new double[2000]; var b = new double[2000];
        m.Multiply(x, a, parallel: false);
        m.Multiply(x, b, parallel: true);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Sparse_vector_dot_dense()
    {
        var v = SparseVector<double>.FromDense([0, 5, 0, 0, 2]);
        Assert.Equal(2, v.NonZeros);
        Assert.Equal(5 * 10 + 2 * 40.0, v.Dot([1, 10, 20, 30, 40]));
        Assert.Equal(5 * 5 + 2 * 2.0, v.Dot(v));
    }
}
