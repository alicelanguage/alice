using Alice.Runtime;

namespace Alice.Tests.Runtime;

public class SimdTests
{
    // Lengths straddle vector widths so both the vector body and scalar tail run.
    public static TheoryData<int> Lengths => new() { 0, 1, 3, 7, 8, 15, 16, 17, 31, 64, 1000, 1023 };

    static double[] Seq(int n, double scale = 1) => Enumerable.Range(0, n).Select(i => (i + 1) * scale).ToArray();

    [Theory, MemberData(nameof(Lengths))]
    public void Sum_matches_scalar(int n)
    {
        var a = Seq(n);
        Assert.Equal(a.Sum(), Simd.Sum<double>(a), 6);
        Assert.Equal(Enumerable.Range(1, n).Sum(), Simd.Sum<int>(Enumerable.Range(1, n).ToArray()));
    }

    [Theory, MemberData(nameof(Lengths))]
    public void Dot_matches_scalar(int n)
    {
        var a = Seq(n); var b = Seq(n, 0.5);
        Assert.Equal(a.Zip(b, (x, y) => x * y).Sum(), Simd.Dot<double>(a, b), 3);
    }

    [Theory, MemberData(nameof(Lengths))]
    public void Elementwise_ops(int n)
    {
        var a = Seq(n); var b = Seq(n, 2); var dst = new double[n];
        Simd.Add<double>(a, b, dst);
        Assert.Equal(a.Zip(b, (x, y) => x + y), dst);
        Simd.Multiply<double>(a, b, dst);
        Assert.Equal(a.Zip(b, (x, y) => x * y), dst);
        Simd.Scale<double>(a, 3, dst);
        Assert.Equal(a.Select(x => x * 3), dst);
    }

    [Theory, MemberData(nameof(Lengths))]
    public void Min_max(int n)
    {
        if (n == 0) { Assert.Throws<ArgumentException>(() => Simd.Max<double>([])); return; }
        var a = Seq(n).Select(x => Math.Sin(x) * 100).ToArray();
        Assert.Equal(a.Max(), Simd.Max<double>(a));
        Assert.Equal(a.Min(), Simd.Min<double>(a));
    }

    [Fact]
    public void Length_mismatch_throws()
    {
        Assert.Throws<ArgumentException>(() => Simd.Dot<double>(new double[3], new double[4]));
    }

    [Theory, MemberData(nameof(Lengths))]
    public void Int_kernels_and_index_of_max(int n)
    {
        var a = Enumerable.Range(0, n).Select(i => (i * 7919) % 101 - 50).ToArray();
        var b = a.Select(x => x + 3).ToArray();
        Assert.Equal(a.Sum(), Simd.Sum<int>(a));
        Assert.Equal(a.Zip(b, (x, y) => x * y).Sum(), Simd.Dot<int>(a, b));
        if (n == 0) { Assert.Equal(-1, Simd.IndexOfMax<int>(a)); return; }
        Assert.Equal(a.Max(), Simd.Max<int>(a));
        Assert.Equal(a.Min(), Simd.Min<int>(a));
        Assert.Equal(Array.IndexOf(a, a.Max()), Simd.IndexOfMax<int>(a));
        var bytes = a.Select(x => (byte)(x + 50)).ToArray();
        Assert.Equal(bytes.Max(), Simd.Max<byte>(bytes));
    }
}
