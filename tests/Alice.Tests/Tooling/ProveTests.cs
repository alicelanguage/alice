using Alice.Cli.Commands;
using Alice.Compiler;

namespace Alice.Tests.Tooling;

/// <summary>`alice prove`: runs every rule of RULES.al and keeps PROOF.al as a certificate tied to the sources.</summary>
[Collection("console")]
public class ProveTests
{
    static readonly List<SourceFile> Files =
    [
        new("app.al", "def double(x : Int32) : Int32\n  x * 2\nend\n"),
        new("RULES.al", "rule double_is_even(x in -1000..1000)\n  double(x) % 2 == 0\nend\nrule double_grows(x in -5..5)\n  double(x) >= x\nend\n"),
    ];

    [Fact]
    public void Runs_every_rule_and_reports_counterexamples()
    {
        var results = Prove.Check(Files, []);
        Assert.Equal(["double_grows", "double_is_even"], results.Select(r => r.Rule).Order());
        Assert.Equal(2001, results.Single(r => r.Rule == "double_is_even").Cases);
        Assert.Null(results.Single(r => r.Rule == "double_is_even").Counterexample);
        // the first failing case in domain order, whatever thread finds a failure first: PROOF.al must be reproducible
        for (int i = 0; i < 20; i++)
            Assert.Equal("x = -5", Prove.Check(Files, []).Single(r => r.Rule == "double_grows").Counterexample);
    }

    [Fact]
    public void Certificate_is_deterministic_and_tied_to_the_sources()
    {
        var results = Prove.Check(Files, []);
        string a = Prove.Certificate(Files, results), b = Prove.Certificate(Files, Prove.Check(Files, []));
        Assert.Equal(a, b);
        Assert.Contains("# rule double_is_even: proven over 2001 cases", a);
        Assert.Contains("# rule double_grows: FAILS", a);
        var edited = new List<SourceFile> { Files[0] with { Text = "def double(x : Int32) : Int32\n  x + x\nend\n" }, Files[1] };
        Assert.NotEqual(Prove.Hash(Files), Prove.Hash(edited));   // any code change makes PROOF.al stale
    }

    [Fact]
    public void Repository_rules_hold_and_PROOF_al_is_current()
    {
        // RULES.al pins Alice's numeric semantics (ADR 0013); a compiler change that breaks one fails here
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "RULES.al"))) dir = Path.GetDirectoryName(dir)!;
        var rules = new List<SourceFile> { new("RULES.al", File.ReadAllText(Path.Combine(dir, "RULES.al"))) };
        var results = Prove.Check(rules, []);
        Assert.All(results, r => Assert.Null(r.Counterexample));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, Prove.ProofFile)).Replace("\r\n", "\n"), Prove.Certificate(rules, results));
    }
}
