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

    public static (List<string> CSharp, string? C) TranspileFull(IReadOnlyList<SourceFile> files, IEnumerable<string>? extraRefs, string nativeLib) =>
        TranspileFull(files, extraRefs, nativeLib, null);

    static (List<string> CSharp, string? C) TranspileFull(IReadOnlyList<SourceFile> files, IEnumerable<string>? extraRefs, string nativeLib, Semantics.ClrTypes? clr)
    {
        var asts = files.Select(f =>
        {
            var ast = Syntax.Parser.ParseFile(f.Text, f.Path);
            if (f.LibraryOnly) ast.Items.RemoveAll(i => i is Syntax.StmtItem);
            return ast;
        }).ToList();
        var binder = new Semantics.Binder(asts, clr ?? new Semantics.ClrTypes(extraRefs));
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
    public static byte[] Emit(IReadOnlyList<string> csharp, string assemblyName, bool exe, IEnumerable<string>? extraRefs = null)
    {
        var trees = csharp.Select((c, i) => CSharpSyntaxTree.ParseText(c, Parse, path: $"{assemblyName}.{i}.g.cs")).ToList();
        var diag = NullErrors.ToDictionary(x => x, _ => ReportDiagnostic.Error);
        var opts = new CSharpCompilationOptions(exe ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithAllowUnsafe(true)
            .WithNullableContextOptions(NullableContextOptions.Enable)
            .WithSpecificDiagnosticOptions(diag);
        var refs = Refs.Value.Concat((extraRefs ?? []).Select(r => (MetadataReference)MetadataReference.CreateFromFile(Path.GetFullPath(r))));
        var comp = CSharpCompilation.Create(assemblyName, trees, refs, opts);
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
