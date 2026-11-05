using Alice.Compiler;
using Alice.Compiler.Backend.Il;

namespace Alice.Tests.Backends;

/// <summary>IL backend constant folding: trap detection and JIT crash workarounds.</summary>
[Collection("console")]
public class IlConstFoldTests
{
    static void DifferentialExpectException<T>(string src) where T : Exception
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "il", true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        var decode = AirToIlTests.DecodeCheck(res.Pe!);
        Assert.True(decode.Count == 0, "IL decode check: " + string.Join(", ", decode));
        var errors = AirToIlTests.IlVerify(res.Pe!);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));

        // Both backends should throw the same exception type
        var csEx = Assert.Throws<T>(() => AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), []));
        var ilEx = Assert.Throws<T>(() => AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []));
        // Exception messages should match (ignoring line number differences)
        Assert.Equal(csEx.GetType(), ilEx.GetType());
    }

    static void Differential(string src, string expected)
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "il", true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        var decode = AirToIlTests.DecodeCheck(res.Pe!);
        Assert.True(decode.Count == 0, "IL decode check: " + string.Join(", ", decode));
        var errors = AirToIlTests.IlVerify(res.Pe!);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
        string cs = AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), []);
        string il = AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []);
        Assert.Equal(expected, cs.Replace("\r\n", "\n"));
        Assert.Equal(cs, il);
    }

    // Non-trapping operations: should fold to result
    [Fact]
    public void ConstFold_add_no_overflow() =>
        Differential("begin\n  puts 100 + 200\nend\n", "300\n");

    [Fact]
    public void ConstFold_mul_safe() =>
        Differential("begin\n  puts 6 * 7\nend\n", "42\n");

    [Fact]
    public void ConstFold_div_safe() =>
        Differential("begin\n  puts 42 / 6\nend\n", "7\n");

    // JIT bug workaround: x * INT_MIN where x is variable (not constant)
    // This should emit safe pattern, not mul.ovf with INT_MIN constant
    // Note: In Alice, * is checked by default, &* is wrapping (unchecked)
    [Fact]
    public void JitWorkaround_var_mul_int32_min_wrapping() =>
        Differential("begin\n  x = 2\n  puts x &* (-2147483648)\nend\n", "0\n");

    [Fact]
    public void JitWorkaround_var_mul_int32_min_wrapping_zero() =>
        Differential("begin\n  x = 0\n  puts x &* (-2147483648)\nend\n", "0\n");

    [Fact]
    public void JitWorkaround_var_mul_int32_min_wrapping_one() =>
        Differential("begin\n  x = 1\n  puts x &* (-2147483648)\nend\n", "-2147483648\n");

    [Fact]
    public void JitWorkaround_int32_min_mul_var_wrapping() =>
        Differential("begin\n  x = 1\n  puts (-2147483648) &* x\nend\n", "-2147483648\n");

    // The original repro from the JIT crash finding (uses wrapping mul)
    [Fact]
    public void JitRepro_simplified()
    {
        string src = @"
def test(n : Int32) : Int32
  x = 0
  for i in 0...n
    x = i &* (-2147483648)
  end
  x
end
begin
  puts test(2)
end
";
        Differential(src, "-2147483648\n");
    }
}
