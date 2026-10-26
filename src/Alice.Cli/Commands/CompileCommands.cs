using System.Diagnostics;
using Alice.Cli.Projects;
using Alice.Compiler;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>build / run / check / emit / gen — single files compile in-process; projects go through MSBuild.</summary>
public static class CompileCommands
{
    sealed class Options
    {
        public List<string> Files = new();
        public List<string> Refs = new();
        public string? Output;
        public bool Release;
        public bool Native;
        public string? RefsFile;
        public string? AnalyzersFile;   // MSBuild @(Analyzer): source generators run in the type check too
        public string? Project;
        public List<string> Rest = new();
    }

    static Options Parse(List<string> a)
    {
        var o = new Options();
        for (int i = 0; i < a.Count; i++)
        {
            switch (a[i])
            {
                case "--": o.Rest = a.Skip(i + 1).ToList(); return o;
                case "-o": o.Output = Value(a, ref i); break;
                case "-r": o.Refs.Add(Value(a, ref i)); break;
                case "--refs-file": o.RefsFile = Value(a, ref i); break;
                case "--analyzers-file": o.AnalyzersFile = Value(a, ref i); break;
                case "--release": o.Release = true; break;
                case "--native": o.Native = true; break;
                default:
                    if (a[i].EndsWith(".al")) { UsageException.RequireFile(a[i]); o.Files.Add(a[i]); }
                    else if (a[i].EndsWith(".alproj") || Directory.Exists(a[i])) o.Project = a[i];
                    else o.Rest.Add(a[i]);
                    break;
            }
        }
        if (o.RefsFile != null) o.Refs.AddRange(File.ReadAllLines(o.RefsFile).Where(l => l.Length > 0));
        return o;
    }

    static string Value(List<string> a, ref int i) =>
        i + 1 < a.Count && !a[i + 1].StartsWith('-') ? a[++i] : throw new UsageException(T("opt.needs.value", a[i]));

    /// <summary>Entry files plus everything they `require`.</summary>
    static List<SourceFile> Read(IEnumerable<string> paths) => AliceCompiler.LoadWithRequires(paths);

    static Dictionary<string, string> Sources(List<SourceFile> files) => files.ToDictionary(f => f.Path, f => f.Text);

