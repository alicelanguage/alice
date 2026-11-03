using System.Diagnostics;
using Alice.Compiler;
using Alice.Fuzz;
using Xunit.Abstractions;

namespace Alice.Tests.Fuzz;

/// <summary>
/// Differential fuzzer, short deterministic seed set: every generated program runs under the C# backend and the IL
/// backend (ADR 0033) with the same stdout and the same escaping exception, and its IL image passes ILVerify. Two
/// variants run in other processes (the switches are read once per process): `ALICE_PAR=0` against the parallel
/// default (bit-identical: the generated programs are deterministic and their parallel code is pure), and
/// `ALICE_DISABLE_PASSES` with every MIR pass off (`alice run -q`).
/// </summary>
[Collection("console")]
public class DifferentialFuzzTests(ITestOutputHelper output)
{
    static Oracles.Compiled Compile(string src)
    {
        var cs = AliceCompiler.TranspileAll([new SourceFile("main.al", src)]);
        return new(cs, AliceCompiler.Emit(cs, "fuzz", exe: true));
    }

    [Fact]
    public void Csharp_and_il_backends_agree_on_generated_programs()
    {
        var stats = new Oracles.DiffStats();
        var known = new Dictionary<string, int>();
        for (int seed = 1; seed <= 14; seed++)
        {
            var src = TypedGen.Generate(seed, avoidKnown: true);
            var f = Oracles.CheckDifferential(src, Compile(src), stats);
            ParserFuzzTests.Check(f, s =>
            {
                if (!Shrink.Terminates(s) || !ShrinkTyping.WellTyped(s)) return null;
                try { return Oracles.CheckDifferential(s, Compile(s), new Oracles.DiffStats()); }
                catch (CompileException) { return null; }
            }, known);
        }
        output.WriteLine($"programs {stats.Programs}, compared {stats.Compared}, IL-unsupported {stats.IlUnsupported}: "
            + string.Join(" | ", stats.Unsupported.Select(kv => kv.Key + " x" + kv.Value)));
        Assert.True(stats.Compared >= 6, $"most programs should be compared (compared {stats.Compared} of {stats.Programs})");
    }

    /// <summary>Programs with parallel code (`par for`, `par a, b =`) and plain ones: ALICE_PAR=0 must print the same.</summary>
    [Fact]
    public void Serial_and_parallel_runs_are_bit_identical()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-fuzz-par-" + Guid.NewGuid().ToString("N"));
        try
        {
            int parallel = 0;
            for (int seed = 1; seed <= 40 && parallel < 3; seed++)
            {
                var gen = new TypedGen(new Rng(seed), avoidKnown: true);
                var src = gen.Program();
                if (!gen.Features.Contains("par-for") && !gen.Features.Contains("par-let")) continue;
                var c = Compile(src);
                if (!AliceCompiler.UsesParallel(c.CSharp)) continue;
                parallel++;
                string inProc = Oracles.NormalizeInProcess(Oracles.Execute(() => c.Image).Output);
                var dll = Oracles.Deploy(dir, "p" + seed, c.Image);
                var psi = new ProcessStartInfo("dotnet");
                psi.ArgumentList.Add(dll);
                psi.Environment["ALICE_PAR"] = "0";
                string serial = Oracles.NormalizeProcess(Oracles.RunProcess(psi, 120_000));
                Assert.True(inProc == serial, $"seed {seed}: ALICE_PAR=0 differs\n--- parallel:\n{inProc}\n--- serial:\n{serial}\n--- program:\n{src}");
            }
            output.WriteLine($"{parallel} parallel programs compared");
            Assert.True(parallel > 0, "no generated program used parallel code");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void Disabling_every_mir_pass_keeps_the_output()
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "alice.dll");
        Assert.True(File.Exists(cli), "alice.dll next to the tests");
        var dir = Path.Combine(Path.GetTempPath(), "alice-fuzz-passes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (int seed in new[] { 3, 8 })
            {
                var src = TypedGen.Generate(seed, avoidKnown: true);
                string baseline = Oracles.NormalizeInProcess(Oracles.Execute(() => Compile(src).Image).Output);
                var file = Path.Combine(dir, "p" + seed + ".al");
                File.WriteAllText(file, src);
                var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir };
                psi.ArgumentList.Add(cli);
                psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add(file);
                psi.Environment["ALICE_DISABLE_PASSES"] = string.Join(",", Oracles.Passes);
                psi.Environment["ALICE_NO_CACHE"] = "1";
                psi.Environment["ALICE_TELEMETRY"] = "0";
                string off = Oracles.NormalizeProcess(Oracles.RunProcess(psi, 180_000));
                Assert.True(baseline == off, $"seed {seed}: passes off differs\n--- default:\n{baseline}\n--- passes off:\n{off}\n--- program:\n{src}");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
