using Alice.Runtime;

namespace Alice.Tests.Runtime;

public class MemoryTests
{
    [Fact]
    public void Growth_is_amortized_and_preserves_data()
    {
        var a = new int[0];
        for (int i = 0; i < 100; i++) { ArrayGrowth.Ensure(ref a, i + 1); a[i] = i; }
        Assert.True(a.Length is >= 100 and <= 256);
        Assert.Equal(Enumerable.Range(0, 100), a.Take(100));
    }

    [Fact]
    public void Free_list_is_lifo()
    {
        var f = new FreeList();
        f.Push(3); f.Push(7);
        Assert.True(f.TryPop(out var x)); Assert.Equal(7, x);
        Assert.True(f.TryPop(out x)); Assert.Equal(3, x);
        Assert.False(f.TryPop(out x)); Assert.Equal(-1, x);
    }
}
