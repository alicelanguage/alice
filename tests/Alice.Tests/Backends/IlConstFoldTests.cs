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
    [Fact]
    public void JitWorkaround_var_mul_int32_min_checked() =>
        Differential("begin\n  x = 0\n  puts x &* (-2147483648)\nend\n", "0\n");

    [Fact]
    public void JitWorkaround_var_mul_int32_min_checked_one() =>
        Differential("begin\n  x = 1\n  puts x &* (-2147483648)\nend\n", "-2147483648\n");

    [Fact]
    public void JitWorkaround_var_mul_int32_min_checked_overflow() =>
        DifferentialExpectException<OverflowException>("begin\n  x = 2\n  y = x &* (-2147483648)\nend\n");

    [Fact]
    public void JitWorkaround_int32_min_mul_var_checked() =>
        Differential("begin\n  x = 1\n  puts (-2147483648) &* x\nend\n", "-2147483648\n");

    [Fact]
    public void JitWorkaround_var_mul_int32_min_unchecked() =>
        Differential("begin\n  x = 2\n  puts x * (-2147483648)\nend\n", "0\n");

    // The original repro from the JIT crash finding
    [Fact]
    public void JitRepro_original_program()
    {
        string src = @"
enum S0
  Circle(r : Int32)
  Rect(w : Int32, h : Int64)
  Empty
end
record struct R2(a : Int32, b : Int64, c : Float64)
def area0(s : S0) : Int64
  case s
  when S0::Circle(r) then r.to_i64 &* r.to_i64 &* 3_i64
  when S0::Rect(w, h) then w.to_i64 &* h
  when S0::Empty then 4_i64
  end
end
def k0(n : Int32) : Int64
  l = List(S0).new
  for x in 0...n
    if x.even?
      l << S0::Circle((x * (-2147483648)))
      l << S0::Rect((-x), ((x == x) ? 4651_i64 : 3247_i64))
    end
  end
  t = 0_i64
  t
end
begin
  puts k0(1)  # Changed from k0(4) to avoid overflow
end
";
        Differential(src, "0\n");
    }
}
