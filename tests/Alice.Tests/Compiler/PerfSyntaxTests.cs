using System.Reflection;
using Alice.Compiler;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

/// <summary>
/// Syntax that hands the compiler a performance decision. `unchecked ... end` makes the integer + - * inside wrap
/// (the region form of `&+ &- &*`, ADR 0013). `par for` asks for the parallel loop whatever the trip count, and
/// is a compile error (AL0209) when iterations are not provably independent instead of silently running serially.
/// </summary>
[Collection("console")]
public class PerfSyntaxTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "perfsyn", exe: false)).GetType("Program")!.GetMethod("f")!;

    const string Hash = "def f(s : String) : Int32\n  h = 0\n  unchecked\n    for c in s.chars\n      h = h * 31 + c.ord\n    end\n  end\n  h\nend\n";

    [Fact]
    public void Unchecked_block_wraps()
    {
        Assert.Contains("unchecked", AliceCompiler.Transpile(Hash));
        string s = "the quick brown fox jumps over the lazy dog";
        int h = 0;
        unchecked { foreach (char c in s) h = h * 31 + c; }
        Assert.Equal(h, Compile(Hash).Invoke(null, [s]));
    }

    [Fact]
    public void Arithmetic_outside_the_block_stays_checked()
        => Assert.Throws<OverflowException>(() =>
        {
            try { Compile("def f(x : Int32) : Int32\n  unchecked\n    y = x * 2\n  end\n  x * 2\nend\n").Invoke(null, [int.MaxValue]); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        });

    [Fact]
    public void Par_for_parallelizes_a_short_loop()
    {
        var src = "def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  par for i in 0...n\n    a[i] = i * 2\n  end\n  a[n - 1]\nend\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.Contains("__parfor", cs);
        Assert.DoesNotContain("65536", cs);
        Assert.Equal(18, Compile(src).Invoke(null, [10]));
    }

    [Fact]
    public void Par_for_rejects_a_dependency()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  par for i in 1...n\n    a[i] = a[i - 1] + 1\n  end\n  a[n - 1]\nend\n"));
        Assert.Equal("AL0209", ex.Diagnostics[0].Code);
    }

    [Fact]
    public void Formatter_indents_unchecked_and_par_for()
        => Assert.Equal("def f(n : Int32)\n  unchecked\n    y = n * 2\n  end\n  par for i in 0...n\n    puts i\n  end\nend\n",
            Formatter.Format("def f(n : Int32)\nunchecked\ny = n * 2\nend\npar for i in 0...n\nputs i\nend\nend\n"));
}
