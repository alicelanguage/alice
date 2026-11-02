using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// AL0202 sees through two blind spots of the audit: methods that write an ivar's collection are mutating (F9),
/// and a block-local copy of a shared reference is shared too (F10).
/// </summary>
[Collection("console")]
public class RacesAliasTests
{
    static void Race(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0202", ex.Diagnostics[0].Code);
    }

    const string Counter = "class Counter\n  def initialize\n    @items = List(Int32).new\n    @slots = Array(Int32).new(8, 0)\n  end\n";

    [Theory]
    [InlineData(Counter + "  def add(x : Int32) : Int32\n    @items << x\n    x\n  end\nend\nc = Counter.new\nPar.For(1000) { |i| c.add(i) }\n")]
    [InlineData(Counter + "  def put(x : Int32) : Int32\n    @slots[0] = x\n    x\n  end\nend\nc = Counter.new\nPar.For(1000) { |i| c.put(i) }\n")]
    [InlineData(Counter + "  def add(x : Int32) : Int32\n    @items.Add(x)\n    x\n  end\nend\nc = Counter.new\nt = spawn { c.add(1) }\nt.wait\n")]
    [InlineData(Counter + "  def add(x : Int32) : Int32\n    @items << x\n    x\n  end\n  def twice(x : Int32) : Int32\n    add(x)\n  end\nend\nc = Counter.new\nPar.For(10) { |i| c.twice(i) }\n")]
    public void Methods_writing_ivar_collections_mutate(string src) => Race(src);

    [Theory]
    [InlineData("xs = List(Int32).new\nPar.For(1000) { |i| ys = xs; ys.Add(i) }\n")]
    [InlineData("xs = List(Int32).new\nPar.For(1000) { |i| ys = xs; ys << i }\n")]
    [InlineData("xs = [0, 0]\nPar.For(10) { |i| ys = xs; ys[0] = i }\n")]
    [InlineData("xs = List(Int32).new\nt = spawn { ys = xs; ys.Add(1) }\nt.wait\n")]
    [InlineData("xs = List(Int32).new\nzs = List(Int32).new\nPar.For(10) { |i| ys = i > 5 ? xs : zs; ys.Add(i) }\n")]
    [InlineData("xs = List(Int32).new\nPar.For(10) { |i| ys = xs; ws = ys; ws.Add(i) }\n")]
    [InlineData("grid = [[0, 0], [0, 0]]\nPar.For(2) { |i| row = grid[0]; row[1] = i }\n")]
    public void Copies_of_shared_references_are_shared(string src) => Race(src);

    [Theory]
    [InlineData("Par.For(10) { |i| ys = List(Int32).new\n  ys.Add(i) }\nputs 1\n", "1")]
    [InlineData("out = Array(Int32).new(10)\nPar.For(10) { |i| o = out; o[i] = i }\nputs out.sum\n", "45")]
    [InlineData("xs = [1, 2, 3]\nout = Array(Int32).new(3)\nPar.For(3) { |i| ys = xs; out[i] = ys[i] }\nputs out.sum\n", "6")]
    public void Fresh_and_read_only_locals_stay_legal(string src, string expected) =>
        Assert.Equal(expected, AliceCompiler.Run(src).Trim());
}
