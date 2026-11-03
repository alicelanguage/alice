using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Emitted C# shapes for the Bend 2 codegen gaps (benchmarks/bend2/README.md, "Codegen causes"): branch-free integer
/// selects, full unrolling of short literal loops, and fully optimized primitive loops.
/// </summary>
[Collection("console")]
public class PerfShapeTests
{
    const string Opt = "MethodImplOptions.AggressiveOptimization";
    const string Sel = "global::Alice.Runtime.Branchless.";

    static string Cs(string src) => string.Join("\n", AliceCompiler.Transpile(src).ReplaceLineEndings("\n").Split('\n').Where(l => !l.StartsWith("#line")));

    // ---- branchless select (mandelbrot / kmeans: RyuJIT if-converts ternaries only outside loops) ----

    [Fact]
    public void Integer_select_over_names_is_branch_free()
        => Assert.Contains(Sel + "U32(a < b, a, b)", Cs("def min2(a : UInt32, b : UInt32) : UInt32\n  a < b ? a : b\nend\n"));

    [Fact]
    public void Wrapping_arithmetic_arms_are_branch_free()   // kmeans dst: |x - c| over UInt32
        => Assert.Contains(Sel + "U32(x < c, unchecked(c - x), unchecked(x - c))", Cs("def ad(x : UInt32, c : UInt32) : UInt32\n  x < c ? c &- x : x &- c\nend\n"));

