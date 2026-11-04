using System.Text.Json;
using Alice.Cli.Commands;
using Alice.Compiler;
using Spectre.Console;

namespace Alice.Tests.Tooling;

/// <summary>
/// `alice prove` for CI and agents: domains at the integer limits, `--timeout`, `--format json`, stable order, the
/// `rule X FAILS: ... -> ...` line, `alice build --prove` / `[build] prove`, and Z3 counterexamples replayed.
/// </summary>
[Collection("console")]
public class ProveCliTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-prove-" + Guid.NewGuid().ToString("N"));
    readonly string cwd = Directory.GetCurrentDirectory();

    public ProveCliTests()
    {
        Directory.CreateDirectory(dir);
        Directory.SetCurrentDirectory(dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(cwd);
        try { Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static (int Code, string Out) Cli(params string[] args)
    {
        var w = new StringWriter();
        var saved = AnsiConsole.Console;
        var (savedOut, savedErr) = (Console.Out, Console.Error);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w), ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        Console.SetOut(w);
        Console.SetError(w);
        try { return (Alice.Cli.App.Run(["--lang", "en", .. args]), w.ToString()); }
        finally { AnsiConsole.Console = saved; Console.SetOut(savedOut); Console.SetError(savedErr); }
    }

    const string Mixed = "def double(x : Int32) : Int32\n  x * 2\nend\n" +
        "rule zeta_grows(x in -5..5)\n  double(x) >= x\nend\n" +
        "rule alpha_even(x in -100..100)\n  double(x) % 2 == 0\nend\n" +
        "rule beta_div(x in 0..3)\n  10 / x > 0\nend\n";

    [Fact]
    public void Domains_ending_at_the_integer_limits_are_enumerated_not_crashed()
    {
        // audit: `for (int v = Lo; v <= Hi; v++)` overflowed at Hi == Int32::MAX and escaped the per-case try
        var rules = "rule top32(a in 2147483640..2147483647, b in 2147483646..2147483647)\n  a > 0 && b > 0\nend\n" +
            "rule bottom32(a in -2147483648..-2147483645)\n  a < 0\nend\n" +
            "rule top64(x in 9223372036854775800..9223372036854775807, y in 9223372036854775806..9223372036854775807)\n  x > 0 && y > 0\nend\n" +
            "rule last_fails(a in 0..2, b in 2147483645..2147483647)\n  b != 2147483647\nend\n";
        var r = Prove.Check([new SourceFile("RULES.al", rules)], []).ToDictionary(x => x.Rule);
        Assert.Equal((16L, (string?)null), (r["top32"].Cases, r["top32"].Counterexample));
        Assert.Equal((4L, (string?)null), (r["bottom32"].Cases, r["bottom32"].Counterexample));
        Assert.Equal((16L, (string?)null), (r["top64"].Cases, r["top64"].Counterexample));
        Assert.Equal("a = 0, b = 2147483647", r["last_fails"].Counterexample);   // the very last inner value is reached
    }

    [Fact]
    public void Domain_sizes_are_exact_at_the_Int64_limits()
    {
        // through double, both bounds rounded to 2^63 and the range read as one case
        Assert.Equal(8, new Alice.Compiler.Syntax.RuleDomain { Lo = long.MaxValue - 7, Hi = long.MaxValue }.Size);
        Assert.Equal(3, new Alice.Compiler.Syntax.RuleDomain { Lo = long.MinValue, Hi = long.MinValue + 2 }.Size);
        Assert.Equal(long.MaxValue, new Alice.Compiler.Syntax.RuleDomain { Lo = long.MinValue, Hi = long.MaxValue }.Size);   // `x : Int64` saturates
    }

    [Fact]
    public void Results_are_in_ordinal_order_and_failures_print_one_line_each()
    {
        var r = Prove.Check([new SourceFile("RULES.al", Mixed)], []);
        Assert.Equal(["alpha_even", "beta_div", "zeta_grows"], r.Select(x => x.Rule));
        Assert.Equal("rule zeta_grows FAILS: x = -5 -> false", r[2].FailureLine);
        Assert.Equal("rule beta_div FAILS: x = 0 -> raised DivideByZeroException", r[1].FailureLine);
        Assert.Equal(["proven", "fails", "fails"], r.Select(x => x.Status));
    }

    [Fact]
    public void A_time_limit_stops_a_rule_without_a_verdict()
    {
        // 2^32 cases of a 1000-step loop: hours of work, stopped after a fraction of a second
        var big = "def spin(a : Int32, b : Int32) : Int32\n  s = a\n  i = 0\n  while i < 1000\n    s = (s &* 31) ^ b\n    i += 1\n  end\n  s\nend\nrule slow(a in 0..65535, b in 0..65535)\n  spin(a, b) != a || true\nend\n";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Assert.Single(Prove.Check([new SourceFile("RULES.al", big)], [], TimeSpan.FromMilliseconds(300)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        Assert.Equal("unknown", r.Status);
        Assert.Null(r.Counterexample);
        Assert.Contains("time limit of 0.3s", r.NotProven);
        Assert.Contains("# rule slow: not proven (time limit", Prove.Certificate([new SourceFile("RULES.al", big)], [r]));
    }

    [Fact]
    public void Json_summary_is_machine_readable_and_the_exit_code_fails()
    {
        File.WriteAllText("RULES.al", Mixed);
        var (code, json) = Cli("prove", "--format", "json");
        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("written", root.GetProperty("proof").GetString());
        var rules = root.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(["alpha_even", "beta_div", "zeta_grows"], rules.Select(x => x.GetProperty("rule").GetString()));
        Assert.Equal(201, rules[0].GetProperty("cases").GetInt64());
        Assert.Equal("proven", rules[0].GetProperty("status").GetString());
        Assert.Equal(-5, rules[2].GetProperty("counterexample").GetProperty("x").GetInt64());
        Assert.Equal("false", rules[2].GetProperty("result").GetString());
        Assert.Equal("raised DivideByZeroException", rules[1].GetProperty("result").GetString());
        Assert.Equal("rule zeta_grows FAILS: x = -5 -> false", rules[2].GetProperty("message").GetString());

        // all proven and PROOF.al current: exit 0; then an edit makes it stale: exit 1 and "stale"
        File.WriteAllText("RULES.al", "rule ok(x in 0..9)\n  x >= 0\nend\n");
        Assert.Equal(0, Cli("prove").Code);
        var (ok, okJson) = Cli("prove", "--check", "--format=json");
        Assert.Equal(0, ok);
        Assert.Equal("current", JsonDocument.Parse(okJson).RootElement.GetProperty("proof").GetString());
        File.AppendAllText("RULES.al", "# edited\n");
        var (stale, staleJson) = Cli("prove", "--check", "--format", "json");
        Assert.Equal(1, stale);
        Assert.Equal("stale", JsonDocument.Parse(staleJson).RootElement.GetProperty("proof").GetString());
    }

    [Fact]
    public void Text_output_prints_the_counterexample_line()
    {
        File.WriteAllText("RULES.al", Mixed);
        var (code, text) = Cli("prove");
        Assert.Equal(1, code);
        Assert.Contains("rule zeta_grows FAILS: x = -5 -> false", text);
        Assert.Contains("rule beta_div FAILS: x = 0 -> raised DivideByZeroException", text);
    }

    [Theory]
    [InlineData(new[] { "prove", "--timeout", "soon" }, "--timeout soon is not a number of seconds")]
    [InlineData(new[] { "prove", "--timeout" }, "--timeout needs a value")]
    [InlineData(new[] { "prove", "--format", "xml" }, "--format xml is not a format")]
    [InlineData(new[] { "build", "RULES.al", "--prove" }, "--prove checks a package's RULES.al")]
    public void Bad_flags_are_usage_errors(string[] args, string message)
    {
        File.WriteAllText("RULES.al", "rule ok(x in 0..9)\n  x >= 0\nend\n");
        var (code, text) = Cli(args);
        Assert.Equal(2, code);
        Assert.Contains(message, text.Replace("\n", " "));
    }

    [Fact]
    public void Build_proves_when_asked_or_configured_and_RULES_al_exists()
    {
        File.WriteAllText("alice.toml", "[package]\nname = \"p\"\n");
        Assert.Null(CompileCommands.ProveSetting("."));
        Assert.Equal(0, CompileCommands.ProveAfterBuild(".", null));       // not asked: nothing runs
        Assert.Equal(0, CompileCommands.ProveAfterBuild(".", "enumerate")); // asked, but no RULES.al: nothing to prove
        File.WriteAllText("alice.toml", "[package]\nname = \"p\"\n\n[build]\nprove = true\n");
        Assert.Equal("enumerate", CompileCommands.ProveSetting("."));
        File.WriteAllText("alice.toml", "[package]\nname = \"p\"\n\n[build]\nprove = \"smt\"\n");
        Assert.Equal("smt", CompileCommands.ProveSetting("."));
        File.WriteAllText("alice.toml", "[package]\nname = \"p\"\n\n[build]\nprove = true\n");

        File.WriteAllText("RULES.al", "rule ok(x in 0..9)\n  x >= 0\nend\n");
        Assert.Equal(1, Cli("prove", "--check").Code);                     // no PROOF.al yet
        Assert.Equal(1, CompileCommands.ProveAfterBuild(".", null));        // so the build fails
        Assert.Equal(0, Cli("prove").Code);
        Assert.Equal(0, CompileCommands.ProveAfterBuild(".", null));
        File.WriteAllText("RULES.al", "rule bad(x in 0..9)\n  x > 0\nend\n");
        Assert.Equal(1, CompileCommands.ProveAfterBuild(".", null));        // a failing rule fails the build
        Assert.Equal(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Smt_counterexamples_are_replayed_through_the_compiled_rule()
    {
        if (!ProveZ3.Available()) return;
        var files = new List<SourceFile> { new("RULES.al", "rule add_overflows(a : Int32, b : Int32)\n  a + b - b == a\nend\nrule wrong(x in 0..1000)\n  x * 3 != 999\nend\nrule fine(a : Int32)\n  (a ^ 5) ^ 5 == a\nend\n") };
        var r = Prove.CheckSmt(files, []).ToDictionary(x => x.Rule);
        Assert.Equal("raised OverflowException", r["add_overflows"].Outcome);
        Assert.Equal("rule wrong FAILS: x = 333 -> false", r["wrong"].FailureLine);
        Assert.True(r["fine"].Proven);
        Assert.Equal(["add_overflows", "fine", "wrong"], Prove.CheckSmt(files, []).Select(x => x.Rule));
    }
}

/// <summary>`alice agents --write`: the AGENTS.md block tells an agent to gate commits on the rules and how to read a counterexample.</summary>
public class ProveAgentsTests
{
    [Fact]
    public void Agents_block_explains_the_prove_gate_and_the_counterexample_loop()
    {
        string block = GuideCommand.MergeAgents(null);
        foreach (var must in new[] { "alice prove --check", "before committing", "FAILS: x = 7, lo = -3 -> false", "-> raised OverflowException", "Loop:", "PROOF.al", "--format json", "alice build --prove" })
            Assert.Contains(must, block.Replace("\n  ", " "));
    }
}
