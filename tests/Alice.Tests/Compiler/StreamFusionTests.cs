using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Producer/consumer fusion (ADR 0024): pure map/select/reject/take chains become one loop; impure stages keep their order.</summary>
[Collection("console")]
public class StreamFusionTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    static string Body(string cs, string sig)
    {
        int start = cs.IndexOf(sig);
        return cs.Substring(start, cs.IndexOf("\n}\n", start) - start);
    }

    [Theory]
    [InlineData("xs.map { |x| x * 3 }.select { |x| x.odd? }.take(2).sum", "12")]           // 3 + 9
    [InlineData("xs.reject { |x| x == 2 }.map { |x| x * x }.take(10).count", "4")]
    [InlineData("xs.map { |x| x + 1 }.take(3).max", "4")]
    [InlineData("xs.select { |x| x > 1 }.take(0).any?", "false")]
    [InlineData("xs.map { |x| x - 1 }.take(-1).count", "0")]
    [InlineData("xs.map { |x| x * 2 }.take(2).to_a.size", "2")]
    [InlineData("xs.map { |x| x * 2 }.take(9).to_a.sum", "30")]
    [InlineData("(1..6).map { |x| x * x }.select { |x| x.even? }.take(2).sum", "20")]
    [InlineData("\"abcab\".chars.select { |c| c == 'a' }.count", "2")]
    public void Fused_chains_compute_the_unfused_result(string expr, string want)
        => Assert.Equal(want, Run($"xs = [1, 2, 3, 4, 5]\nputs {expr}\n"));

    [Fact]
    public void Take_each_stops_after_n()
        => Assert.Equal("2\n4", Run("xs = [1, 2, 3, 4, 5]\nxs.map { |x| x * 2 }.take(2).each { |x| puts x }\n"));

    [Fact]
    public void Take_chain_is_one_loop_without_intermediate_arrays()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Int32)) : Int32\n  xs.map { |x| x * 3 }.select { |x| x.odd? }.take(2).sum\nend\n");
        var body = Body(cs, "int f(int[] xs)");
        Assert.DoesNotContain(".take(", body);
        Assert.DoesNotContain("=>", body);
        Assert.DoesNotContain("new ", body);
    }

    [Fact]
    public void To_a_of_a_map_chain_is_presized()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Int32)) : Array(Int32)\n  xs.map { |x| x + 1 }.map { |x| x * 2 }.to_a\nend\n");
        var body = Body(cs, "int[] f(int[] xs)");
        Assert.DoesNotContain("List<", body);
        Assert.Equal("4,6", Run("puts [1, 2].map { |x| x + 1 }.map { |x| x * 2 }.to_a.join(\",\")\n"));
        Assert.Equal("4,6", Run("puts [1, 2, 3].map { |x| x + 1 }.take(2).map { |x| x * 2 }.to_a.join(\",\")\n"));
        Assert.Equal("2,4,6", Run("puts (1..3).map { |x| x * 2 }.to_a.join(\",\")\n"));
    }

    [Fact]
    public void Impure_stages_keep_stage_by_stage_order()
    {
        // unfused semantics: every map call runs before any select call
        var src = "xs = [1, 2, 3]\nys = xs.map { |x| puts \"m#{x}\"\n x }.select { |x| puts \"s#{x}\"\n x > 1 }.to_a\nputs ys.size\n";
        Assert.Equal("m1\nm2\nm3\ns1\ns2\ns3\n2", Run(src));
    }

    [Fact]
    public void Impure_stage_before_a_short_circuit_runs_on_every_element()
    {
        var src = "n = 0\nxs = [1, 2, 3, 4]\nr = xs.map { |x| n += 1\n x }.any? { |x| x > 1 }\nputs r\nputs n\n";
        Assert.Equal("true\n4", Run(src));
        var t = "n = 0\nxs = [1, 2, 3, 4]\nr = xs.map { |x| n += 1\n x }.take(1).sum\nputs r\nputs n\n";
        Assert.Equal("1\n4", Run(t));
    }

    [Fact]
    public void Impure_sink_block_still_fuses_after_pure_stages()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Int32))\n  xs.map { |x| x * 2 }.select { |x| x > 2 }.each { |x| puts x }\nend\n");
        Assert.DoesNotContain("=>", Body(cs, "void f(int[] xs)"));
        Assert.Equal("4\n6", Run("[1, 2, 3].map { |x| x * 2 }.select { |x| x > 2 }.each { |x| puts x }\n"));
    }

    [Fact]
    public void Literal_array_in_an_inlined_block_lives_on_the_stack()
    {
        var src = "def f(xs : Array(Int32)) : Int32\n  xs.map { |x|\n    p = [x, 2]\n    p[0] * p[1]\n  }.sum\nend\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.Contains("stackalloc int[2]", cs);
        Assert.Equal("12", Run(src + "puts f([1, 2, 3])\n"));
    }

    [Fact]
    public void Escaping_literal_array_in_a_block_stays_on_the_heap()
    {
        var src = "def f(xs : Array(Int32)) : Int32\n  xs.map { |x|\n    p = [x, 2]\n    p.size + p.sum\n  }.sum\nend\n";
        Assert.DoesNotContain("stackalloc", AliceCompiler.Transpile(src));
        Assert.Equal("11", Run(src + "puts f([1, 2])\n"));
    }
}
