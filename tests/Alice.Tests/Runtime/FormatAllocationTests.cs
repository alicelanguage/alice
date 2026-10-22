using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>Formatting a number allocates the resulting string and nothing else (no boxing in the generic path).</summary>
public class FormatAllocationTests
{
    static long Allocated(Func<string> f)
    {
        for (int i = 0; i < 100; i++) f();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) f();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
    }

    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    [Fact]
    public void Generic_str_of_value_types_does_not_box()
    {
        Assert.Equal("12345", Prelude.str<int>(12345));
        Assert.Equal(Allocated(() => 12345.ToString(Inv)), Allocated(() => Prelude.str<int>(12345)));
        Assert.Equal(Allocated(() => 123456789012L.ToString(Inv)), Allocated(() => Prelude.str<long>(123456789012L)));
        Assert.Equal(Allocated(() => Prelude.str(2.5)), Allocated(() => Prelude.str<double>(2.5)));
        Assert.Equal(Allocated(() => ((byte)7).ToString(Inv)), Allocated(() => Prelude.str<byte>(7)));
    }

    [Fact]
    public void Inspect_of_value_types_does_not_box()
        => Assert.Equal(Allocated(() => 4242.ToString(Inv)), Allocated(() => Prelude.inspect<int>(4242)));
}
