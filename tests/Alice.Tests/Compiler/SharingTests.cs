using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// 0.12.2: immutable objects built from constants are shared (hash-consing, as interaction nets share
/// erasers). `Node.new(nil, nil)` in a tree builder is one static instance instead of half of all allocations, as
/// long as nothing in the program can tell two such objects apart.
/// </summary>
[Collection("console")]
public class SharingTests
{
    const string Node = "class Node\n  getter left : Node?\n  getter right : Node?\n  def initialize(@left : Node?, @right : Node?)\n  end\n  def check : Int32\n    l = left\n    r = right\n    if l.nil? || r.nil?\n      1\n    else\n      1 + l.check + r.check\n    end\n  end\nend\ndef build(d : Int32) : Node\n  d > 0 ? Node.new(build(d - 1), build(d - 1)) : Node.new(nil, nil)\nend\n";

    [Fact]
    public void Constant_immutable_objects_are_shared()
    {
        var src = Node + "puts build(10).check\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.Contains("__shared_Node_", cs);
        Assert.Equal("2047", AliceCompiler.Run(src).Trim());
    }

    [Fact]
    public void Shared_leaves_halve_tree_allocation()
    {
        var cs = AliceCompiler.Transpile(Node);
        var build = AliceCompiler.Load(AliceCompiler.Emit([cs], "share", exe: false)).GetType("Program")!.GetMethod("build")!;
        var f = build.CreateDelegate<Func<int, object>>();
        for (int i = 0; i < 20; i++) f(10);
        long before = GC.GetAllocatedBytesForCurrentThread();
        f(10);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 1023 * 40, $"1023 inner nodes only (got {bytes} bytes)");   // 2047 nodes unshared
    }

    [Theory]
    [InlineData("a = Node.new(nil, nil)\nb = Node.new(nil, nil)\nputs a.equal?(b)\n")]                 // identity observed
    [InlineData("a = Node.new(nil, nil)\nb = Node.new(nil, nil)\nputs a == b\n")]                      // reference equality
    [InlineData("xs = List(Node).new\nxs.Add(Node.new(nil, nil))\nputs xs.Count\n")]                    // handed to .NET
    public void Identity_use_keeps_fresh_objects(string main)
        => Assert.DoesNotContain("__shared_", AliceCompiler.Transpile(Node + main));

    [Fact]
    public void Mutable_objects_are_never_shared()
    {
        var src = "class Cell\n  getter v : Int32\n  def initialize(@v : Int32)\n  end\n  def bump\n    @v += 1\n  end\nend\nc = Cell.new(0)\nc.bump\nd = Cell.new(0)\nputs d.v\n";
        Assert.DoesNotContain("__shared_", AliceCompiler.Transpile(src));
        Assert.Equal("0", AliceCompiler.Run(src).Trim());
    }
}
