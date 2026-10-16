using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Block iteration lowers to plain loops (no closure/delegate) for every collection element type.</summary>
public class LoopLoweringTests
{
    [Theory]
    [InlineData("List(E)")]
    [InlineData("Array(E)")]
    [InlineData("List(Int32)")]
    [InlineData("HashSet(E)")]
    public void Each_over_collection_of_alice_class_is_a_loop(string coll)
    {
        var cs = AliceCompiler.Transpile($"class E\n  property alive : Bool = true\nend\ndef f(xs : {coll})\n  xs.each {{ |o| o.alive = !o.alive }}\nend\n".Replace("o.alive = !o.alive", coll.Contains("Int32") ? "puts o" : "o.alive = !o.alive"));
        Assert.DoesNotContain(".each(", cs);
        Assert.DoesNotContain("=>", cs[cs.IndexOf("void f(")..]);
    }
}
