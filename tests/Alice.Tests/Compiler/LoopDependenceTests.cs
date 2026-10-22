using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Parallel counted loops (ADR 0008) must not carry a dependency between iterations: a written array is read
/// only at the loop index, and a read of another array at a shifted index runs in parallel only when a runtime
/// check proves the two arrays are distinct objects.
/// </summary>
public class LoopDependenceTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "dep", exe: false)).GetType("Program")!.GetMethod("f")!;

    const int N = 200_000;

    const string Prefix = "def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  for i in 1...n\n    a[i] = a[i - 1] + 1\n  end\n  a[n - 1]\nend\n";
    const string Aliased = "def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  b = a\n  for i in 1...n\n    a[i] = b[i - 1] + 1\n  end\n  a[n - 1]\nend\n";
    const string Stencil = "def f(n : Int32) : Int32\n  src = Array(Int32).new(n, 1)\n  dst = Array(Int32).new(n)\n  for i in 1...n - 1\n    dst[i] = src[i - 1] + src[i + 1]\n  end\n  dst[n / 2]\nend\n";

    [Fact]
    public void Loop_carried_dependency_stays_serial()
    {
        Assert.DoesNotContain("__parfor", AliceCompiler.Transpile(Prefix));
        Assert.Equal(N - 1, Compile(Prefix).Invoke(null, [N]));
    }

    [Fact]
    public void Aliased_arrays_take_the_serial_path()
        => Assert.Equal(N - 1, Compile(Aliased).Invoke(null, [N]));

    [Fact]
    public void Stencil_over_a_distinct_array_stays_parallel()
    {
        var cs = AliceCompiler.Transpile(Stencil);
        Assert.Contains("__parfor", cs);
        Assert.Contains("ReferenceEquals(dst, src)", cs);
        Assert.Equal(2, Compile(Stencil).Invoke(null, [N]));
    }
}
