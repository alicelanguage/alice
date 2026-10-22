using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// An empty collection filled by a counted loop, one unconditional add per iteration, is presized to the trip count:
/// no growth copies, and no large-object-heap garbage that only gen2 collections reclaim.
/// </summary>
[Collection("console")]
public class PresizeTests
{
    const string Soa = "@[SoA]\nrecord struct P(x : Float64, y : Float64)\n";

    [Fact]
    public void SoA_store_filled_by_a_counted_loop_is_reserved()
    {
        var src = Soa + "def make(n : Int32) : SoA(P)\n  ps = SoA(P).new\n  for i in 0...n\n    ps.add(P.new(i * 1.0, 2.0))\n  end\n  ps\nend\nputs make(1000).size\n";
        Assert.Matches(@"ps\.reserve\(", AliceCompiler.Transpile(src));
        Assert.Equal("1000", AliceCompiler.Run(src).Trim());
    }

    [Fact]
    public void List_filled_by_a_counted_loop_is_presized()
    {
        var src = "def make(n : Int32) : List(Int32)\n  xs = List(Int32).new\n  for i in 1..n\n    xs << i * 2\n  end\n  xs\nend\nputs make(10).Count\n";
        Assert.Contains("EnsureCapacity(", AliceCompiler.Transpile(src));
        Assert.Equal("10", AliceCompiler.Run(src).Trim());
    }

    [Theory]
    [InlineData("def make(n : Int32) : List(Int32)\n  xs = List(Int32).new\n  for i in 0...n\n    xs << i if i.even?\n  end\n  xs\nend\n")]            // conditional add: count unknown
    [InlineData("def make(n : Int32) : List(Int32)\n  xs = List(Int32).new\n  for i in 0...n\n    break if i > 5\n    xs << i\n  end\n  xs\nend\n")]       // early exit
    [InlineData("def make(n : Int32) : List(Int32)\n  xs = List(Int32).new\n  k = 0\n  for i in 0...n\n    xs << i\n  end\n  xs\nend\n")]               // not adjacent
    public void Uncertain_counts_are_not_presized(string src)
        => Assert.DoesNotContain("EnsureCapacity(", AliceCompiler.Transpile(src));

    [Fact]
    public void Reserve_is_callable_from_Alice()
    {
        var src = Soa + "ps = SoA(P).new\nps.reserve(100)\nps.add(P.new(1.0, 2.0))\nputs ps.size\n";
        Assert.Equal("1", AliceCompiler.Run(src).Trim());
    }
}
