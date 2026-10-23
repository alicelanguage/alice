using System.Diagnostics;
using Alice.Cli.Projects;

namespace Alice.Tests.Integration;

/// <summary>`kind = "wasm"` (ADR 0023): an Alice package built for the browser, end to end. The bundle is run under
/// Node (the .NET WebAssembly runtime runs there too); skipped where Node is not installed.</summary>
[Trait("Category", "Integration")]
[Collection("msbuild")]
public class WasmTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-wasm-" + Guid.NewGuid().ToString("N"), "hello");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static (int Code, string Output) Node(string script, string cwd)
    {
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    static bool HasNode()
    {
        try { return Node("--version", ".").Code == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [Fact]
    public void Manifest_kind_wasm_generates_a_webassembly_project()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"hello\"\nkind = \"wasm\"\n");
        var xml = File.ReadAllText(Manifest.Sync(dir));
        Assert.Contains("Sdk=\"Microsoft.NET.Sdk.WebAssembly\"", xml);
        Assert.DoesNotContain("Sdk=\"Microsoft.NET.Sdk\"", xml);
        Assert.Contains("wwwroot/**", xml);
        Assert.DoesNotContain("ServerGarbageCollection>true", xml);   // one thread of GC in the browser
    }

    [Fact]
    public void New_wasm_package_builds_a_site_and_exports_alice_functions_to_javascript()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "new", dir, "--wasm"]));
        Assert.True(File.Exists(Path.Combine(dir, "wwwroot", "index.html")));
        Assert.True(File.Exists(Path.Combine(dir, "wwwroot", "main.js")));
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));
        string site = Path.Combine(dir, "dist", "wwwroot");
        Assert.True(File.Exists(Path.Combine(site, "index.html")));
        Assert.True(File.Exists(Path.Combine(site, "_framework", "dotnet.js")));
        if (!HasNode()) return;
        // the template's exported function, called from JavaScript; then its Main
        File.WriteAllText(Path.Combine(dir, "check.mjs"), """
            import { dotnet } from './dist/wwwroot/_framework/dotnet.js';
            const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
            const app = await getAssemblyExports(getConfig().mainAssemblyName);
            console.log('greet=' + app.App.greet('Alice'));
            await runMain();
            """);
        var (code, output) = Node("check.mjs", dir);
        Assert.True(code == 0, output);
        Assert.Contains("greet=Hello, Alice!", output);
        Assert.Contains("hello is running in WebAssembly", output);
    }
}
