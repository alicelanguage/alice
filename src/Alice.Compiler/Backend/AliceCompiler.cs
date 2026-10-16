using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Alice.Compiler;

public sealed record SourceFile(string Path, string Text);

/// <summary>Front door: Alice source -> lowered C# -> Roslyn -> IL assembly on the CLR.</summary>
public static class AliceCompiler
{
    /// <summary>Alice files -> AST -> bound HIR -> MIR -> C# (one unit per file).</summary>
    public static List<string> TranspileAll(IReadOnlyList<SourceFile> files, IEnumerable<string>? extraRefs = null)
    {
        var asts = files.Select(f => Syntax.Parser.ParseFile(f.Text, f.Path)).ToList();
        var binder = new Semantics.Binder(asts, new Semantics.ClrTypes(extraRefs));
        binder.Bind();
        var emitter = new Backend.Emitter(binder);
        return asts.Select(emitter.EmitFile).ToList();
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
                return new Diagnostic("error", d.Id, span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, d.GetMessage(), len);
            }).ToList();
            throw new CompileException(errs);
        }
        return ms.ToArray();
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
