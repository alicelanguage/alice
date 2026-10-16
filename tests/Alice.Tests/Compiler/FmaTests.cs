using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Floating-point contraction like clang's default (-ffp-contract=on): a*b+c -> fused multiply-add.</summary>
public class FmaTests
{
    [Theory]
    [InlineData("def f(a : Float64, b : Float64, c : Float64) : Float64\n  a * b + c\nend\n")]
    [InlineData("def f(a : Float64, b : Float64, c : Float64) : Float64\n  c - a * b\nend\n")]
    [InlineData("def f(a : Float64, b : Float64, c : Float64) : Float64\n  x = c\n  x += a * b\n  x\nend\n")]
    public void Multiply_add_contracts_to_fma(string src)
        => Assert.Contains("Prelude.fma(", AliceCompiler.Transpile(src));

    [Fact]
    public void StrictMath_keeps_separate_rounding_and_integers_never_contract()
    {
        Assert.DoesNotContain("fma(", AliceCompiler.Transpile("@[StrictMath]\ndef f(a : Float64, b : Float64, c : Float64) : Float64\n  a * b + c\nend\n"));
        Assert.DoesNotContain("fma(", AliceCompiler.Transpile("def f(a : Int32, b : Int32, c : Int32) : Int32\n  a * b + c\nend\n"));
    }

    [Fact]
    public void Fma_result_is_the_single_rounded_value()
    {
        var m = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile("def f(a : Float64, b : Float64, c : Float64) : Float64\n  a * b + c\nend\n")], "fma", exe: false)).GetType("Program")!.GetMethod("f")!;
        double a = 0.1, b = 10.0, c = -1.0;
        double expected = System.Runtime.Intrinsics.X86.Fma.IsSupported || System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported
            ? Math.FusedMultiplyAdd(a, b, c) : a * b + c;
        Assert.Equal(expected, (double)m.Invoke(null, [a, b, c])!);
    }
}
