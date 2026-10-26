using System.Diagnostics;
using Alice.Cli.Projects;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>`alice build` / `alice run` on a `kind = "wasm"` package (ADR 0023, 0024).</summary>
public static class WasmCommands
{
    /// <summary>The published site: dist/wwwroot is deployable as is.</summary>
    public static string Dist(string proj) => Path.Combine(ProjectFiles.PackageDir(proj), "dist");

    /// <summary>`dotnet publish` arguments. Release builds are trimmed with SIMD (alice.toml's generated project), and
    /// compiled ahead of time to WebAssembly when the wasm-tools workload is installed (interpreted IL otherwise).</summary>
    public static List<string> PublishArgs(string proj, string dist, bool release, bool aotWorkload)
    {
        var args = new List<string> { "publish", proj, "-c", release ? "Release" : "Debug", "-o", dist, "-v", "q", "-nologo", "-clp:NoSummary" };
        if (release && aotWorkload) args.Add("-p:RunAOTCompilation=true");
        return args;
    }

    /// <summary>Whether `dotnet workload install wasm-tools` was run (needed for AOT and native relinking).</summary>
    static bool HasAotWorkload()
    {
        var r = DotNet.Run(["workload", "list"]);
        return r.ExitCode == 0 && r.Output.Any(l => l.TrimStart().StartsWith("wasm-tools ", StringComparison.Ordinal));
    }

    public static int Build(string proj, bool release, Stopwatch sw)
    {
        string name = Path.GetFileNameWithoutExtension(proj);
        string dist = Dist(proj);
        bool aot = release && HasAotWorkload();
        var r = UiH.Spin($"{T("building")} {name}{(aot ? " (AOT)" : "")}…", () => DotNet.Run(PublishArgs(proj, dist, release, aot)));
        if (r.ExitCode != 0 || r.Diagnostics.Any(d => d.Severity == "error"))
        {
            CompileCommands.Report(r);
            return r.ExitCode == 0 ? 1 : r.ExitCode;
        }
        UiH.Status(T("finished"), $"{Path.GetRelativePath(".", Path.Combine(dist, "wwwroot"))} ({UiH.Elapsed(sw)})");
        CompileCommands.RecordLock(proj);
        return 0;
    }

    /// <summary>Checked before building: a missing Node should not cost a full publish first.</summary>
    public static void RequireNode()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", "--version") { UseShellExecute = false, RedirectStandardOutput = true })!;
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new UsageException("alice run on a wasm package needs Node.js", "install node, or `alice build` and serve dist/wwwroot with any static web server");
        }
    }

    /// <summary>The bundle under Node, which runs the same .NET WebAssembly runtime; arguments pass through.</summary>
    public static int Run(string proj, List<string> rest)
    {
        string runner = Path.Combine(ProjectFiles.PackageDir(proj), ".alice", "run.mjs");
        File.WriteAllText(runner, WasmTemplates.NodeRunner(Path.Combine(Dist(proj), "wwwroot", "_framework", "dotnet.js")));
        var psi = new ProcessStartInfo("node") { UseShellExecute = false };
        psi.ArgumentList.Add(runner);
        foreach (var a in rest) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
