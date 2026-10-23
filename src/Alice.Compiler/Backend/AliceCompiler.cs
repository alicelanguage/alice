using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Alice.Compiler;

/// <summary>An Alice source. LibraryOnly drops top-level statements (test builds reuse app files without running them).</summary>
public sealed record SourceFile(string Path, string Text, bool LibraryOnly = false);

/// <summary>Front door: Alice source -> lowered C# -> Roslyn -> IL assembly on the CLR.</summary>
public static class AliceCompiler
{
    /// <summary>Alice files -> AST -> bound HIR -> MIR -> C# (one unit per file).</summary>
    public static List<string> TranspileAll(IReadOnlyList<SourceFile> files, IEnumerable<string>? extraRefs = null) =>
        TranspileFull(files, extraRefs, "alice_native").CSharp;

    /// <summary>C# units plus the C source of all @[Native] functions (null when there are none).</summary>
    public static List<string> TranspileAll(IReadOnlyList<SourceFile> files, IEnumerable<string>? extraRefs, Semantics.ClrTypes clr) =>
        TranspileFull(files, extraRefs, "alice_native", clr).CSharp;

    public static (List<string> CSharp, string? C) TranspileFull(IReadOnlyList<SourceFile> files, IEnumerable<string>? extraRefs, string nativeLib, bool dotnetNames = false) =>
        TranspileFull(files, extraRefs, nativeLib, null, dotnetNames);

    /// <summary>
    /// Whether the compiled program runs code in parallel (forks, parallel loops): `[runtime] gc = "auto"` then picks
    /// server GC. Parallel code allocating long-lived data is collection-bound on workstation GC (benchmarks/inet
    /// tree: 85 vs 24 ms); serial programs keep workstation GC.
    /// </summary>
    public static bool UsesParallel(IEnumerable<string> csharp) => csharp.Any(c => c.Contains("global::Alice.Runtime.Par."));

    /// <summary>One file with `[package] naming = "dotnet"`: public members get .NET names.</summary>
    public static string TranspileDotnetNames(string src, string file = "main.al") =>
        TranspileFull([new SourceFile(file, src)], null, "alice_native", null, true).CSharp[0];

    static (List<string> CSharp, string? C) TranspileFull(IReadOnlyList<SourceFile> files, IEnumerable<string>? extraRefs, string nativeLib, Semantics.ClrTypes? clr, bool dotnetNames = false)
    {
        var asts = files.Select(f =>
        {
            var ast = Syntax.Parser.ParseFile(f.Text, f.Path);
            if (f.LibraryOnly) ast.Items.RemoveAll(i => i is Syntax.StmtItem);
            return ast;
        }).ToList();
        var binder = new Semantics.Binder(asts, clr ?? new Semantics.ClrTypes(extraRefs)) { DotnetNames = dotnetNames };
        binder.Bind();
        var emitter = new Backend.Emitter(binder) { NativeLib = nativeLib };
        var cs = asts.Select(emitter.EmitFile).ToList();
        return (cs, emitter.Native.IsEmpty ? null : emitter.Native.Source);
    }

