using Alice.Cli.Projects;

namespace Alice.Tests.Tooling;

/// <summary>Target-specific build settings from alice.toml (ADR 0024): wasm, NativeAOT, ReadyToRun, single-threaded.</summary>
public class TargetManifestTests
{
    static string Sync(string toml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), toml);
        return File.ReadAllText(Manifest.Sync(dir));
    }

    [Fact]
    public void Wasm_is_single_threaded_and_release_is_trimmed_simd_and_invariant()
    {
        var xml = Sync("[package]\nname = \"web\"\nkind = \"wasm\"\n");
        Assert.Contains("<AliceSequential>true</AliceSequential>", xml);
        Assert.Contains("Condition=\"'$(Configuration)' == 'Release'\"", xml);
        Assert.Contains("<WasmEnableSIMD>true</WasmEnableSIMD>", xml);
        Assert.Contains("<PublishTrimmed>true</PublishTrimmed>", xml);
        Assert.Contains("<TrimMode>full</TrimMode>", xml);
        Assert.Contains("<InvariantGlobalization>true</InvariantGlobalization>", xml);
    }

    [Fact]
    public void Wasm_keeps_culture_globalization_when_asked_for()
    {
        var xml = Sync("[package]\nname = \"web\"\nkind = \"wasm\"\n\n[runtime]\nglobalization = \"culture\"\n");
        Assert.DoesNotContain("InvariantGlobalization", xml);
    }

    [Fact]
    public void Exe_is_not_sequential_unless_threads_are_off()
    {
        Assert.DoesNotContain("AliceSequential", Sync("[package]\nname = \"app\"\n"));
        Assert.Contains("<AliceSequential>true</AliceSequential>", Sync("[package]\nname = \"app\"\n\n[build]\nthreads = false\n"));
    }

    [Fact]
    public void Native_aot_folds_identical_bodies_and_checks_aot_compatibility()
    {
        var xml = Sync("[package]\nname = \"app\"\n\n[build]\nnative = true\n");
        Assert.Contains("<PublishAot>true</PublishAot>", xml);
        Assert.Contains("<IsAotCompatible>true</IsAotCompatible>", xml);
        Assert.Contains("<IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>", xml);
    }

    [Fact]
    public void Ready_to_run_is_opt_in()
    {
        Assert.DoesNotContain("PublishReadyToRun", Sync("[package]\nname = \"app\"\n"));
        Assert.Contains("<PublishReadyToRun>true</PublishReadyToRun>", Sync("[package]\nname = \"app\"\n\n[build]\nready_to_run = true\n"));
    }

    [Fact]
    public void Gen_sequential_emits_no_thread_pool_code()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-genseq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "main.al");
        File.WriteAllText(src, "def fib(n : Int32) : Int64\n  if n < 2\n    n.to_i64\n  else\n    a = fib(n - 1)\n    b = fib(n - 2)\n    a + b\n  end\nend\nputs fib(20)\n");
        Assert.Equal(0, Alice.Cli.Commands.CompileCommands.Gen(["--no-check", "--sequential", "-o", Path.Combine(dir, "out"), src]));
        Assert.DoesNotContain("Alice.Runtime.Par.", File.ReadAllText(Path.Combine(dir, "out", "main.al.g.cs")));
        Assert.Equal("workstation", File.ReadAllText(Path.Combine(dir, "out", "alice.gc")));
    }
}
