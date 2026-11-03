using Alice.Cli.Commands;
using Alice.Cli.Proof;
using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Syntax;
using Spectre.Console;

namespace Alice.Tests;

/// <summary>Review round 1 (Fable), F6: `&+` inside `unchecked ... end` wraps its operands too, on both backends.</summary>
[Collection("console")]
public class FixFrontWrappingInUncheckedTests
{
    const string Src = "a = Int32::MaxValue\nunchecked\n  puts (a + 1) &+ 1\n  puts 1 &+ (a + 1)\nend\n" +
        "begin\n  puts (a + 1) &+ 1\nrescue e : OverflowException\n  puts \"ovf\"\nend\n";
    const string Expected = "-2147483647\n-2147483647\novf\n";

    [Fact]
    public void Wrapping_operator_inside_an_unchecked_region_does_not_recheck_operands_csharp() =>
        Assert.Equal(Expected, AliceCompiler.Run(Src).Replace("\r\n", "\n"));

    [Fact]
    public void Wrapping_operator_inside_an_unchecked_region_does_not_recheck_operands_il()
    {
        var res = AirToIl.Compile([new SourceFile("main.al", Src)], "w2", exe: true);
        Assert.True(res.Ok, string.Join("\n", res.Errors.Select(e => e.Message)));
        Assert.Equal(Expected, AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []).Replace("\r\n", "\n"));
    }
}

/// <summary>F3: `--backend il` runs the C# path\x27s diagnostic gate, so it rejects what the C# path rejects.</summary>
[Collection("console")]
public class FixFrontNullSafetyIlGateTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-ilgate-" + Guid.NewGuid().ToString("N"));

    public FixFrontNullSafetyIlGateTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    (int Code, string Out) Run(string backend, string src)
    {
        string path = Path.Combine(dir, "p" + Guid.NewGuid().ToString("N")[..8] + ".al");
        File.WriteAllText(path, src);
        var w = new StringWriter();
        var saved = AnsiConsole.Console;
        var (savedOut, savedErr) = (Console.Out, Console.Error);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w), ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        Console.SetOut(w);
        Console.SetError(w);
        try { return (Alice.Cli.App.Run(["--lang", "en", "run", "--backend", backend, path]), w.ToString()); }
        finally { AnsiConsole.Console = saved; Console.SetOut(savedOut); Console.SetError(savedErr); }
    }

    [Theory]
    // review-r1 n1.al, n3.al (nil flow, AL0310) and the constant overflow (AL0300)
    [InlineData("def len(s : String) : Int32\n  s.size\nend\ndef pick(flag : Bool) : String?\n  flag ? \"abc\" : nil\nend\ns = pick(false)\nputs len(s)\n", "AL0310")]
    [InlineData("class P\n  property name : String\n  def initialize(@name : String)\n  end\nend\np = P.new(\"a\")\nq : P? = nil\nputs q.name.size\n", "AL0310")]
    [InlineData("z = 0_u32 - 1_u32\nputs z\n", "AL0300")]
    public void Il_backend_rejects_what_the_csharp_path_rejects(string src, string code)
    {
        foreach (var backend in new[] { "csharp", "il" })
        {
            var (rc, output) = Run(backend, src);
            Assert.True(rc == 1, $"{backend}: exit {rc}\n{output}");
            Assert.Contains(code, output);
            Assert.DoesNotContain("NullReferenceException", output);
        }
    }

    [Fact]
    public void Il_backend_still_runs_a_clean_program()
    {
        var (rc, output) = Run("il", "s : String? = \"abc\"\nputs s.size if s\n");
        Assert.True(rc == 0, output);
        Assert.Contains("3", output);
    }
}

/// <summary>F8: `--smt` computes narrow ints at Int32 like the compiled code, and a proof is sample-checked.</summary>
[Collection("console")]
public class FixFrontProveSmtNarrowTests
{
    const string Rules = "rule i8wrap(a in -128..127)\n  x = a.to_i8\n  (x &+ 1.to_i8) <= 127.to_i8\nend\n" +
        "rule i8sum(a in 100..127)\n  x = a.to_i8\n  (x &+ x) < 0.to_i8\nend\n" +
        "rule u8wrap(a in 0..255)\n  x = a.to_u8\n  (x &+ 1.to_u8) >= 1.to_u8 || x == 255.to_u8\nend\n" +
        "rule i8narrow(a in -128..127)\n  x = a.to_i8\n  (x &+ 1.to_i8).to_i8! >= -128.to_i8\nend\n" +
        "rule i8neg(a in -128..127)\n  x = a.to_i8\n  -x != -128 || a == 128\nend\n";

    [Fact]
    public void Smt_agrees_with_enumeration_on_narrow_integers()
    {
        if (!Tooling.ProveZ3.Available()) return;
        var files = new List<SourceFile> { new("RULES.al", Rules) };
        var smt = SmtProver.Prove(files, []).ToDictionary(o => o.Rule);
        var exhaustive = Prove.Check(files, []).ToDictionary(r => r.Rule);
        foreach (var (rule, o) in smt)
        {
            Assert.Null(o.Untranslatable);
            Assert.True(o.Proved == exhaustive[rule].Proven, $"{rule}: smt proved={o.Proved} ({o.Counterexample}), enumeration proven={exhaustive[rule].Proven}");
        }
        Assert.False(smt["i8wrap"].Proved);
        Assert.False(smt["i8sum"].Proved);
        Assert.True(smt["u8wrap"].Proved);
        Assert.True(smt["i8neg"].Proved);   // -(-128) is Int32 128: no overflow
        var r = Prove.CheckSmt(files, []).ToDictionary(x => x.Rule);
        Assert.Equal("a = 127", r["i8wrap"].Counterexample);
        Assert.Equal("false", r["i8wrap"].Outcome);
    }

    [Fact]
    public void A_proof_the_compiled_rule_contradicts_is_never_reported_as_proven()
    {
        var files = new List<SourceFile> { new("RULES.al", "rule edge(a in 0..10, b : Bool)\n  a != 10 || b\nend\n") };
        var program = AliceCompiler.Load(AliceCompiler.Emit(AliceCompiler.TranspileAll(files, []), "ff_prove", false)).GetType("Program");
        var claimed = new Prove.RuleResult("edge", -1, null, 0, Smt: true);   // as if Z3 had said UNSAT
        var r = Prove.SampleCheck(program, claimed, [new RuleDomain { Name = "a", Lo = 0, Hi = 10 }, new RuleDomain { Name = "b", IsBool = true }]);
        Assert.False(r.Proven);
        Assert.Equal("a = 10, b = false", r.Counterexample);
        Assert.Contains("internal error", r.Outcome);
        var holds = Prove.SampleCheck(program, claimed with { Rule = "edge" }, [new RuleDomain { Name = "a", Lo = 0, Hi = 9 }, new RuleDomain { Name = "b", IsBool = true }]);
        Assert.True(holds.Proven);
    }
}
