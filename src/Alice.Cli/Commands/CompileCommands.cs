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
        public bool ReadyToRun;
        public string? RefsFile;
        public string? AnalyzersFile;   // MSBuild @(Analyzer): source generators run in the type check too
        public string? Project;
        public bool Strict;   // --strict: AL0220 for anything not statically typed
        public string? Prove;   // --prove / --prove=smt: `alice prove --check` after the build (also `[build] prove`)
        public List<string> Rest = new();
    }

    /// <summary>
    /// Server GC heaps for parallel programs (the runtime caps it at the core count, so min(cores, 8)). One heap per
    /// core cost the 32-core box ~10 ms of startup (bfs 0: 38 -> 29 ms) and no throughput: bfs, kmeans and the
    /// idiomatic tree-bitonic ran 2-15% faster with 8 heaps (docs/heap-gc.md). Alice.targets uses the same value.
    /// </summary>
    const int ServerHeaps = 8;

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
                case "--r2r": o.ReadyToRun = o.Release = true; break;
                case "--strict": o.Strict = true; break;
                case "--prove": o.Prove = "enumerate"; break;
                case "--prove=smt": o.Prove = "smt"; break;
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
    static byte[]? CompileFiles(List<SourceFile> files, string name, bool exe, List<string> refs, string? nativeDir = null, Action? onNative = null, bool strict = false)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var image = UiH.Spin($"{T("compiling")} {name}…", () =>
            {
                var (img, c) = CompileCSharp(files, name, exe, refs, strict);
                // @[Native] functions: clang -O3 -march=native; without a C compiler the managed bodies run instead
                if (c != null)
                {
                    onNative?.Invoke();
                    string dir = nativeDir ?? Path.Combine(Path.GetTempPath(), "alice-native-" + Guid.NewGuid().ToString("N"));
                    if (AliceCompiler.BuildNativeLibrary(c, dir, name + "_native")) Alice.Runtime.NativeLibs.AddSearchPath(dir);
                    else Console.Error.WriteLine("warning: no C compiler found; @[Native] functions use their managed fallback");
                }
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

    /// <summary>
    /// The C# pipeline: lowering, then Roslyn (nullable flow -> AL0310, constant overflow -> AL0300, ...). Throws
    /// <see cref="CompileException"/> on any error. Both backends pass through this one gate (review-r1 F3): until the
    /// binder owns nullable flow, `--backend il` must never accept a program the C# path rejects.
    /// </summary>
    static (byte[] Image, string? C) CompileCSharp(List<SourceFile> files, string name, bool exe, List<string> refs, bool strict)
    {
        var ph = Stopwatch.StartNew();
        var (cs, c) = AliceCompiler.TranspileFull(files, refs, name + "_native", strict: strict);
        Alice.Cli.Telemetry.TelemetrySession.Phase("transpile", ph.ElapsedMilliseconds);
        ph.Restart();
        var img = AliceCompiler.Emit(cs, name, exe, refs);
        Alice.Cli.Telemetry.TelemetrySession.Phase("emit", ph.ElapsedMilliseconds);
        return (img, c);
    }

    public static int RunCmd(List<string> a)
    {
        if (WasmTarget(a)) return WasmCommands.RunAlice(a);   // ADR 0034: native wasm backend (runtime = "alice")
        string? flagBackend = TakeOption(a, "--backend");
        string backend = ResolveBackend(flagBackend, null);
        var o = Parse(a);
        if (o.Files.Count == 0) return RunProject(o, backend);
        var files = Read(o.Files);
        string name = Path.GetFileNameWithoutExtension(o.Files[0]);
        string? key = RunCache.Enabled && !o.Strict ? RunCache.Key(files, name, o.Refs) : null;   // a cached image skipped the strict check
        var image = key != null ? RunCache.TryGet(key) : null;
        if (image == null)
        {
            bool native = false;
            image = CompileFiles(files, name, true, o.Refs, onNative: () => native = true, strict: o.Strict);
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

    /// <summary>`--name value` removed from the arguments (before the program arguments are collected).</summary>
    /// <summary>`--target wasm32` / `wasm32-wasi` (removed from the arguments); any other target is a usage error.</summary>
    static bool WasmTarget(List<string> a)
    {
        string? t = TakeOption(a, "--target");
        if (t == null || WasmCommands.IsAliceTarget(t)) return t != null;
        throw new UsageException($"unknown target `{t}`", "--target wasm32 | wasm32-wasi");
    }

    /// <summary>Backend choice (precedence: flag > toml > env > default IL).</summary>
    static string ResolveBackend(string? flag, string? toml)
    {
        if (flag is not null)
        {
            if (flag is not ("csharp" or "il")) throw new UsageException($"unknown backend `{flag}`", "--backend il | csharp");
            return flag;
        }
        if (toml is not null) return toml;
        string? env = Environment.GetEnvironmentVariable("ALICE_BACKEND");
        if (env is not null)
        {
            if (env is not ("csharp" or "il")) throw new InvalidOperationException($"ALICE_BACKEND={env} is not a valid backend (expected il or csharp)");
            return env;
        }
        return "il";   // ADR 0033: IL is the default backend (1.0.0)
    }

    static string? TakeOption(List<string> a, string name)
    {
        int i = a.IndexOf(name);
        int dd = a.IndexOf("--");
        if (i < 0 || dd >= 0 && i > dd || i + 1 >= a.Count) return null;
        string v = a[i + 1];
        a.RemoveRange(i, 2);
        return v;
    }

    /// <summary>The direct IL backend (ADR 0033, D1.4): unsupported nodes are reported, nothing falls back silently.</summary>
    static Alice.Compiler.Backend.Il.IlCompileResult? CompileIl(List<SourceFile> files, string name, List<string> refs, bool strict = false)
    {
        try
        {
            CompileCSharp(files, name, exe: true, refs, strict);   // the C# path's diagnostic gate (F3); the image is discarded
            var res = Alice.Compiler.Backend.Il.AirToIl.Compile(files, name, exe: true, refs);
            if (res.Ok) return res;
            foreach (var e in res.Errors.Take(20)) Console.Error.WriteLine("il: unsupported " + e.Message);
            Console.Error.WriteLine("il: " + res.Stats);
            return null;
        }
        catch (CompileException e)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics, Sources(files));
            return null;
        }
    }

    static int RunIl(Options o)
    {
        var files = Read(o.Files);
        string name = Path.GetFileNameWithoutExtension(o.Files[0]);
        var res = CompileIl(files, name, o.Refs, o.Strict);
        if (res == null) return 1;
        var entry = AliceCompiler.Load(res.Pe!).EntryPoint;
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
    public static int BuildWorkspace(string root, bool release, bool strict = false, string? prove = null)
    {
        var ws = Workspace.Load(root);
        UiH.Status(T("workspace"), $"{ws.Members.Count} member(s): {string.Join(", ", ws.Members.Select(m => m.Name))}", "blue");
        foreach (var m in ws.Members)
        {
            int rc = BuildProject(Manifest.Sync(m.Dir), release, strict: strict);
            if (rc == 0) rc = ProveAfterBuild(m.Dir, prove);
            if (rc != 0) return rc;
        }
        return 0;
    }

    /// <summary>
    /// `alice build --prove` or `[build] prove = true` (`"smt"` for --smt): when the package has a RULES.al, the build
    /// runs `alice prove --check` in the package directory and fails when a rule fails or PROOF.al is stale, so CI
    /// gates on the rules with no extra step. Without RULES.al there is nothing to prove.
    /// </summary>
    public static int ProveAfterBuild(string dir, string? flag)
    {
        string? mode = flag ?? ProveSetting(dir);
        if (mode == null || !File.Exists(Path.Combine(dir, "RULES.al"))) return 0;
        string cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            UiH.Status("Proving", $"{Path.GetFileName(Path.GetFullPath("."))} (alice prove --check{(mode == "smt" ? " --smt" : "")})");
            return ProveCmd(mode == "smt" ? ["--check", "--smt"] : ["--check"]);
        }
        finally { Directory.SetCurrentDirectory(cwd); }
    }

    /// <summary>`[build] prove` from the package's alice.toml: true, "smt", or absent/false (null).</summary>
    public static string? ProveSetting(string dir)
    {
        string toml = Path.Combine(dir, Manifest.FileName);
        if (!File.Exists(toml)) return null;
        var build = TomlLite.Parse(File.ReadAllText(toml)).GetValueOrDefault("build");
        return build != null && build.TryGetValue("prove", out var p) ? p switch
        {
            true => "enumerate",
            false => null,
            "smt" => "smt",
            _ => throw new UsageException($"[build] prove = {p} is not a prove mode", "use `prove = true` (enumeration) or `prove = \"smt\"`"),
        } : null;
    }

    static int RunProject(Options o, string? backend = null)
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
        bool wasm = ProjectFiles.Kind(proj) == "wasm";
        if (wasm) WasmCommands.RequireNode();
        int rc = BuildProject(proj, o.Release, strict: o.Strict);
        if (rc != 0) return rc;
        UiH.Status(T("running"), Path.GetFileNameWithoutExtension(proj));
        if (wasm) return WasmCommands.Run(proj, o.Rest);
        var args = new List<string> { "run", "--no-build", "--project", proj, "-c", o.Release ? "Release" : "Debug", "--" };
        args.AddRange(o.Rest);
        return DotNet.Run(args, inherit: true).ExitCode;
    }

    /// <summary>The runtimeconfig of a built executable: server GC (bounded heaps) when the program uses Alice.Runtime.Par.</summary>
    static string RuntimeConfig(bool parallel) =>
        "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + Environment.Version.Major + ".0.0\"},\"configProperties\":{\"System.Runtime.TieredPGO\":true,\"System.GC.Server\":" + (parallel ? "true,\"System.GC.Gen0MaxBudget\":16777216,\"System.GC.HeapCount\":" + ServerHeaps : "false") + ",\"System.GC.Concurrent\":true}}}";

    public static int Build(List<string> a)
    {
        if (WasmTarget(a)) return WasmCommands.BuildAlice(a);   // ADR 0034: native wasm backend (runtime = "alice")
        var o = Parse(a);
        if (o.Files.Count == 0)
        {
            if (Workspace.IsWorkspaceRoot(o.Project ?? ".")) return BuildWorkspace(o.Project ?? ".", o.Release, o.Strict, o.Prove);
            var proj = ProjectFiles.Find(o.Project ?? ".") ?? throw UsageException.NoProject();
            if (o.Native && ProjectFiles.Kind(proj) == "wasm")
                throw new UsageException("--native does not apply to a WebAssembly package", "alice build --release trims it (and compiles it ahead of time with the wasm-tools workload)");
            int rc = BuildProject(proj, o.Release || o.Native, o.Native, o.ReadyToRun, o.Strict);
            return rc != 0 ? rc : ProveAfterBuild(ProjectFiles.PackageDir(proj), o.Prove);
        }
        if (o.Prove != null) throw new UsageException("--prove checks a package's RULES.al", "run `alice build --prove` in the package directory, or `alice prove --check` for loose files");
        var files = Read(o.Files);
        string name = Path.GetFileNameWithoutExtension(o.Output ?? o.Files[0]);
        string dir = Path.GetDirectoryName(Path.GetFullPath(o.Output ?? o.Files[0] + ".dll"))!;
        Directory.CreateDirectory(dir);
        if (o.Native) return NativeSingle(files, name, dir, o.Refs);
        var cs = AliceCompiler.TranspileAll(files, o.Refs);
        bool exe = cs.Any(c => c.Contains("public static void Main(string[] ARGV)"));
        var image = CompileFiles(files, name, exe, o.Refs, dir, strict: o.Strict);
        if (image == null) return 1;
        var dll = Path.Combine(dir, name + ".dll");
        File.WriteAllBytes(dll, image);
        var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
        File.Copy(rt, Path.Combine(dir, Path.GetFileName(rt)), true);
        foreach (var r in o.Refs) File.Copy(r, Path.Combine(dir, Path.GetFileName(r)), true);
        if (exe)
            File.WriteAllText(Path.Combine(dir, name + ".runtimeconfig.json"), RuntimeConfig(AliceCompiler.UsesParallel(cs)));
        UiH.Status(T("finished"), dll);
        return 0;
    }

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

    /// <summary>NativeAOT for loose files: wrap them in a throwaway .alproj and publish.</summary>
    static int NativeSingle(List<SourceFile> files, string name, string dir, List<string> refs)
    {
        var work = Path.Combine(dir, "obj", "alice-native", name);
        StageNativeSources(files, work);
        var proj = Path.Combine(work, name + ".alproj");
        File.WriteAllText(proj, ProjectFiles.ProjectXml("exe").Replace("</PropertyGroup>",
            "  <PublishAot>true</PublishAot>\n    <OptimizationPreference>Speed</OptimizationPreference>\n    <IlcInstructionSet>native</IlcInstructionSet>\n    <IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>\n  </PropertyGroup>"));
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

    /// <summary>`dotnet publish` arguments for ReadyToRun (precompiled code beside the IL) for this machine's runtime.</summary>
    public static List<string> ReadyToRunArgs(string proj, string outDir) =>
        ["publish", proj, "-c", "Release", "-r", PortableRid(), "--self-contained", "false",
         "-o", outDir, "-p:PublishReadyToRun=true", "-v", "q", "-nologo", "-clp:NoSummary"];

    /// <summary>linux-x64, win-arm64, ...: distro-built runtimes report their own RID (arch-x64 on CachyOS), which has
    /// no runtime pack on NuGet.</summary>
    public static string PortableRid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{os}-{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }

    /// <summary>`alice build --r2r`, or `[build] ready_to_run = true` with --release: bin/r2r starts without JIT.</summary>
    static int ReadyToRun(string proj, Stopwatch sw)
    {
        string outDir = Path.Combine(ProjectFiles.PackageDir(proj), "bin", "r2r");
        var r = UiH.Spin($"{T("building")} ReadyToRun {Path.GetFileNameWithoutExtension(proj)}…", () => DotNet.Run(ReadyToRunArgs(proj, outDir)));
        if (r.ExitCode != 0)
        {
            Report(r);
            return r.ExitCode;
        }
        UiH.Status(T("finished"), $"ReadyToRun {Path.GetRelativePath(".", outDir)} ({UiH.Elapsed(sw)})");
        return 0;
    }

    public static int BuildProject(string proj, bool release, bool native = false, bool readyToRun = false, bool strict = false)
    {
        if (native) return Publish(proj, Path.Combine(Path.GetDirectoryName(proj)!, "bin", "native"));
        var sw = Stopwatch.StartNew();
        string name = Path.GetFileNameWithoutExtension(proj);
        int built = 0;
        UiH.Status(T("compiling"), $"{name} ({(release ? "release" : "debug")})");
        if (ProjectFiles.Kind(proj) == "wasm") return WasmCommands.Build(proj, release, sw);
        // --strict overrides alice.toml for this build (`[build] strict = true` sets AliceStrict in the .alproj)
        string[] buildArgs = ["build", proj, "-c", release ? "Release" : "Debug", "-v", "q", "-nologo", "-clp:NoSummary", .. (strict ? new[] { "-p:AliceStrict=true" } : [])];
        var r = UiH.Spin($"{T("building")} {name}…", () => DotNet.Run(buildArgs,
            line => { if (line.Contains(" -> ")) Interlocked.Increment(ref built); }));
        if (r.ExitCode != 0 || r.Diagnostics.Any(d => d.Severity == "error"))
        {
            Report(r);
            return r.ExitCode == 0 ? 1 : r.ExitCode;
        }
        UiH.Status(T("finished"), T("finished.detail", Math.Max(1, built), UiH.Elapsed(sw)));
        RecordLock(proj);
        if (readyToRun || (release && File.ReadAllText(proj).Contains("<PublishReadyToRun>true</PublishReadyToRun>"))) return ReadyToRun(proj, sw);
        return 0;
    }

    /// <summary>After a successful restore, pin the exact NuGet versions in alice.lock.</summary>
    internal static void RecordLock(string proj)
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
        bool onlyExec = errors.Count > 0 && errors.All(d => d.Code == "MSB3073");
        if (!onlyExec) errors.RemoveAll(d => d.Code == "MSB3073");
        // nothing but "alice gen exited with code N": what the command printed is the only clue, so show it
        if (r.Diagnostics.Count == 0 || onlyExec) foreach (var l in r.Output.Where(l => !l.Contains("MSB3073")).TakeLast(30)) Console.Error.WriteLine(l);
        if (r.Diagnostics.Count > 0) Ui.DiagnosticRenderer.Render(errors);
    }

    public static int Check(List<string> a)
    {
        var o = Parse(a);
        var paths = o.Files.Count > 0 ? o.Files : Directory.GetFiles(".", "*.al", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).ToList();
        if (paths.Count == 0) throw new UsageException(T("no.files"), T("hint.no.project"));
        var files = Read(paths);
        var sw = Stopwatch.StartNew();
        var image = CompileFiles(files, "check", false, o.Refs, strict: o.Strict);
        if (image == null) return 1;
        UiH.Status(T("finished"), $"{T("checking")} {files.Count} file(s) ({UiH.Elapsed(sw)})");
        return 0;
    }

    /// <summary>`alice prove [files] [--check] [--smt] [--timeout SECONDS] [--format text|json]`: every .al file under the
    /// current directory by default.</summary>
    public static int ProveCmd(List<string> a)
    {
        a = [.. a];
        bool check = a.Remove("--check");
        bool smt = a.Remove("--smt");
        string format = Flag(a, "--format") ?? "text";
        if (format is not ("text" or "json")) throw new UsageException($"--format {format} is not a format", "use --format text or --format json");
        TimeSpan? timeout = null;
        if (Flag(a, "--timeout") is { } secs)
            timeout = double.TryParse(secs.TrimEnd('s'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0
                ? TimeSpan.FromSeconds(t) : throw new UsageException($"--timeout {secs} is not a number of seconds", "e.g. --timeout 30 (per rule)");
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
        try { return Prove.Run(files, o.Refs, check, ".", smt, timeout, format == "json"); }
        catch (CompileException e)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics, Sources(files));
            return 1;
        }
    }

    /// <summary>`--name value` or `--name=value`, removed from the arguments.</summary>
    static string? Flag(List<string> a, string name)
    {
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].StartsWith(name + "=", StringComparison.Ordinal)) { var v = a[i][(name.Length + 1)..]; a.RemoveAt(i); return v; }
            if (a[i] != name) continue;
            if (i + 1 >= a.Count) throw new UsageException($"{name} needs a value");
            var value = a[i + 1];
            a.RemoveRange(i, 2);
            return value;
        }
        return null;
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
        // hidden: D1.1 PersistedAssemblyBuilder spike (throwaway, src/Alice.Compiler/Backend/IlSpike/README.md)
        if (a.Remove("--il-spike")) return Alice.Compiler.Backend.IlSpike.SpikeCli.Run(a);
        bool air = a.Remove("--air");   // hidden: the MIR/AIR text dump (ADR 0032) instead of C#
        bool ilOut = a.Remove("--il");  // <name>.dll + .pdb from the direct IL backend (ADR 0033)
        var o = Parse(a);
        if (o.Files.Count == 0) throw UsageException.Usage("alice emit <file.al>");
        var files = Read(o.Files);
        if (ilOut)
        {
            string name = Path.GetFileNameWithoutExtension(o.Output ?? o.Files[0]);
            string dir = Path.GetDirectoryName(Path.GetFullPath(o.Output ?? o.Files[0] + ".dll"))!;
            var res = CompileIl(files, name, o.Refs, o.Strict);
            if (res == null) return 1;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".dll"), res.Pe!);
            File.WriteAllBytes(Path.Combine(dir, name + ".pdb"), res.Pdb!);
            var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
            File.Copy(rt, Path.Combine(dir, Path.GetFileName(rt)), true);
            // the C# build's GC policy (server GC for parallel programs), so IL and C# images run alike
            File.WriteAllText(Path.Combine(dir, name + ".runtimeconfig.json"), RuntimeConfig(AliceCompiler.UsesParallel(AliceCompiler.TranspileAll(files, o.Refs))));
            Console.Write(Alice.Compiler.Backend.Il.IlDisassembler.Disassemble(res.Module!));
            Console.Error.WriteLine("il: " + res.Stats + " -> " + Path.Combine(dir, name + ".dll"));
            return 0;
        }
        try
        {
            var units = () => AliceCompiler.TranspileFull(files, o.Refs, "alice_native", strict: o.Strict).CSharp;
            if (air) Console.Write(Alice.Compiler.Mir.MirDump.Collect(() => units()));
            else foreach (var cs in units()) Console.WriteLine(cs);
        }
        catch (CompileException e) when (o.Strict)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics, Sources(files));
            return 1;
        }
        return 0;
    }

    /// <summary>MSBuild entry (Alice.targets): writes one .g.cs per source; errors in canonical MSBuild format.</summary>
    public static int Gen(List<string> a)
    {
        // package builds pass --no-check: csc type-checks the output anyway, and its errors come back as Alice
        // diagnostics (DotNet.ParseLine). The in-process check is a second, cold compile: 2.4 of 3.7 s per edit.
        bool check = !a.Remove("--no-check");
        bool sequential = a.Remove("--sequential");
        var o = Parse(a);   // --strict (AliceStrict): AL0220
        if (o.Output == null || o.Files.Count == 0) throw new CompileException(T("no.files"));
        Directory.CreateDirectory(o.Output);
        try
        {
            var files = Read(o.Files);
            string lib = o.Rest.FirstOrDefault(r => r.StartsWith("--native-lib="))?["--native-lib=".Length..] ?? "alice_native";
            string naming = o.Rest.FirstOrDefault(r => r.StartsWith("--naming="))?["--naming=".Length..] ?? "alice";
            var (cs, csrc) = AliceCompiler.TranspileFull(files, o.Refs, lib, naming == "dotnet", sequential, o.Strict);
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
