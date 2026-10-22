using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// A shift uses only the low 5 (Int32) or 6 (Int64) bits of its count, so the + - * that compute a count are
/// modular (ADR 0013): wrapping changes no bit of the result and the count drops its overflow check.
/// </summary>
[Collection("console")]
public class ShiftCountTests
{
    const string Bits = "def bit(row : Int32, column : Int32, n : Int32) : Int32\n  1 << (row - column + n - 1)\nend\n";

    [Fact]
    public void Shift_count_arithmetic_is_unchecked()
        => Assert.Contains("1 << unchecked(((row - column) + n) - 1)", AliceCompiler.Transpile(Bits));

    [Fact]
    public void Shifted_value_stays_checked()
        => Assert.DoesNotContain("unchecked", AliceCompiler.Transpile("def f(a : Int32, b : Int32) : Int32\n  (a * b) << 2\nend\n"));

    [Fact]
    public void Calls_inside_a_count_keep_their_own_checks()
        => Assert.DoesNotContain("unchecked", AliceCompiler.Transpile("def g(x : Int32) : Int32\n  x\nend\ndef f(a : Int32, b : Int32) : Int32\n  1 << g(a * b)\nend\n"));

    [Fact]
    public void Wrapped_count_gives_the_same_bits()
        => Assert.Equal("2", AliceCompiler.Run(Bits + "puts bit(2147483647, -2147483648, 3)\n").Trim());
}
