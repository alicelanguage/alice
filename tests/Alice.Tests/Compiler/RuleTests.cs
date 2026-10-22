using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// RULES.al (0.12.2), Alice's take on Bend's LAWS.bend: `rule` declares a property over a finite,
/// explicit domain. Rules are pure, so checking every case is a proof by exhaustion, not a test sample.
/// </summary>
[Collection("console")]
public class RuleTests
{
    const string Program = "def clamp(x : Int32, lo : Int32, hi : Int32) : Int32\n  x < lo ? lo : (x > hi ? hi : x)\nend\n";

    static (long Cases, string? Counterexample) Prove(string src, string rule)
    {
        var asm = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "rules", exe: false));
        var r = asm.GetType("Program")!.GetMethod("__prove_" + rule)!.Invoke(null, null)!;
        var t = (System.Runtime.CompilerServices.ITuple)r;
        return ((long)t[0]!, (string?)t[1]);
    }

    [Fact]
    public void A_true_rule_is_proven_over_its_whole_domain()
    {
        var (cases, cex) = Prove(Program + "rule clamp_in_range(x in -500..500, lo in -10..10)\n  c = clamp(x, lo, lo + 5)\n  c >= lo && c <= lo + 5\nend\n", "clamp_in_range");
        Assert.Null(cex);
        Assert.Equal(1001L * 21, cases);
    }

    [Fact]
    public void A_false_rule_yields_a_counterexample()
    {
        var (_, cex) = Prove(Program + "rule clamp_is_identity(x in 0...100, b : Bool)\n  clamp(x, 0, 50) == x || b\nend\n", "clamp_is_identity");
        Assert.NotNull(cex);
        Assert.Matches(@"x = (5[1-9]|[6-9]\d), b = False", cex);
    }

    [Fact]
    public void A_rule_that_raises_fails()
    {
        var (_, cex) = Prove("rule div(x in -3..3)\n  10 / x > -100\nend\n", "div");
        Assert.Contains("x = 0", cex);
        Assert.Contains("DivideByZeroException", cex);
    }

    [Theory]
    [InlineData("rule loud(x in 0..3)\n  puts x\n  true\nend\n", "AL0210")]              // rules are pure: no I/O, so they are deterministic
    [InlineData("rule huge(a in 0..100000, b in 0..100000)\n  a + b >= 0\nend\n", "AL0214")]   // 10^10 cases: not provable by exhaustion
    public void Rules_must_be_pure_and_finite(string src, string code)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal(code, ex.Diagnostics[0].Code);
    }
}
