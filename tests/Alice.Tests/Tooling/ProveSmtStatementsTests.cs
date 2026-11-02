using Alice.Cli.Commands;
using Alice.Cli.Proof;
using Alice.Compiler;

namespace Alice.Tests.Tooling;

/// <summary>`--smt` executes every statement of a rule: a discarded call that raises fails the rule (audit F14).</summary>
[Collection("console")]
public class ProveSmtStatementsTests
{
    const string Rule = "def inv(x : Int32) : Int32\n  10 / x\nend\nrule discarded(x in -5..5)\n  inv(x)\n  true\nend\n";

    [Fact]
    public void Enumeration_fails_the_rule()
    {
        var r = Assert.Single(Prove.Check([new SourceFile("RULES.al", Rule)], []));
        Assert.NotNull(r.Counterexample);
    }

    [Fact]
    public void Smt_agrees_with_enumeration()
    {
        if (SmtProver.Unavailable() != null) return;   // Z3 not installed
        var o = Assert.Single(SmtProver.Prove([new SourceFile("RULES.al", Rule)], []));
        Assert.False(o.Proved, "a discarded call that divides by zero was proven");
    }
}
