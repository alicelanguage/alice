using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Single-threaded targets (`kind = "wasm"`, ADR 0024): parallel loops, pipelines and forks lower to their serial
/// form, so no thread-pool code is emitted and results stay the same.</summary>
[Collection("console")]
public class SequentialTargetTests
{
    static string Seq(string src) => AliceCompiler.TranspileFull([new SourceFile("main.al", src)], null, "alice_native", sequential: true).CSharp[0];

    public static TheoryData<string, string> Programs => new()
    {
        // automatic parallel loop (reduction)
        { "def f(n : Int32) : Int64\n  s = 0L\n  for i in 0...n\n    k = 0L\n    for j in 0...100\n      k += j * i\n    end\n    s += k\n  end\n  s\nend\nputs f(70_000)\n", "Par." },
        // explicit `par for`
        { "def f(n : Int32) : Int64\n  out = Array(Int64).new(n)\n  par for i in 0...n\n    out[i] = i.to_i64 * 2\n  end\n  out.sum\nend\nputs f(100)\n", "Par." },
        // pure pipeline over an array
        { "def f(xs : Array(Int64)) : Int64\n  xs.map { |x| x * 3 }.select { |x| x % 2 == 0 }.sum { |x| x + 1 }\nend\nputs f(Array(Int64).new(10) { |i| i.to_i64 })\n", "Par." },
        // recursive fork
        { "def fib(n : Int32) : Int64\n  if n < 2\n    n.to_i64\n  else\n    a = fib(n - 1)\n    b = fib(n - 2)\n    a + b\n  end\nend\nputs fib(20)\n", "Par." },
        // explicit `par` let
        { "pure def work(n : Int64) : Int64\n  s = 0_i64\n  for i in 0...n\n    s += i\n  end\n  s\nend\ndef both(n : Int64) : Int64\n  par a, b = work(n), work(n + 1)\n  a + b\nend\nputs both(10_i64)\n", "Par." },
    };

    [Theory, MemberData(nameof(Programs))]
    public void Parallel_constructs_lower_to_serial_code(string src, string banned)
    {
        Assert.Contains(banned, AliceCompiler.Transpile(src));   // the default target does parallelize it
        var cs = Seq(src);
        Assert.DoesNotContain(banned, cs);
        Assert.DoesNotContain("__serial", cs);   // no fork clone either
        Assert.Equal(AliceCompiler.Run(src).Trim(), Run(cs).Trim());
    }

    [Fact]
    public void Par_for_that_is_not_independent_is_still_an_error()
    {
        const string src = "def f(n : Int32) : Int32\n  xs = Array(Int32).new(n)\n  par for i in 1...n\n    xs[i] = xs[i - 1] + 1\n  end\n  xs[n - 1]\nend\n";
        var ex = Assert.ThrowsAny<Exception>(() => Seq(src));
        Assert.Contains("cannot parallelize", ex.Message);
    }

    static string Run(string cs) => AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([cs], "seq", exe: true)), []);
}
