using Alice.Runtime;

namespace Alice.Tests.Runtime;

public class NativeTests
{
    [Fact]
    public void Native_library_is_built_and_loaded() => Assert.True(Native.IsAvailable);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1000)]
    public void Native_kernels_match_managed(int n)
    {
        var a = Enumerable.Range(0, n).Select(i => i * 0.5).ToArray();
        var b = Enumerable.Range(0, n).Select(i => 1.0 + i).ToArray();
        Assert.Equal(Simd.Dot<double>(a, b), Native.Dot(a, b), 6);
        Assert.Equal(Simd.Sum<double>(a), Native.Sum(a), 6);
    }
}
