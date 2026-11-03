using System.Diagnostics;
using Alice.Cli.Projects;
using Alice.Compiler;
using Alice.Compiler.Backend.Wasm;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>`alice build` / `alice run` on a `kind = "wasm"` package (ADR 0023, 0024; runtime = dotnet), and
/// <c>--target wasm32</c> on loose files: the native wasm backend (ADR 0034, runtime = alice).</summary>
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
            using var p = Process.Start(new ProcessStartInfo(Executables.Resolve("node"), "--version") { UseShellExecute = false, RedirectStandardOutput = true })!;
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
        var psi = new ProcessStartInfo(Executables.Resolve("node")) { UseShellExecute = false };
        psi.ArgumentList.Add(runner);
        foreach (var a in rest) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>`--target wasm32` (or `wasm32-wasi`): the native wasm backend instead of .NET.</summary>
    public static bool IsAliceTarget(string? target) => target is "wasm32" or "wasm32-wasi";

    sealed class AliceOptions
    {
        public List<string> Files = [];
        public string? Output;
        public string Runner = "";
        public bool PanicAbort, SourceMap;
        public List<string> Rest = [];
    }

    static AliceOptions ParseAlice(List<string> a)
    {
        var o = new AliceOptions();
        for (int i = 0; i < a.Count; i++)
        {
            switch (a[i])
            {
                case "--": o.Rest.AddRange(a.Skip(i + 1)); i = a.Count; break;
                case "--target": i++; break;
                case "-o": o.Output = i + 1 < a.Count ? a[++i] : throw new UsageException("-o needs a file"); break;
                case "--runner": o.Runner = i + 1 < a.Count ? a[++i] : throw new UsageException("--runner needs node or wasmtime"); break;
                case "--panic=abort": o.PanicAbort = true; break;
                case "--source-map": o.SourceMap = true; break;
                case "--release": break;   // the wasm backend has one optimization level
                default:
                    if (a[i].EndsWith(".al")) { UsageException.RequireFile(a[i]); o.Files.Add(a[i]); }
                    else o.Rest.Add(a[i]);
                    break;
            }
        }
        if (o.Files.Count == 0) throw new UsageException("--target wasm32 builds loose .al files", "alice build --target wasm32 main.al");
        if (o.Runner is not ("" or "node" or "wasmtime")) throw new UsageException($"unknown wasm runner `{o.Runner}`", "--runner node | wasmtime");
        return o;
    }

    /// <summary>Compiles to `name.wasm` beside its Node runner `name.mjs` (and `name.wasm.map` with --source-map).
    /// Returns false after printing the compile errors or the unsupported nodes.</summary>
    static bool CompileAlice(AliceOptions o, string wasmPath)
    {
        WasmCompileResult res;
        string map = Path.GetFileName(wasmPath) + ".map";
        try
        {
            var files = AliceCompiler.LoadWithRequires(o.Files);
            res = AirToWasm.Compile(files, new WasmOptions { PanicAbort = o.PanicAbort, SourceMapUrl = o.SourceMap ? map : null });
        }
        catch (CompileException e) { Console.Error.WriteLine(e.Message); return false; }
        if (!res.Ok)
        {
            foreach (var e in res.Errors.Take(20)) Console.Error.WriteLine("wasm: unsupported " + e.Message);
            Console.Error.WriteLine("wasm: " + res.Stats);
            return false;
        }
        string dir = Path.GetDirectoryName(Path.GetFullPath(wasmPath))!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(wasmPath, res.Wasm!);
        File.WriteAllText(Path.ChangeExtension(wasmPath, ".mjs"), Wasi.NodeRunner(wasmPath));
        if (res.SourceMap != null) File.WriteAllText(Path.Combine(dir, map), res.SourceMap);
        return true;
    }

    /// <summary>`alice build --target wasm32 a.al [-o out.wasm]`.</summary>
    public static int BuildAlice(List<string> a)
    {
        var sw = Stopwatch.StartNew();
        var o = ParseAlice(a);
        string wasm = o.Output ?? Path.ChangeExtension(o.Files[0], ".wasm");
        UiH.Status(T("compiling"), $"{Path.GetFileNameWithoutExtension(wasm)} (wasm32)");
        if (!CompileAlice(o, wasm)) return 1;
        UiH.Status(T("finished"), $"{Path.GetRelativePath(".", wasm)} {new FileInfo(wasm).Length / 1024.0:0.0} KB ({UiH.Elapsed(sw)})");
        return 0;
    }

    /// <summary>`alice run --target wasm32 a.al [-- args]` under Node (node:wasi), or wasmtime when Node is missing
    /// or with `--runner wasmtime`. The exit code is the program's.</summary>
    public static int RunAlice(List<string> a)
    {
        var o = ParseAlice(a);
        string runner = o.Runner != "" ? o.Runner : Has("node") ? "node" : Has("wasmtime") ? "wasmtime"
            : throw new UsageException("alice run --target wasm32 needs Node.js or wasmtime", "install node 22+ or wasmtime, or `alice build --target wasm32` and run the .wasm elsewhere");
        string dir = Path.Combine(Path.GetTempPath(), "alice-wasm32-" + Environment.ProcessId);
        string wasm = Path.Combine(dir, Path.GetFileNameWithoutExtension(o.Files[0]) + ".wasm");
        try
        {
            if (!CompileAlice(o, wasm)) return 1;
            UiH.Status(T("running"), Path.GetFileNameWithoutExtension(wasm));
            var psi = new ProcessStartInfo(Executables.Resolve(runner)) { UseShellExecute = false };
            if (runner == "node") { psi.ArgumentList.Add("--no-warnings"); psi.ArgumentList.Add(Path.ChangeExtension(wasm, ".mjs")); }
            else foreach (var x in new[] { "run", "-W", "exceptions=y", wasm }) psi.ArgumentList.Add(x);
            foreach (var r in o.Rest) psi.ArgumentList.Add(r);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    static bool Has(string exe)
    {
        try { Executables.Resolve(exe); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
