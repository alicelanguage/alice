using Alice.Runtime;

namespace Alice.Tests.Runtime;

public class ParallelMathTests
{
    [Fact]
    public void Par_for_visits_every_index_once()
    {
        var hits = new int[100_003];
        Par.For(hits.Length, i => Interlocked.Increment(ref hits[i]));
        Assert.All(hits, h => Assert.Equal(1, h));
    }

    [Fact]
    public void Par_map_preserves_order()
    {
        var src = Enumerable.Range(0, 10_000).ToArray();
        Assert.Equal(src.Select(x => x * 2L), Par.Map(src, x => x * 2L));
    }

    [Fact]
    public void Par_sum_matches()
    {
        var src = Enumerable.Range(0, 1_000_000).Select(i => (double)i).ToArray();
        Assert.Equal(src.Sum(), Par.Sum<double>(src), 1);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 5, 7)]
    [InlineData(64, 64, 64)]
    [InlineData(97, 33, 129)]
    public void Matmul_matches_naive(int n, int k, int m)
    {
        var rnd = new Random(n * 31 + m);
        var a = Enumerable.Range(0, n * k).Select(_ => rnd.NextDouble()).ToArray();
        var b = Enumerable.Range(0, k * m).Select(_ => rnd.NextDouble()).ToArray();
        var c = new double[n * m];
        AMath.MatMul<double>(a, b, c, n, k, m);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                double s = 0;
                for (int p = 0; p < k; p++) s += a[i * k + p] * b[p * m + j];
                Assert.Equal(s, c[i * m + j], 9);
            }
    }

    [Fact]
    public void Stats()
    {
        double[] x = [2, 4, 4, 4, 5, 5, 7, 9];
        Assert.Equal(5, AMath.Mean<double>(x));
        Assert.Equal(4, AMath.Variance<double>(x));
        Assert.Equal(Math.Sqrt(x.Sum(v => v * v)), AMath.Norm(x), 9);
        Assert.Equal(5, AMath.Clamp(9, 0, 5));
        Assert.Equal(2.5, AMath.Lerp(0.0, 10.0, 0.25));
    }
}
