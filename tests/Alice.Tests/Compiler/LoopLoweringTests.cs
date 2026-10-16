using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Block iteration lowers to plain loops (no closure/delegate) for every collection element type.</summary>
[Collection("console")]
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

    [Fact]
    public void Generic_clr_collections_of_alice_types_keep_member_types()
    {
        var src = "class E\n  property alive : Bool = true\nend\ndef f(xs : List(E)) : Int32\n  n = 0\n  for i in 0...xs.Count\n    e = xs[i]\n    n += 1 if e.alive\n  end\n  d = Dictionary(String, E).new\n  d[\"a\"] = xs[0]\n  k = d.Count + xs.Count\n  n + k\nend\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.DoesNotContain("Count()", cs);
        Assert.Contains("__parfor", cs);   // element type known: the counting loop is a proven-pure reduction
    }

    [Fact]
    public void List_loops_iterate_a_span_only_when_the_body_cannot_change_the_list()
    {
        var safe = AliceCompiler.Transpile("def f(xs : List(Float64)) : Float64\n  s = 0.0\n  xs.each { |x| s += Math.Sqrt(x) }\n  s\nend\n");
        Assert.Contains("CollectionsMarshal.AsSpan(xs)", safe);
        var unsafeBody = AliceCompiler.Transpile("def grow(xs : List(Float64))\n  xs.Add(1.0)\nend\ndef f(xs : List(Float64)) : Float64\n  s = 0.0\n  xs.each { |x| grow(xs) }\n  s\nend\n");
        Assert.DoesNotContain("AsSpan", unsafeBody);
        var direct = AliceCompiler.Transpile("def f(xs : List(Float64))\n  xs.each { |x| xs.Add(x) if x > 1.0 }\nend\n");
        Assert.DoesNotContain("AsSpan", direct);
    }

    [Fact]
    public void Span_lowered_list_loop_gives_the_same_result()
    {
        Assert.Equal("6", AliceCompiler.Run("xs = List(Int32).new\nxs.Add(1)\nxs.Add(2)\nxs.Add(3)\nt = 0\nxs.each { |x| t += x }\nputs t").Trim());
    }
}