    /// <summary>Compiles C to a shared library with clang -O3 -march=native; returns false (managed fallback) without clang.</summary>
    public static bool BuildNativeLibrary(string cSource, string outDir, string libBase)
    {
        Directory.CreateDirectory(outDir);
        string c = Path.Combine(outDir, libBase + ".c");
        File.WriteAllText(c, cSource);
        string lib = Path.Combine(outDir, OperatingSystem.IsWindows() ? libBase + ".dll" : OperatingSystem.IsMacOS() ? "lib" + libBase + ".dylib" : "lib" + libBase + ".so");
        foreach (var cc in new[] { "clang", "cc", "gcc" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(cc) { RedirectStandardError = true, RedirectStandardOutput = true };
                // Windows clang targets MSVC: no -fPIC, and the C runtime already provides libm
                var flags = OperatingSystem.IsWindows()
                    ? new[] { "-O3", "-march=native", "-shared", "-o", lib, c }
                    : new[] { "-O3", "-march=native", "-shared", "-fPIC", "-o", lib, c, "-lm" };
                foreach (var a in flags) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi)!;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0) return true;
                throw new CompileException($"{c}:1:1: native compilation failed ({cc}): {err.Trim()}");
            }
            catch (System.ComponentModel.Win32Exception) { }   // compiler not installed: try the next one
        }
        return false;
    }

    public sealed record NativeBuild(byte[] Image, string? NativeDir);

    /// <summary>Single-file compile including @[Native] functions (library placed in a temp dir and registered).</summary>
    public static NativeBuild CompileNative(string src, string name, bool buildNative, string file = "main.al")
    {
        var (cs, c) = TranspileFull([new SourceFile(file, src)], null, name + "_native");
        var image = Emit(cs, name, exe: cs.Any(x => x.Contains("public static void Main(string[] ARGV)")));
        string? dir = null;
        if (buildNative && c != null)
        {
            dir = Path.Combine(Path.GetTempPath(), "alice-native-" + Guid.NewGuid().ToString("N"));
            if (BuildNativeLibrary(c, dir, name + "_native")) Alice.Runtime.NativeLibs.AddSearchPath(dir);
            else dir = null;
        }
        return new NativeBuild(image, dir);
    }

    static readonly System.Text.RegularExpressions.Regex RequireLine = new(@"^\s*require\s+""(?<p>[^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>Loads entry files plus everything they `require` (relative paths, `.al` optional), transitively.</summary>
    public static List<SourceFile> LoadWithRequires(IEnumerable<string> entries)
    {
        var result = new List<SourceFile>();
        var seen = new HashSet<string>();
        var queue = new Queue<string>(entries.Select(Path.GetFullPath));
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path)) continue;
            var text = File.ReadAllText(path);
            result.Add(new SourceFile(path, text));
            foreach (System.Text.RegularExpressions.Match m in RequireLine.Matches(text))
            {
                var rel = m.Groups["p"].Value;
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, rel));
                if (!full.EndsWith(".al")) full += ".al";
                if (!File.Exists(full))
                {
                    int line = text[..m.Index].Count(c => c == '\n') + 1;
                    throw new CompileException($"{path}:{line}:1: cannot find '{rel}' (looked for {full})");
                }
                queue.Enqueue(full);
            }
        }
        return result;
    }

    public static string Transpile(string src, string file = "main.al") => TranspileAll([new SourceFile(file, src)])[0];

    static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    static IEnumerable<MetadataReference> References()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var set = new HashSet<string>(tpa.Split(Path.PathSeparator));
        set.Add(typeof(Alice.Runtime.Prelude).Assembly.Location);
        set.Add(typeof(AliceCompiler).Assembly.Location);
        foreach (var p in set) if (p.Length > 0 && File.Exists(p)) yield return MetadataReference.CreateFromFile(p);
    }

    static readonly Lazy<List<MetadataReference>> Refs = new(() => References().ToList());

    // Null-safety diagnostics are errors in Alice (ADR 0004).
    static readonly string[] NullErrors = ["CS8600", "CS8601", "CS8602", "CS8603", "CS8604", "CS8625"];

    /// <summary>Compiles lowered C# to an IL image. Throws CompileException with Alice file:line diagnostics.</summary>
    /// <summary>
    /// Runs the Roslyn source generators of the given analyzer assemblies over the generated C#, as MSBuild would:
    /// `[JsonSerializable]` contexts, `[GeneratedRegex]`, `[LoggerMessage]`... written in Alice get their generated half.
    /// </summary>
    static CSharpCompilation RunGenerators(CSharpCompilation comp, IEnumerable<string> analyzers)
    {
        var gens = new List<ISourceGenerator>();
        foreach (var path in analyzers)
        {
            try { gens.AddRange(new Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference(path, AnalyzerLoader.Instance).GetGenerators(LanguageNames.CSharp)); }
            catch (Exception) { }   // an analyzer that cannot load is skipped, as MSBuild warns and continues
        }
        if (gens.Count == 0) return comp;
        CSharpGeneratorDriver.Create(gens, parseOptions: Parse).RunGeneratorsAndUpdateCompilation(comp, out var updated, out _);
        return (CSharpCompilation)updated;
    }

    /// <summary>
    /// Framework generators fire only on their attributes, and loading + running them costs ~0.5 s per compile:
    /// keep one only when the code uses its trigger. Generators from packages always run (referencing them is opt-in).
    /// </summary>
    static readonly (string Assembly, string[] Triggers)[] FrameworkGenerators =
    [
        ("System.Text.Json.SourceGeneration", ["JsonSerializable"]),
        ("System.Text.RegularExpressions.Generator", ["GeneratedRegex"]),
        ("Microsoft.Interop.LibraryImportGenerator", ["LibraryImport"]),
        ("Microsoft.Interop.ComInterfaceGenerator", ["GeneratedComInterface", "GeneratedComClass"]),
        ("Microsoft.Interop.JavaScript.JSImportGenerator", ["JSImport", "JSExport"]),
        ("Microsoft.Interop.SourceGeneration", ["LibraryImport", "GeneratedComInterface", "JSImport"]),
        ("Microsoft.Extensions.Logging.Generators", ["LoggerMessage"]),
        ("Microsoft.Extensions.Options.SourceGeneration", ["OptionsValidator"]),
        ("Microsoft.AspNetCore.Http.RequestDelegateGenerator", ["MapGet", "MapPost", "MapPut", "MapDelete"]),
    ];

    static IEnumerable<string> Triggered(IEnumerable<string> analyzers, IReadOnlyList<string> csharp)
    {
        foreach (var path in analyzers)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var known = FrameworkGenerators.FirstOrDefault(g => g.Assembly == name);
            if (known.Assembly == null || known.Triggers.Any(t => csharp.Any(c => c.Contains(t)))) yield return path;
        }
    }

    sealed class AnalyzerLoader : Microsoft.CodeAnalysis.IAnalyzerAssemblyLoader
    {
        public static readonly AnalyzerLoader Instance = new();
        public void AddDependencyLocation(string fullPath) { }
        public System.Reflection.Assembly LoadFromPath(string fullPath) => System.Reflection.Assembly.LoadFrom(fullPath);
    }

    /// <summary>The source generators that ship with the targeting pack (System.Text.Json, Regex, logging...), for
    /// single-file builds that have no MSBuild to supply them.</summary>
    static readonly Lazy<List<string>> FrameworkAnalyzers = new(() =>
    {
        try
        {
            var shared = Path.GetDirectoryName(typeof(object).Assembly.Location)!;   // dotnet/shared/Microsoft.NETCore.App/10.0.x
            var root = Path.GetFullPath(Path.Combine(shared, "..", "..", ".."));
            var packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packs)) return [];
            string major = Environment.Version.Major + ".";
            var ver = Directory.GetDirectories(packs).Select(Path.GetFileName).Where(v => v!.StartsWith(major)).OrderByDescending(v => Version.TryParse(v!.Split('-')[0], out var x) ? x : new Version()).FirstOrDefault();
            if (ver == null) return [];
            var dir = Path.Combine(packs, ver, "analyzers", "dotnet", "cs");
            return Directory.Exists(dir) ? [.. Directory.GetFiles(dir, "*.dll")] : [];
        }
        catch (Exception) { return []; }
    });

    public static byte[] Emit(IReadOnlyList<string> csharp, string assemblyName, bool exe, IEnumerable<string>? extraRefs = null, IEnumerable<string>? analyzers = null)
    {
        var trees = csharp.Select((c, i) => CSharpSyntaxTree.ParseText(c, Parse, path: $"{assemblyName}.{i}.g.cs")).ToList();
        var diag = NullErrors.ToDictionary(x => x, _ => ReportDiagnostic.Error);
        var opts = new CSharpCompilationOptions(exe ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithAllowUnsafe(true)
            .WithOverflowChecks(true)   // Int32::MAX + 1 raises (ADR 0013); `&+ &- &*` wrap
            .WithNullableContextOptions(NullableContextOptions.Enable)
            .WithSpecificDiagnosticOptions(diag);
        var refs = Refs.Value.Concat((extraRefs ?? []).Select(r => (MetadataReference)MetadataReference.CreateFromFile(Path.GetFullPath(r))));
        var comp = CSharpCompilation.Create(assemblyName, trees, refs, opts);
        comp = RunGenerators(comp, Triggered(analyzers ?? FrameworkAnalyzers.Value, csharp));
        using var ms = new MemoryStream();
        var res = comp.Emit(ms);
        if (!res.Success)
        {
            var errs = res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20).Select(d =>
            {
                var span = d.Location.GetMappedLineSpan();
                int len = Math.Max(1, span.EndLinePosition.Line == span.StartLinePosition.Line ? span.EndLinePosition.Character - span.StartLinePosition.Character : 1);
                var (code, msg) = Diagnostics.CSharpErrors.Translate(d);
                return new Diagnostic("error", code, span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, msg, len);
            }).ToList();
            throw new CompileException(errs);
        }
        return ms.ToArray();
    }

    /// <summary>Type-checks lowered C# without emitting IL (editor diagnostics): errors mapped to Alice positions.</summary>
    public static List<Diagnostic> Check(IReadOnlyList<string> csharp, IEnumerable<string>? extraRefs = null)
    {
        var trees = csharp.Select((c, i) => CSharpSyntaxTree.ParseText(c, Parse, path: $"check.{i}.g.cs")).ToList();
        var kind = csharp.Any(c => c.Contains("public static void Main(string[] ARGV)"))
            ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
        var opts = new CSharpCompilationOptions(kind)
            .WithAllowUnsafe(true)
            .WithOverflowChecks(true)   // Int32::MAX + 1 raises (ADR 0013); `&+ &- &*` wrap
            .WithNullableContextOptions(NullableContextOptions.Enable)
            .WithSpecificDiagnosticOptions(NullErrors.ToDictionary(x => x, _ => ReportDiagnostic.Error));
        var runtimeNames = Refs.Value.OfType<PortableExecutableReference>().Select(r => Path.GetFileName(r.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refs = Refs.Value.Concat((extraRefs ?? []).Where(File.Exists).Where(r => !runtimeNames.Contains(Path.GetFileName(r)))
            .Select(r => (MetadataReference)MetadataReference.CreateFromImage(File.ReadAllBytes(r), filePath: Path.GetFullPath(r))));
        var comp = CSharpCompilation.Create("check", trees, refs, opts);
        return comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(50).Select(d =>
        {
            var span = d.Location.GetMappedLineSpan();
            int len = Math.Max(1, span.EndLinePosition.Line == span.StartLinePosition.Line ? span.EndLinePosition.Character - span.StartLinePosition.Character : 1);
            var (code, msg) = Diagnostics.CSharpErrors.Translate(d);
            return new Diagnostic("error", code, span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, msg, len);
        }).ToList();
    }

    public static Assembly Load(byte[] image) => new AssemblyLoadContext("alice", isCollectible: true).LoadFromStream(new MemoryStream(image));

    /// <summary>Runs an assembly's entry point, capturing stdout.</summary>
    public static string Invoke(Assembly asm, string[] args)
    {
        var entry = asm.EntryPoint ?? throw new CompileException("program has no top-level statements (no entry point)");
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { entry.Invoke(null, [args]); }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        finally { Console.SetOut(old); }
        return sw.ToString();
    }

    public static string Run(string src, string file = "main.al", params string[] args)
        => Invoke(Load(Emit([Transpile(src, file)], "main", exe: true)), args);
}
