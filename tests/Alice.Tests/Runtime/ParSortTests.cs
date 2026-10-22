using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>`sort` on large primitive arrays runs a fork/join merge sort (ADR 0015); results equal the serial sort.</summary>
public class ParSortTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(70_000)]
    [InlineData(1_000_003)]
    public void Parallel_sort_equals_serial_sort_for_ints(int n)
    {
        var rng = new Random(n);
        var xs = Enumerable.Range(0, n).Select(_ => rng.Next(-1000, 1000)).ToArray();
        var copy = (int[])xs.Clone();
        var expected = (int[])xs.Clone();
        Array.Sort(expected);
        Assert.Equal(expected, xs.sort());
        Assert.Equal(copy, xs);   // sort returns a new array; the receiver is untouched
    }

    [Fact]
    public void Parallel_sort_orders_doubles_with_nan_like_array_sort()
    {
        var rng = new Random(7);
        var xs = Enumerable.Range(0, 300_000).Select(i => i % 997 == 0 ? double.NaN : rng.NextDouble() - 0.5).ToArray();
        var expected = (double[])xs.Clone();
        Array.Sort(expected);
        Assert.Equal(expected, xs.sort());
    }

    [Fact]
    public void Strings_keep_the_serial_sort()
    {
        var xs = new[] { "b", "a", "c" };
        Assert.Equal(new[] { "a", "b", "c" }, xs.sort());
    }
}
