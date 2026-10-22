using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Value-range analysis (ADR 0014): an integer + - * whose operand ranges prove it cannot overflow drops its
/// overflow check. Masks, shifts, widened Int32 values and literals bound ranges; everything else is the full
/// type range, so a possibly overflowing operation stays checked.
/// </summary>
[Collection("console")]
public class RangeTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    const string Hash = "def h(x : Int32, seed : Int64) : Float64\n  z = (seed ^ (x.to_i64 * 0x45d9f3b)) & 0xFFFF_FFFF\n  z = ((z ^ (z >> 16)) * 0x45d9f3b) & 0xFFFF_FFFF\n  z = z ^ (z >> 16)\n  signed = z >= 2147483648 ? z - 4294967296 : z\n  signed.to_f\nend\n";

    [Fact]
    public void Masked_hash_arithmetic_is_unchecked()
    {
        var cs = AliceCompiler.Transpile(Hash);
        Assert.Contains("unchecked((z ^ (z >> 16)) * 0x45d9f3b)", cs);   // z is masked to [0, 2^32): the multiply cannot overflow
        Assert.Contains("unchecked((((long)x)) * 0x45d9f3b)", cs);   // Int32 widened times a literal: below 2^58
    }

    [Theory]
    [InlineData("def f(a : Int64, b : Int64) : Int64\n  a * b\nend\n")]                       // unknown ranges
    [InlineData("def f(a : Int64) : Int64\n  (a & 0xFFFF_FFFF) * 0x7FFF_FFFF_FFFF\nend\n")]      // 2^32 * 2^47 overflows
    [InlineData("def f(a : Int32) : Int32\n  b = a & 0xFF\n  b = a\n  b * 2\nend\n")]               // a later assignment widens b
    public void Possibly_overflowing_arithmetic_stays_checked(string src)
        => Assert.DoesNotContain("unchecked", AliceCompiler.Transpile(src));

    [Theory]
    [InlineData(0, 1234L)]
    [InlineData(-5, 99L)]
    [InlineData(int.MaxValue, long.MaxValue)]
    public void Range_proven_code_computes_the_same_values(int x, long seed)
    {
        long z = (seed ^ (x * 0x45d9f3bL)) & 0xFFFFFFFF;
        z = ((z ^ (z >> 16)) * 0x45d9f3b) & 0xFFFFFFFF;
        z ^= z >> 16;
        double expected = z >= 2147483648 ? z - 4294967296 : z;
        Assert.Equal(Alice.Runtime.Prelude.str(expected), Run(Hash + $"puts h({x}, {seed}_i64)"));
    }

    [Fact]
    public void Overflow_is_still_trapped_where_the_range_allows_it()
        => Assert.ThrowsAny<Exception>(() => Run("x = 4_000_000_000_i64\nm = x & 0x7FFF_FFFF_FFFF_FFFF\nputs m * m"));

    [Fact]
    public void Proven_sign_reinterpretation_is_one_sign_extension()
    {
        // z in [0, 2^32): `z >= 2^31 ? z - 2^32 : z` is exactly (long)(int)z, no data-dependent branch
        var cs = AliceCompiler.Transpile(Hash);
        Assert.Contains("((long)unchecked(((int)z)))", cs);
        Assert.DoesNotContain("4294967296", cs.Substring(cs.IndexOf("double h(")));
    }

    [Fact]
    public void Unproven_sign_reinterpretation_keeps_the_conditional()
        => Assert.DoesNotContain("unchecked((int)", AliceCompiler.Transpile("def f(z : Int64) : Int64\n  z >= 2147483648 ? z - 4294967296 : z\nend\n"));

    [Theory]
    [InlineData("z = 0xFFFF_FFFF_i64 & 4294967295\nputs(z >= 2147483648 ? z - 4294967296 : z)", "-1")]
    [InlineData("z = 2147483647_i64 & 0xFFFF_FFFF\nputs(z >= 2147483648 ? z - 4294967296 : z)", "2147483647")]
    [InlineData("z = 2147483648_i64 & 0xFFFF_FFFF\nputs(z >= 2147483648 ? z - 4294967296 : z)", "-2147483648")]
    [InlineData("b = 200 & 0xFF\nputs(b >= 128 ? b - 256 : b)", "-56")]
    [InlineData("b = 65535 & 0xFFFF\nputs(b < 32768 ? b : b - 65536)", "-1")]
    public void Sign_reinterpretation_values(string src, string expected)
        => Assert.Equal(expected, Run(src));
}