    [Theory]
    [InlineData("def f(a : Int32, b : Int32) : Int32\n  a > b ? a : b\nend\nputs f(-5, -7)\nputs f(2, 9)\n", "-5\n9")]
    [InlineData("def f(a : Int64, b : Int64) : Int64\n  a < b ? a : b\nend\nputs f(-5_i64, 3_i64)\nputs f(4_i64, -9_i64)\n", "-5\n-9")]
    [InlineData("def f(a : UInt32, b : UInt32) : UInt32\n  a < b ? a : b\nend\nputs f(4294967295_u32, 1_u32)\nputs f(0_u32, 7_u32)\n", "1\n0")]
    public void Branch_free_select_keeps_the_value(string src, string expected)
    {
        Assert.Contains(Sel, Cs(src));
        Assert.Equal(expected, AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("def f(a : Array(Int32), i : Int32) : Int32\n  i < a.size ? a[i] : 0\nend\n")]          // an index can throw: only the taken arm may run
    [InlineData("def f(a : Int32, b : Int32) : Int32\n  b != 0 ? a / b : 0\nend\n")]                  // division can throw
    [InlineData("def f(a : Int32, b : Int32) : Int32\n  a > 0 ? a + b : b\nend\n")]                   // checked + can throw
    [InlineData("def g(x : Int32) : Int32\n  x\nend\ndef f(a : Int32) : Int32\n  a > 0 ? g(a) : 0\nend\n")]   // a call
    [InlineData("def f(a : Float64, b : Float64) : Float64\n  a < b ? a : b\nend\n")]                 // not an integer
    [InlineData("def f(a : UInt32) : UInt32\n  a > 7_u32 ? 1_u32 : 0_u32\nend\n")]                    // c ? 1 : 0 is already setcc
    public void Other_selects_stay_ternaries(string src)
        => Assert.DoesNotContain(Sel, Cs(src));

    [Fact]
    public void Conditional_update_stays_a_branch()   // mandelbrot's escape freeze: a select would sit on the loop-carried chain
    {
        var cs = Cs("def f(n : UInt32, c : UInt32) : UInt32\n  z = 0_u32\n  e = 0_u32\n  k = n\n  while k != 0_u32\n    e = e | (z > 1000_u32 ? 1_u32 : 0_u32)\n    nz = z &* z &+ c\n    z = e == 0_u32 ? nz : z\n    k = k &- 1_u32\n  end\n  z\nend\n");
        Assert.Contains("z = (e == 0U) ? nz", cs);
        Assert.DoesNotContain(Sel, cs);
    }

    [Fact]
    public void Guarded_index_is_not_evaluated()
        => Assert.Equal("0", AliceCompiler.Run("def f(a : Array(Int32), i : Int32) : Int32\n  i < a.size ? a[i] : 0\nend\nputs f([1, 2], 5)\n").Trim());

    // ---- full unrolling (merkle: 22 Speck rounds) ----

    const string Rounds = "def sp(rk : Array(UInt32), x0 : UInt32) : UInt32\n  x = x0\n  i = 0\n  while i < 4\n    x = ((x << 3) | (x >> 29)) ^ rk[i]\n    i += 1\n  end\n  x\nend\n";

    [Fact]
    public void Short_literal_loop_is_unrolled_with_constant_indices()
    {
        var cs = Cs(Rounds);
        Assert.DoesNotContain("while (i < 4)", cs);
        for (int k = 0; k < 4; k++) Assert.Contains($"^ rk[{k}]", cs);
        Assert.Contains("var i = 4;", cs);   // the counter keeps its final value
    }

    [Fact]
    public void Unrolled_loop_computes_the_same_value()   // expected values from the compiler before unrolling (main 3b6c7df)
        => Assert.Equal("3167833281\n4", AliceCompiler.Run(Rounds + "rk = Array(UInt32).new(4)\nrk[0] = 1_u32\nrk[1] = 22_u32\nrk[2] = 333_u32\nrk[3] = 4444_u32\nputs sp(rk, 123456789_u32)\ni = 0\nwhile i < 4\n  i += 1\nend\nputs i\n").Trim().ReplaceLineEndings("\n"));

    [Fact]
    public void Counting_loop_leaves_only_its_final_value()
    {
        var cs = Cs("def f() : Int32\n  i = 0\n  while i < 4\n    i += 1\n  end\n  i\nend\n");
        Assert.Contains("var i = 4;", cs);
        Assert.DoesNotContain("while", cs);
    }

    [Theory]
    [InlineData("def f(a : Array(UInt32)) : UInt32\n  x = 0_u32\n  i = 0\n  while i < 100\n    x = x ^ a[i]\n    i += 1\n  end\n  x\nend\n")]                       // too many trips
    [InlineData("def g(x : UInt32) : UInt32\n  x &* 3_u32\nend\ndef f(a : Array(UInt32)) : UInt32\n  x = 0_u32\n  i = 0\n  while i < 4\n    x = g(x) ^ a[i]\n    i += 1\n  end\n  x\nend\n")]   // a call in the body
    [InlineData("def f(a : Array(UInt32)) : UInt32\n  x = 0_u32\n  i = 0\n  while i < 4\n    break if a[i] == 0_u32\n    x = x ^ a[i]\n    i += 1\n  end\n  x\nend\n")]   // break
    [InlineData("def f(a : Array(UInt32)) : UInt32\n  x = 0_u32\n  i = 0\n  while i < 4\n    i += 1\n    x = x ^ a[i]\n  end\n  x\nend\n")]                         // counter written mid-body
    public void Other_loops_stay_loops(string src)
        => Assert.Contains("while (i < ", Cs(src));

    // ---- fully optimized loops over owned arrays (bfs maze); helper-calling loops keep tiered PGO (perf2, R0/R10) ----

    [Fact]
    public void Primitive_loop_calling_leaf_functions_keeps_tiered_pgo()   // kmeans chunk: within 3% either way on a core pair
    {
        var cs = Cs("enum St\n  L(n : UInt32)\nend\ndef h(x : UInt32) : UInt32\n  x ^ (x << 13)\nend\ndef chunk(i : UInt32) : St\n  s = 0_u32\n  j = 64_u32\n  while j != 0_u32\n    s = s &+ h(i &+ j)\n    j = j &- 1_u32\n  end\n  St::L(s)\nend\n");
        // St is an arena sum type (ADR 0037): its values are ulong terms
        Assert.Contains("public static ulong chunk", cs);
        Assert.DoesNotContain(Opt + ")]\npublic static ulong chunk", cs);
    }

    [Fact]
    public void Loop_over_an_owned_array_is_hot()
    {
        var cs = Cs("def maze(m : Int32) : Int32\n  g = Array(Int32).new(1024)\n  c = 0\n  while c < 1024\n    g[c] = c &* m\n    c += 1\n  end\n  g[m & 1023]\nend\nputs maze(3)\n");
        Assert.Contains("Own<int>.Rent(1024)", cs);
        Assert.Contains(Opt + ")]\npublic static int maze", cs);
    }

    [Fact]
    public void Loop_with_object_work_keeps_tiered_pgo()
        => Assert.DoesNotContain(Opt, Cs("def f(n : Int32) : Int32\n  xs = [] of String\n  i = 0\n  while i < n\n    xs << i.to_s\n    i += 1\n  end\n  xs.size\nend\n"));
}