    /// <summary>Compile Alice sources to an in-memory IL image, rendering diagnostics on failure.</summary>
    static byte[]? CompileFiles(List<SourceFile> files, string name, bool exe, List<string> refs, string? nativeDir = null, Action? onNative = null)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var image = UiH.Spin($"{T("compiling")} {name}…", () =>
            {
                var ph = Stopwatch.StartNew();
                var (cs, c) = AliceCompiler.TranspileFull(files, refs, name + "_native");
                Alice.Cli.Telemetry.TelemetrySession.Phase("transpile", ph.ElapsedMilliseconds);
                // @[Native] functions: clang -O3 -march=native; without a C compiler the managed bodies run instead
                if (c != null)
                {
                    onNative?.Invoke();
                    string dir = nativeDir ?? Path.Combine(Path.GetTempPath(), "alice-native-" + Guid.NewGuid().ToString("N"));
                    if (AliceCompiler.BuildNativeLibrary(c, dir, name + "_native")) Alice.Runtime.NativeLibs.AddSearchPath(dir);
                    else Console.Error.WriteLine("warning: no C compiler found; @[Native] functions use their managed fallback");
                }
                ph.Restart();
                var img = AliceCompiler.Emit(cs, name, exe, refs);
                Alice.Cli.Telemetry.TelemetrySession.Phase("emit", ph.ElapsedMilliseconds);
                return img;
            });
            UiH.Status(T("compiling"), $"{name} ({UiH.Elapsed(sw)})");
            return image;
        }
        catch (CompileException e)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics, Sources(files));
            return null;
        }
    }

    public static int RunCmd(List<string> a)
    {
        var o = Parse(a);
        if (o.Files.Count == 0) return RunProject(o);
        var files = Read(o.Files);
        string name = Path.GetFileNameWithoutExtension(o.Files[0]);
        string? key = RunCache.Enabled ? RunCache.Key(files, name, o.Refs) : null;
        var image = key != null ? RunCache.TryGet(key) : null;
        if (image == null)
        {
            bool native = false;
            image = CompileFiles(files, name, true, o.Refs, onNative: () => native = true);
            if (image == null) return 1;
            if (key != null && !native) RunCache.Put(key, image);   // a native library lives beside the run, not in the image
        }
        UiH.Status(T("running"), name);
        var asm = AliceCompiler.Load(image);
        var entry = asm.EntryPoint;
        if (entry == null) return 0;
        try { entry.Invoke(null, [o.Rest.ToArray()]); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
        {
            Ui.Ui.Error($"{e.InnerException.GetType().Name}: {e.InnerException.Message}");
            return 101;
        }
        return 0;
    }

    /// <summary>Builds every workspace member, dependencies first.</summary>
    public static int BuildWorkspace(string root, bool release)
    {
        var ws = Workspace.Load(root);
        UiH.Status(T("workspace"), $"{ws.Members.Count} member(s): {string.Join(", ", ws.Members.Select(m => m.Name))}", "blue");
        foreach (var m in ws.Members)
        {
            int rc = BuildProject(Manifest.Sync(m.Dir), release);
            if (rc != 0) return rc;
        }
        return 0;
    }

    static int RunProject(Options o)
    {
        string where = o.Project ?? ".";
        if (Workspace.IsWorkspaceRoot(where))
        {
            var ws = Workspace.Load(where);
            var exes = ws.Members.Where(m => m.Kind != "lib").ToList();
            string? pick = o.Rest.FirstOrDefault(r => exes.Any(e => e.Name == r));
            var target = pick != null ? exes.First(e => e.Name == pick) : exes.Count == 1 ? exes[0] : throw new CompileException($"workspace has {exes.Count} runnable members ({string.Join(", ", exes.Select(e => e.Name))}): alice run <member>");
            if (pick != null) o.Rest.Remove(pick);
            where = target.Dir;
        }
        var proj = ProjectFiles.Find(where) ?? throw UsageException.NoProject();
        int rc = BuildProject(proj, o.Release);
        if (rc != 0) return rc;
        UiH.Status(T("running"), Path.GetFileNameWithoutExtension(proj));
        if (ProjectFiles.Kind(proj) == "wasm") return RunWasm(proj, o.Rest);
        var args = new List<string> { "run", "--no-build", "--project", proj, "-c", o.Release ? "Release" : "Debug", "--" };
        args.AddRange(o.Rest);
        return DotNet.Run(args, inherit: true).ExitCode;
    }

    public static int Build(List<string> a)
    {
        var o = Parse(a);
        if (o.Files.Count == 0)
        {
            if (Workspace.IsWorkspaceRoot(o.Project ?? ".")) return BuildWorkspace(o.Project ?? ".", o.Release);
            var proj = ProjectFiles.Find(o.Project ?? ".") ?? throw UsageException.NoProject();
            return BuildProject(proj, o.Release || o.Native, o.Native);
        }
        var files = Read(o.Files);
        string name = Path.GetFileNameWithoutExtension(o.Output ?? o.Files[0]);
        string dir = Path.GetDirectoryName(Path.GetFullPath(o.Output ?? o.Files[0] + ".dll"))!;
        Directory.CreateDirectory(dir);
        if (o.Native) return NativeSingle(files, name, dir, o.Refs);
        var cs = AliceCompiler.TranspileAll(files, o.Refs);
        bool exe = cs.Any(c => c.Contains("public static void Main(string[] ARGV)"));
        var image = CompileFiles(files, name, exe, o.Refs, dir);
        if (image == null) return 1;
        var dll = Path.Combine(dir, name + ".dll");
        File.WriteAllBytes(dll, image);
        var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
        File.Copy(rt, Path.Combine(dir, Path.GetFileName(rt)), true);
        foreach (var r in o.Refs) File.Copy(r, Path.Combine(dir, Path.GetFileName(r)), true);
        if (exe)
            File.WriteAllText(Path.Combine(dir, name + ".runtimeconfig.json"),
                "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + Environment.Version.Major + ".0.0\"},\"configProperties\":{\"System.Runtime.TieredPGO\":true,\"System.GC.Server\":" + (AliceCompiler.UsesParallel(cs) ? "true,\"System.GC.Gen0MaxBudget\":16777216" : "false") + ",\"System.GC.Concurrent\":true}}}");
        UiH.Status(T("finished"), dll);
        return 0;
    }

    /// <summary>NativeAOT for loose files: wrap them in a throwaway .alproj and publish.</summary>
    /// <summary>
    /// Copies the entry file and everything it `require`s under their common root, keeping relative paths: a flat copy
    /// broke `require "../lib/x"` (multi-file programs could not be built with --native). The staging directory is
    /// emptied first, so a file from an earlier build is never compiled.
    /// </summary>
    public static void StageNativeSources(List<SourceFile> files, string work)
    {
        var full = files.Select(f => Path.GetFullPath(f.Path)).ToList();
        string root = Path.GetDirectoryName(full[0])!;
        foreach (var p in full.Skip(1))
            while (!(Path.GetDirectoryName(p)! + Path.DirectorySeparatorChar).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                root = Path.GetDirectoryName(root) ?? root;
        foreach (var old in Directory.Exists(work) ? Directory.GetFiles(work, "*.al", SearchOption.AllDirectories) : []) File.Delete(old);
        foreach (var p in full)
        {
            var dest = Path.Combine(work, "src", Path.GetRelativePath(root, p));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(p, dest, true);
        }
    }

    static int NativeSingle(List<SourceFile> files, string name, string dir, List<string> refs)
    {
        var work = Path.Combine(dir, "obj", "alice-native", name);
        StageNativeSources(files, work);
        var proj = Path.Combine(work, name + ".alproj");
        File.WriteAllText(proj, ProjectFiles.ProjectXml("exe").Replace("</PropertyGroup>",
            "  <PublishAot>true</PublishAot>\n    <OptimizationPreference>Speed</OptimizationPreference>\n    <IlcInstructionSet>native</IlcInstructionSet>\n  </PropertyGroup>"));
        return Publish(proj, dir);
    }

    static int Publish(string proj, string outDir)
    {
        var sw = Stopwatch.StartNew();
        var r = UiH.Spin($"{T("building")} native {Path.GetFileNameWithoutExtension(proj)}…",
            () => DotNet.Run(["publish", proj, "-c", "Release", "-o", outDir, "-p:PublishAot=true", "-v", "q", "-nologo"]));
        if (r.ExitCode != 0)
        {
            Report(r);
            return r.ExitCode;
        }
        UiH.Status(T("finished"), $"native {outDir} ({UiH.Elapsed(sw)})");
        return 0;
    }

    public static int BuildProject(string proj, bool release, bool native = false)
    {
        if (native) return Publish(proj, Path.Combine(Path.GetDirectoryName(proj)!, "bin", "native"));
        var sw = Stopwatch.StartNew();
        string name = Path.GetFileNameWithoutExtension(proj);
        int built = 0;
        UiH.Status(T("compiling"), $"{name} ({(release ? "release" : "debug")})");
        if (ProjectFiles.Kind(proj) == "wasm") return BuildWasm(proj, release, sw);
        var r = UiH.Spin($"{T("building")} {name}…", () => DotNet.Run(["build", proj, "-c", release ? "Release" : "Debug", "-v", "q", "-nologo", "-clp:NoSummary"],
            line => { if (line.Contains(" -> ")) Interlocked.Increment(ref built); }));
        if (r.ExitCode != 0 || r.Diagnostics.Any(d => d.Severity == "error"))
        {
            Report(r);
            return r.ExitCode == 0 ? 1 : r.ExitCode;
        }
        UiH.Status(T("finished"), T("finished.detail", Math.Max(1, built), UiH.Elapsed(sw)));
        RecordLock(proj);
        return 0;
    }

    /// <summary>A wasm package publishes: dist/wwwroot is the whole site (index.html, main.js, _framework/), ready
    /// for any static host.</summary>
    static int BuildWasm(string proj, bool release, Stopwatch sw)
    {
        string name = Path.GetFileNameWithoutExtension(proj);
        string dist = Path.Combine(ProjectFiles.PackageDir(proj), "dist");
        var r = UiH.Spin($"{T("building")} {name}…", () => DotNet.Run(["publish", proj, "-c", release ? "Release" : "Debug", "-o", dist,
            "-v", "q", "-nologo", "-clp:NoSummary"]));
        if (r.ExitCode != 0 || r.Diagnostics.Any(d => d.Severity == "error"))
        {
            Report(r);
            return r.ExitCode == 0 ? 1 : r.ExitCode;
        }
        UiH.Status(T("finished"), $"{Path.GetRelativePath(".", Path.Combine(dist, "wwwroot"))} ({UiH.Elapsed(sw)})");
        RecordLock(proj);
        return 0;
    }

    /// <summary>`alice run` on a wasm package: the bundle under Node, which runs the same .NET WebAssembly runtime.</summary>
    static int RunWasm(string proj, List<string> rest)
    {
        string pkg = ProjectFiles.PackageDir(proj);
        string runner = Path.Combine(pkg, ".alice", "run.mjs");
        File.WriteAllText(runner, WasmTemplates.NodeRunner(Path.Combine(pkg, "dist", "wwwroot", "_framework", "dotnet.js")));
        var psi = new ProcessStartInfo("node") { UseShellExecute = false };
        psi.ArgumentList.Add(runner);
        foreach (var a in rest) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new UsageException("alice run on a wasm package needs Node.js", "install node, or serve dist/wwwroot with any static web server and open it in a browser");
        }
    }

    /// <summary>After a successful restore, pin the exact NuGet versions in alice.lock.</summary>
    static void RecordLock(string proj)
    {
        string dir = Path.GetDirectoryName(Path.GetDirectoryName(proj)!)!;
        if (!File.Exists(Path.Combine(dir, Manifest.FileName))) return;
        var deps = TomlLite.Parse(File.ReadAllText(Path.Combine(dir, Manifest.FileName))).GetValueOrDefault("dependencies") ?? new();
        var nuget = deps.Where(kv => kv.Value is string).Select(kv => kv.Key).ToList();
        if (nuget.Count == 0) return;
        var resolved = Lockfile.FromAssets(Path.Combine(dir, "obj", "project.assets.json"), nuget);
        if (resolved.Count == 0) return;
        string lockDir = Workspace.FindRoot(dir) ?? dir;
        var before = Lockfile.Read(dir);
        if (resolved.All(kv => before.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
        Lockfile.Write(lockDir, resolved);
        UiH.Status(T("locked"), string.Join(", ", resolved.Select(kv => $"{kv.Key} {kv.Value}")));
    }

    internal static void Report(DotNet.Result r)
    {
        // MSB3073 ("the command alice gen exited with code 1") only repeats the Alice errors above it
        var errors = r.Diagnostics.Where(d => d.Severity == "error").ToList();
        if (errors.Any(d => d.Code != "MSB3073")) errors.RemoveAll(d => d.Code == "MSB3073");
        if (r.Diagnostics.Count > 0) Ui.DiagnosticRenderer.Render(errors);
        else foreach (var l in r.Output.TakeLast(30)) Console.Error.WriteLine(l);
    }

    public static int Check(List<string> a)
    {
        var o = Parse(a);
        var paths = o.Files.Count > 0 ? o.Files : Directory.GetFiles(".", "*.al", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).ToList();
        if (paths.Count == 0) throw new UsageException(T("no.files"), T("hint.no.project"));
        var files = Read(paths);
        var sw = Stopwatch.StartNew();
        var image = CompileFiles(files, "check", false, o.Refs);
        if (image == null) return 1;
        UiH.Status(T("finished"), $"{T("checking")} {files.Count} file(s) ({UiH.Elapsed(sw)})");
        return 0;
    }

    /// <summary>`alice prove [files] [--check]`: every .al file under the current directory by default.</summary>
    public static int ProveCmd(List<string> a)
    {
        bool check = a.Remove("--check");
        bool smt = a.Remove("--smt");
        var o = Parse(a);
        // this package's sources: nested directories with their own alice.toml (examples, workspace members) are other
        // packages, with their own dependencies and their own RULES.al
        static bool OwnPackage(string f)
        {
            for (var d = Path.GetDirectoryName(Path.GetFullPath(f)); d != null && d != Path.GetFullPath("."); d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, Manifest.FileName))) return false;
            return true;
        }
        var paths = o.Files.Count > 0 ? o.Files : Directory.GetFiles(".", "*.al", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("/.alice/") && Path.GetFileName(f) != Prove.ProofFile && OwnPackage(f)).ToList();
        var files = Read(paths);
        try { return Prove.Run(files, o.Refs, check, ".", smt); }
        catch (CompileException e)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics, Sources(files));
            return 1;
        }
    }

    public static int ExplainCmd(List<string> a)
    {
        var o = Parse(a);
        if (o.Files.Count == 0) throw new UsageException(T("no.files"), T("hint.no.project"));
        try { return Explain.Run(Read(o.Files), o.Refs); }
        catch (CompileException e)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics, Sources(Read(o.Files)));
            return 1;
        }
    }

    public static int Emit(List<string> a)
    {
        var o = Parse(a);
        if (o.Files.Count == 0) throw UsageException.Usage("alice emit <file.al>");
        foreach (var cs in AliceCompiler.TranspileAll(Read(o.Files), o.Refs)) Console.WriteLine(cs);
        return 0;
    }

    /// <summary>MSBuild entry (Alice.targets): writes one .g.cs per source; errors in canonical MSBuild format.</summary>
    public static int Gen(List<string> a)
    {
        // package builds pass --no-check: csc type-checks the output anyway, and its errors come back as Alice
        // diagnostics (DotNet.ParseLine). The in-process check is a second, cold compile: 2.4 of 3.7 s per edit.
        bool check = !a.Remove("--no-check");
        var o = Parse(a);
        if (o.Output == null || o.Files.Count == 0) throw new CompileException(T("no.files"));
        Directory.CreateDirectory(o.Output);
        try
        {
            var files = Read(o.Files);
            string lib = o.Rest.FirstOrDefault(r => r.StartsWith("--native-lib="))?["--native-lib=".Length..] ?? "alice_native";
            string naming = o.Rest.FirstOrDefault(r => r.StartsWith("--naming="))?["--naming=".Length..] ?? "alice";
            var (cs, csrc) = AliceCompiler.TranspileFull(files, o.Refs, lib, naming == "dotnet");
            // read by Alice.targets when alice.toml leaves [runtime] gc at "auto"
            File.WriteAllText(Path.Combine(o.Output, "alice.gc"), AliceCompiler.UsesParallel(cs) ? "server" : "workstation");
            if (csrc != null) File.WriteAllText(Path.Combine(o.Output, lib + ".c"), csrc);
            // type-check here (in-process Roslyn) so errors are reported in Alice terms, never as raw csc output
            // Emit already references the running framework. Keep other framework packs (e.g. ASP.NET).
            var runtimeRefs = (((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? "")
                .Split(Path.PathSeparator).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var checkRefs = o.Refs.Where(r => !runtimeRefs.Contains(Path.GetFileName(r)) && !Path.GetFileName(r).StartsWith("Alice.Runtime") && File.Exists(r)).ToList();
            var analyzers = o.AnalyzersFile != null && File.Exists(o.AnalyzersFile)
                ? File.ReadAllLines(o.AnalyzersFile).Where(l => l.Length > 0).ToList() : null;
            if (check) AliceCompiler.Emit(cs, "check", cs.Any(c => c.Contains("public static void Main(string[] ARGV)")), checkRefs, analyzers);
            var used = new HashSet<string>();
            foreach (var old in Directory.GetFiles(o.Output, "*.g.cs")) File.Delete(old);
            for (int i = 0; i < files.Count; i++)
            {
                string baseName = Path.GetFileNameWithoutExtension(files[i].Path);
                string n = baseName;
                for (int k = 2; !used.Add(n); k++) n = baseName + k;
                File.WriteAllText(Path.Combine(o.Output, n + ".al.g.cs"), cs[i]);
            }
            return 0;
        }
        catch (CompileException e)
        {
            foreach (var d in e.Diagnostics) Console.WriteLine($"{d.File}({d.Line},{d.Col}): error {d.Code}: {d.Message}");
            return 1;
        }
    }
}
