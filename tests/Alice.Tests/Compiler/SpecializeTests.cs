using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// MLton-style specialization (ADR 0026): a user higher-order function called with a lambda literal is cloned per
/// lambda, and the clone calls the lambda as a static method: no delegate, no closure, nothing for the JIT to guess.
/// </summary>
[Collection("console")]
public class SpecializeTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    const string ApplyN = "def apply_n(n : Int32, &f : Int32 -> Int64) : Int64\n  s = 0_i64\n  i = 0\n  while i < n\n    s += yield i\n    i += 1\n  end\n  s\nend\n";
    const string Fold = "def fold(xs : Array(Int64), init : Int64, f : Func(Int64, Int64, Int64)) : Int64\n  acc = init\n  for x in xs\n    acc = f.call(acc, x)\n  end\n  acc\nend\n";

    /// <summary>The C# text of the method whose declaration contains <paramref name="sig"/>.</summary>
    static string Method(string cs, string sig)
    {
        int start = cs.IndexOf(sig, StringComparison.Ordinal);
        Assert.True(start >= 0, $"`{sig}` not found in:\n{cs}");
        return cs.Substring(start, cs.IndexOf("\n}\n", start, StringComparison.Ordinal) - start);
    }

    [Fact]
    public void Block_literal_gets_a_clone_without_a_delegate()
    {
        var cs = AliceCompiler.Transpile(ApplyN + "puts apply_n(10) { |v| v.to_i64 * 2 }\n");
        var clone = Method(cs, "apply_n__spec");
        Assert.DoesNotContain("Func<", clone);
        Assert.DoesNotContain("=>", Method(cs, "void Main("));
        Assert.Equal("90", Run(ApplyN + "puts apply_n(10) { |v| v.to_i64 * 2 }\n"));
    }

    [Fact]
    public void Captured_values_become_parameters_of_the_clone()
    {
        string src = ApplyN + "k = 3_i64\nputs apply_n(10) { |v| v.to_i64 * k }\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.DoesNotContain("Func<", Method(cs, "apply_n__spec"));
        Assert.DoesNotContain("=>", Method(cs, "void Main("));
        Assert.Equal("135", Run(src));
    }

    [Fact]
    public void Lambda_argument_called_with_call_is_specialized()
    {
        string src = Fold + "k = 2_i64\nxs = [1_i64, 2_i64, 3_i64]\nputs fold(xs, 0_i64, ->(a : Int64, b : Int64) { a + b })\nputs fold(xs, 1_i64, ->(a : Int64, b : Int64) { a + b * k })\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.DoesNotContain("Func<", Method(cs, "fold__spec"));
        Assert.Equal("6\n13", Run(src));
    }

    [Fact]
    public void A_block_that_assigns_a_captured_local_shares_it_by_reference()
    {
        string src = "def each_to(n : Int32, &f : Int32 ->)\n  i = 0\n  while i < n\n    yield i\n    i += 1\n  end\nend\ncount = 0\neach_to(5) { |i| count += i }\nputs count\n";
        Assert.Contains("ref", Method(AliceCompiler.Transpile(src), "each_to__spec"));
        Assert.Equal("10", Run(src));
    }

    [Fact]
    public void Evaluation_order_is_kept()
    {
        string src = "def twice(&f : Int32 -> Int32) : Int32\n  a = yield 1\n  b = yield 2\n  a * 10 + b\nend\ndef note(x : Int32) : Int32\n  puts \"arg #{x}\"\n  x\nend\nputs twice { |v| note(v) + 1 }\n";
        Assert.Contains("twice__spec", AliceCompiler.Transpile(src));
        Assert.Equal("arg 1\narg 2\n23", Run(src));
    }

    [Fact]
    public void An_escaping_function_parameter_is_not_specialized()
    {
        string src = "def keep(f : Func(Int32, Int32)) : Func(Int32, Int32)\n  f\nend\nputs keep(->(x : Int32) { x + 1 }).call(4)\n";
        Assert.DoesNotContain("keep__spec", AliceCompiler.Transpile(src));
        Assert.Equal("5", Run(src));
    }

    [Fact]
    public void Identical_capture_free_lambdas_share_one_clone()
    {
        string src = ApplyN + "puts apply_n(3) { |v| v.to_i64 * 2 }\nputs apply_n(4) { |v| v.to_i64 * 2 }\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.Contains("apply_n__spec1(", cs);
        Assert.DoesNotContain("apply_n__spec2(", cs);
        Assert.Equal("6\n12", Run(src));
    }

    [Fact]
    public void Capturing_closure_allocates_nothing_once_specialized()
    {
        var asm = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(ApplyN + "def f(n : Int32) : Int64\n  k = n.to_i64\n  apply_n(n) { |v| v.to_i64 * k }\nend\n")], "spec", exe: false));
        var m = asm.GetType("Program")!.GetMethod("f")!;
        Assert.Equal(50L, m.Invoke(null, [5]));
        var f = m.CreateDelegate<Func<int, long>>();
        for (int i = 0; i < 50; i++) f(5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) f(5);
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before) / 100);
    }
}
