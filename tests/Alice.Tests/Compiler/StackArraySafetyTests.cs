using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Stack arrays (Lowerer.StackArrays) must never change behavior: no compile breaks, no stack exhaustion.</summary>
[Collection("console")]
public class StackArraySafetyTests
{
    static object? Run(string src, string method, params object[] args)
    {
        var asm = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "stk", exe: false));
        return asm.GetType("Program")!.GetMethod(method)!.Invoke(null, args);
    }

    [Fact]
    public void Parallel_loop_with_a_local_small_array_compiles_and_is_correct()
    {
        // the per-iteration buffer is private to the iteration: it is not an alias candidate of `out`
        const string src = "def f(n : Int32) : Int32\n  out = Array(Int32).new(100000, 0)\n  for i in 0...100000\n    p = [i, i * 2]\n    out[i] = p[0] + p[1]\n  end\n  out[99999] + n\nend\n";
        Assert.Equal(99999 * 3 + 1, Run(src, "f", 1));
    }

    [Fact]
    public void Recursive_methods_keep_arrays_on_the_heap()
    {
        // one stackalloc per frame would exhaust the thread stack long before the heap version runs out
        const string src = "def g(d : Int32) : Int32\n  a = Array(Float64).new(256, 1.0)\n  return a.size if d == 0\n  g(d - 1) + a[0].to_i\nend\n";
        Assert.DoesNotContain("stackalloc", AliceCompiler.Transpile(src));
    }

    [Fact]
    public void Stack_buffers_per_method_stay_within_a_budget()
    {
        var sb = new System.Text.StringBuilder("def f(n : Int32) : Float64\n  s = 0.0\n");
        for (int k = 0; k < 8; k++) sb.Append($"  a{k} = Array(Float64).new(256, 1.0)\n  s += a{k}[0]\n");
        sb.Append("  s\nend\n");
        var cs = AliceCompiler.Transpile(sb.ToString());
        int bytes = System.Text.RegularExpressions.Regex.Matches(cs, @"stackalloc double\[(\d+)\]").Sum(m => int.Parse(m.Groups[1].Value) * 8);
        Assert.InRange(bytes, 1, 4096);
        Assert.Equal(8.0, Run(sb.ToString(), "f", 0));
    }
}
