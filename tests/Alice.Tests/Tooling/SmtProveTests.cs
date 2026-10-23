using Alice.Cli.Commands;
using Alice.Cli.Proof;
using Alice.Compiler;

namespace Alice.Tests.Tooling;

/// <summary>`alice prove --smt` (ADR 0022). Skipped where Z3 is not installed.</summary>
[Collection("console")]
public class SmtProveTests
{
    static bool HasZ3 => SmtProver.Unavailable() == null;

    static SmtProver.Outcome One(string rules, string app = "")
    {
        var files = new List<SourceFile> { new("RULES.al", rules) };
        if (app.Length > 0) files.Add(new("app.al", app));
        return Assert.Single(SmtProver.Prove(files, []));
    }

    [Theory]
    // whole-range properties enumeration can never cover (2^32 / 2^64 inputs)
    [InlineData("rule abs_of_square(x in -46340..46340)\n  (x * x) >= 0\nend\n")]
    [InlineData("rule xor_twice(a : Int32, b : Int32)\n  (a ^ b) ^ b == a\nend\n")]
    [InlineData("rule wrapping_add_commutes(a : Int64, b : Int64)\n  (a &+ b) == (b &+ a)\nend\n")]
    [InlineData("rule floored_mod_sign(a : Int32, b in -100..100)\n  return true if b == 0 || (a == Int32::MIN && b == -1)\n  r = a % b\n  b > 0 ? r >= 0 && r < b : r <= 0 && r > b\nend\n")]
    public void Proves_rules_over_every_input(string rule)
    {
        if (!HasZ3) return;
        var o = One(rule);
        Assert.Null(o.Untranslatable);
        Assert.True(o.Proved, o.Counterexample);
    }

    [Theory]
    // a counterexample must be a real input that makes the compiled rule fail
    [InlineData("rule add_overflows(a : Int32, b : Int32)\n  a + b - b == a\nend\n", "overflow")]                       // checked + raises
    [InlineData("rule truncated_mod(a : Int32)\n  return true if a >= 0\n  a % 3 <= 0\nend\n", "floored")]                    // -1 % 3 == 2
    [InlineData("rule min_div(a : Int32, b : Int32)\n  return true if b == 0\n  (a / b) * b <= a || true\nend\n", "MIN/-1")]  // raises
    [InlineData("rule div_zero(a in 0..10, b in 0..10)\n  a / b >= 0\nend\n", "zero")]
    [InlineData("rule wrong(x in 0..1000)\n  x * 3 != 999\nend\n", "false")]
    public void Finds_counterexamples_and_they_are_real(string rule, string kind)
    {
        if (!HasZ3) return;
        var o = One(rule);
        Assert.Null(o.Untranslatable);
        Assert.False(o.Proved, kind);
        Assert.NotNull(o.Counterexample);
        // replay the counterexample through the compiled rule: it must return false or raise
        var assigns = o.Counterexample!.Split(", ").Select(p => p.Split(" = ")).ToDictionary(p => p[0], p => long.Parse(p[1]));
        var src = "def __replay : Bool\n  begin\n    " + Call(rule, assigns) + "\n  rescue e\n    false\n  end\nend\nputs __replay\n";
        Alice.Compiler.Backend.Emitter.SkipOversizedRules = true;   // the rule itself may be too large to enumerate
        try { Assert.Equal("false", AliceCompiler.Run(rule + src).Trim()); }
        finally { Alice.Compiler.Backend.Emitter.SkipOversizedRules = false; }
    }

    static string Call(string rule, Dictionary<string, long> args)
    {
        var name = rule.Split('(')[0]["rule ".Length..].Trim();
        var ps = rule[(rule.IndexOf('(') + 1)..rule.IndexOf(')')].Split(',').Select(p => p.Trim().Split(' ')[0]);
        return $"rule_{name}({string.Join(", ", ps.Select(p => args[p] + (rule.Contains($"{p} : Int64") ? "_i64" : "")))})";
    }

    [Fact]
    public void Agrees_with_enumeration_on_finite_domains()
    {
        // every rule of the repository plus a failing one: --smt and exhaustion must reach the same verdict
        if (!HasZ3) return;
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "RULES.al"))) dir = Path.GetDirectoryName(dir)!;
        var files = new List<SourceFile> { new("RULES.al", File.ReadAllText(Path.Combine(dir, "RULES.al")) + "rule bad(x in -50..50)\n  x * x != 49\nend\n") };
        var smt = Prove.CheckSmt(files, []);
        var exhaustive = Prove.Check(files, []);
        foreach (var e in exhaustive)
        {
            var s = smt.Single(r => r.Rule == e.Rule);
            Assert.Equal(e.Counterexample == null, s.Counterexample == null);
        }
        Assert.Contains(smt, r => r.Rule == "bad" && r.Counterexample != null);
    }

    [Fact]
    public void Inlines_pure_methods_and_reports_what_it_cannot_translate()
    {
        if (!HasZ3) return;
        var app = "def double(x : Int32) : Int32\n  x &* 2\nend\n";
        Assert.True(One("rule double_even(x : Int32)\n  double(x) % 2 == 0\nend\n", app).Proved);
        var loop = One("rule loops(x in 0..10)\n  t = 0\n  while t < x\n    t += 1\n  end\n  t == x\nend\n");
        Assert.Contains("not supported by --smt", loop.Untranslatable);
    }

    [Fact]
    public void Full_range_rules_are_too_large_to_enumerate_and_say_so()
    {
        // 2^64 inputs (a full Int32 range is 2^32, exactly what enumeration still covers)
        var ex = Assert.Throws<CompileException>(() => Prove.Check([new("RULES.al", "rule x_ok(x : Int64)\n  x == x\nend\n")], []));
        Assert.Contains("alice prove --smt", ex.Message);
    }
}
