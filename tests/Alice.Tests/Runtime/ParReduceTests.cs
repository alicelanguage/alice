using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>`sum`/`min`/`max` of large primitive arrays fork into SIMD chunks; results equal the serial ones.</summary>
public class ParReduceTests
{
    static readonly int[] Sizes = [10, 1 << 20, (1 << 22) + 7];

    [Fact]
    public void Integer_sum_min_max_are_exact()
    {
        foreach (int n in Sizes)
        {
            var rng = new Random(n);
            var xs = Enumerable.Range(0, n).Select(_ => rng.Next(-1000, 1000)).ToArray();
            Assert.Equal(xs.Aggregate(0, (a, b) => unchecked(a + b)), xs.sum());
            Assert.Equal(xs.Min(), xs.min());
            Assert.Equal(xs.Max(), xs.max());
            var ls = xs.Select(x => (long)x * 1_000_000).ToArray();
            Assert.Equal(ls.Sum(), ls.sum());
        }
    }

    [Fact]
    public void Double_min_max_are_exact_and_sum_is_within_reassociation_error()
    {
        var rng = new Random(3);
        var xs = Enumerable.Range(0, (1 << 22) + 3).Select(_ => rng.NextDouble() * 2 - 1).ToArray();
        Assert.Equal(xs.Min(), xs.min());
        Assert.Equal(xs.Max(), xs.max());
        double exact = xs.Select(x => (decimal)x).Sum() is var d ? (double)d : 0;
        Assert.True(Math.Abs(xs.sum() - exact) < 1e-9, $"{xs.sum()} vs {exact}");
    }

    [Fact]
    public void Nan_propagates_through_parallel_min_and_max()
    {
        var xs = Enumerable.Range(0, 1 << 21).Select(i => (double)i).ToArray();
        xs[1_500_000] = double.NaN;
        Assert.True(double.IsNaN(xs.max()));
        Assert.True(double.IsNaN(xs.min()));
        Assert.True(double.IsNaN(xs.sum()));
    }
}
