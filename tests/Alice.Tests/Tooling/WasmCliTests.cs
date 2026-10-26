using Alice.Cli.Commands;
using Spectre.Console;

namespace Alice.Tests.Tooling;

/// <summary>The wasm workflow of the CLI (ADR 0023/0024) without building: arguments and usage errors.</summary>
[Collection("console")]
public class WasmCliTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-wasmcli-" + Guid.NewGuid().ToString("N"));

    public WasmCliTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"web\"\nkind = \"wasm\"\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static (int Code, string Out) Cli(params string[] args)
    {
        var w = new StringWriter();
        var saved = AnsiConsole.Console;
        var savedErr = Console.Error;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w), ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        Console.SetError(w);
        try { return (Alice.Cli.App.Run(["--lang", "en", .. args]), w.ToString()); }
        finally { AnsiConsole.Console = saved; Console.SetError(savedErr); }
    }

    [Fact]
    public void Native_build_of_a_wasm_package_is_a_usage_error()
    {
        var (code, output) = Cli("build", dir, "--native");
        Assert.Equal(2, code);
        Assert.Contains("WebAssembly", output);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Release_publish_compiles_ahead_of_time_when_the_workload_is_installed(bool release, bool workload, bool aot)
    {
        var args = WasmCommands.PublishArgs("web.alproj", "dist", release, workload);
        Assert.Equal(aot, args.Contains("-p:RunAOTCompilation=true"));
        Assert.Contains(release ? "Release" : "Debug", args);
    }

    [Fact]
    public void Ready_to_run_publishes_for_the_current_runtime()
    {
        var args = CompileCommands.ReadyToRunArgs("app.alproj", "bin/r2r");
        Assert.Contains("-p:PublishReadyToRun=true", args);
        Assert.Contains(CompileCommands.PortableRid(), args);
        Assert.Matches("^(linux|win|osx)-(x64|arm64|x86|arm)$", CompileCommands.PortableRid());
    }
}
