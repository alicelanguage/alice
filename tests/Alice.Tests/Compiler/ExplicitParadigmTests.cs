using Alice.Compiler;
using Xunit;

namespace Alice.Tests.Compiler;

/// <summary>
/// 0.12.2: explicit opt-ins. Ergonomic code is still inferred; these let a program state what it wants
/// (an effect, a fork) so the compiler optimizes without heuristics, and turns a failed proof into a compile error.
/// </summary>
[Collection("console")]   // runs programs and captures stdout
public class ExplicitParadigmTests
{
    [Fact]
    public void Pure_def_is_checked()
    {
        const string ok = "pure def sq(n : Int32) : Int32\n  n * n\nend\nputs sq(7)\n";
        Assert.Equal("49", AliceCompiler.Run(ok).Trim());
        var ex = Assert.ThrowsAny<Exception>(() => AliceCompiler.Transpile("pure def loud(n : Int32) : Int32\n  puts n\n  n\nend\n"));
        Assert.Contains("not pure", ex.Message);
    }

    [Fact]
    public void Par_let_forks_without_recursion()
    {
        // Bend's `a b = f(x) g(y)`: the programmer asks for the fork, so it no longer has to be recursive
        const string src = "pure def work(n : Int64) : Int64\n  s = 0_i64\n  for i in 0...n\n    s += i\n  end\n  s\nend\ndef both(n : Int64) : Int64\n  par a, b = work(n), work(n + 1)\n  a + b\nend\nputs both(10_i64)\n";
        Assert.Contains("Par.Fork(", AliceCompiler.Transpile(src));
        Assert.Equal("100", AliceCompiler.Run(src).Trim());
    }

    [Theory]
    [InlineData("def g(n : Int32) : Int32\n  puts n\n  n\nend\ndef f(n : Int32) : Int32\n  par a, b = g(n), g(n + 1)\n  a + b\nend\n")]   // impure
    [InlineData("pure def g(n : Int32) : Int32\n  n\nend\ndef f(n : Int32) : Int32\n  a = 0\n  b = 0\n  par a, b = g(n), g(a)\n  a + b\nend\n")]   // dependent
    public void Par_let_that_cannot_fork_is_an_error(string src)
    {
        var ex = Assert.ThrowsAny<Exception>(() => AliceCompiler.Transpile(src));
        Assert.Contains("cannot fork", ex.Message);
    }

    [Fact]
    public void Inline_def_asks_for_aggressive_inlining()
    {
        var cs = AliceCompiler.Transpile("inline def sq(n : Int32) : Int32\n  n * n\nend\nputs sq(3)\n");
        Assert.Contains("MethodImplOptions.AggressiveInlining", cs);
    }

    [Fact]
    public void Safe_def_runs_only_alice_code()
    {
        Assert.Equal("6", AliceCompiler.Run("safe def add(a : Int32, b : Int32) : Int32\n  a + b\nend\nputs add(2, 4)\n").Trim());
    }

    [Theory]
    [InlineData("safe def f : Int32\n  System.Environment.ProcessorCount\nend\n")]                                   // .NET interop
    [InlineData("safe def f(a : Int32) : Int32\n  unchecked\n    a * a\n  end\nend\n")]                             // unchecked arithmetic
    [InlineData("def g : Int32\n  System.Environment.ProcessorCount\nend\nsafe def f : Int32\n  g\nend\n")]         // transitively
    public void Safe_def_rejects_escapes(string src)
    {
        var ex = Assert.ThrowsAny<Exception>(() => AliceCompiler.Transpile(src));
        Assert.Contains("not safe", ex.Message);
    }
}
