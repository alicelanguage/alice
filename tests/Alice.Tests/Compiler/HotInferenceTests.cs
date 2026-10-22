using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// A self-recursive function over primitives and primitive arrays is a compute kernel: it is compiled fully
/// optimized from its first call (AggressiveOptimization) instead of waiting in tier 0 and on-stack replacement.
/// Its calls have no virtual targets, so tiered PGO has nothing to add.
/// </summary>
[Collection("console")]
public class HotInferenceTests
{
    const string Opt = "MethodImplOptions.AggressiveOptimization";

    [Fact]
    public void Self_recursive_primitive_kernel_is_hot()
        => Assert.Contains(Opt, AliceCompiler.Transpile("def fib(n : Int32) : Int64\n  n < 2 ? n.to_i64 : fib(n - 1) + fib(n - 2)\nend\nputs fib(20)\n"));

    [Fact]
    public void Array_parameters_keep_the_kernel_hot()
        => Assert.Contains(Opt, AliceCompiler.Transpile("def fill(a : Array(Int32), i : Int32)\n  if i < a.size\n    a[i] = i\n    fill(a, i + 1)\n  end\nend\n"));

    [Fact]
    public void Loop_nest_is_hot()   // a long-running nest would otherwise sit in instrumented tier 0 until OSR
        => Assert.Contains(Opt, AliceCompiler.Transpile("def f(n : Int32) : Int64\n  s = 0_i64\n  for i in 0...n\n    for j in 0...n\n      s += i * j\n    end\n  end\n  s\nend\n"));

    [Fact]
    public void Primitive_functions_called_from_loops_are_hot()   // noise(x, y) in a pixel loop, and lattice inside it
    {
        var cs = string.Join("\n", AliceCompiler.Transpile("def lattice(x : Int32) : Float64\n  x * 0.5\nend\ndef noise(x : Int32) : Float64\n  lattice(x) + lattice(x + 1)\nend\ndef render(n : Int32) : Float64\n  s = 0.0\n  for i in 0...n\n    s += noise(i)\n  end\n  s\nend\n")
            .ReplaceLineEndings("\n").Split('\n').Where(l => !l.StartsWith("#line")));
        Assert.Contains(Opt + ")]\npublic static double lattice", cs);
        Assert.Contains(Opt + ")]\npublic static double noise", cs);
    }

    [Fact]
    public void Single_loop_keeps_tiered_pgo()
        => Assert.DoesNotContain(Opt, AliceCompiler.Transpile("def f(xs : Array(Int32)) : Int32\n  s = 0\n  for x in xs\n    s += x\n  end\n  s\nend\n"));

    [Theory]
    [InlineData("def sq(n : Int32) : Int32\n  n * n\nend\n")]                                                   // not recursive
    [InlineData("def walk(o : Object, n : Int32) : Int32\n  n == 0 ? 0 : walk(o, n - 1)\nend\n")]                 // reference parameter: PGO may devirtualize
    [InlineData("class T\n  def initialize(@l : T?)\n  end\nend\ndef build(d : Int32) : T\n  d == 0 ? T.new(nil) : T.new(build(d - 1))\nend\n")]   // builds objects: allocation-heavy, PGO helps
    public void Other_functions_stay_tiered(string src)
        => Assert.DoesNotContain(Opt, AliceCompiler.Transpile(src));
}
