using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Appending to or index-writing an ivar's collection writes state the method did not allocate (audit F9).</summary>
[Collection("console")]
public class PurityMutatesTests
{
    [Theory]
    [InlineData("class Log\n  def initialize\n    @items = List(Int32).new\n  end\n  pure def add(x : Int32) : Int32\n    @items << x\n    x\n  end\nend\nputs Log.new.add(1)\n")]
    [InlineData("class Log\n  def initialize\n    @items = Array(Int32).new(4, 0)\n  end\n  pure def put(x : Int32) : Int32\n    @items[0] = x\n    x\n  end\nend\nputs Log.new.put(1)\n")]
    [InlineData("class Log\n  def initialize\n    @items = List(Int32).new\n  end\n  pure def add(x : Int32) : Int32\n    @items.Add(x)\n    x\n  end\nend\nputs Log.new.add(1)\n")]
    [InlineData("class Log\n  def initialize\n    @grid = [[0, 0], [0, 0]]\n  end\n  pure def put(x : Int32) : Int32\n    @grid[0][1] = x\n    x\n  end\nend\nputs Log.new.put(1)\n")]
    public void Writes_through_ivars_are_not_pure(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0210", ex.Diagnostics[0].Code);
    }

    [Fact]
    public void Reading_an_ivar_collection_stays_pure() =>
        Assert.Equal("2", AliceCompiler.Run("class Log\n  def initialize\n    @items = [1, 2]\n  end\n  pure def n : Int32\n    @items.size + @items[0] - 1\n  end\nend\nputs Log.new.n\n").Trim());
}
