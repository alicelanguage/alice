using System.Security.Cryptography;
using System.Text;

namespace Alice.Compiler.Diagnostics;

/// <summary>
/// Differential harness for the binder's flow checks (D1.12, ADR 0033): with ALICE_FLOW_ORACLE=dir, every C#-path
/// compile records the binder's flow diagnostics (AL0309/AL0310/AL0300 constant overflow) and compares them with the
/// Roslyn diagnostics they replace, by code and line. One line per compile goes to dir/log.tsv; the sources of a
/// compile that differs go to dir/HASH/ for a repro. Off (no cost) when the variable is unset.
/// </summary>
public static class FlowOracle
{
    public static readonly string? Dir = Environment.GetEnvironmentVariable("ALICE_FLOW_ORACLE");
    public static bool On => Dir != null;

    /// <summary>Roslyn ids whose Alice translation the binder owns.</summary>
    public static readonly HashSet<string> RoslynIds = ["CS8600", "CS8601", "CS8602", "CS8603", "CS8604", "CS8625", "CS0165", "CS0220", "CS0221", "CS0031", "CS0020"];

    [ThreadStatic] static List<Diagnostic>? binder;
    [ThreadStatic] static IReadOnlyList<SourceFile>? sources;
    static readonly object Gate = new();

    public static void Binder(IReadOnlyList<Diagnostic> flow, IReadOnlyList<SourceFile> files)
    {
        if (!On) return;
        binder = [.. flow];
        sources = files;
    }

    /// <summary>Compares the recorded binder diagnostics with Roslyn's (all errors of the compilation).</summary>
    public static void Roslyn(IEnumerable<Microsoft.CodeAnalysis.Diagnostic> all)
    {
        if (!On || binder == null || sources == null) return;
        var b = binder; var src = sources;
        binder = null; sources = null;
        var errs = all.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        // the nullable ids are warnings (an assertion) since the binder reports AL0310 itself
        var r = all.Where(d => RoslynIds.Contains(d.Id) && d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning).Select(d =>
        {
            var span = d.Location.GetMappedLineSpan();
            return (Code: CSharpErrors.Translate(d).Code, File: Path.GetFileName(span.Path ?? ""), Line: span.StartLinePosition.Line + 1, Id: d.Id);
        }).ToList();
        bool other = errs.Any(d => !RoslynIds.Contains(d.Id));
        var rk = r.Select(x => (x.Code, x.File, x.Line)).ToHashSet();
        var bk = b.Select(x => (x.Code, File: Path.GetFileName(x.File), x.Line)).ToHashSet();
        var miss = rk.Except(bk).ToList();
        var extra = bk.Except(rk).ToList();
        string status = miss.Count == 0 && extra.Count == 0 ? "same" : "DIFF";
        var text = string.Join("\n\0", src.Select(f => f.Path + "\n" + f.Text));
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        string Fmt(IEnumerable<(string Code, string File, int Line)> xs) => string.Join(",", xs.Select(x => $"{x.Code}@{x.File}:{x.Line}"));
        string ids = string.Join(",", r.Select(x => $"{x.Id}@{x.Line}"));
        var line = $"{status}\t{hash}\tother={(other ? 1 : 0)}\tmiss={Fmt(miss)}\textra={Fmt(extra)}\troslyn={ids}\tfiles={string.Join(";", src.Select(f => f.Path))}\n";
        lock (Gate)
        {
            Directory.CreateDirectory(Dir!);
            File.AppendAllText(Path.Combine(Dir!, "log.tsv"), line);
            if (status == "DIFF")
            {
                var d = Path.Combine(Dir!, hash);
                if (!Directory.Exists(d))
                {
                    Directory.CreateDirectory(d);
                    int i = 0;
                    foreach (var f in src) File.WriteAllText(Path.Combine(d, $"{i++}_{Path.GetFileName(f.Path)}"), f.Text);
                    File.WriteAllText(Path.Combine(d, "diags.txt"), string.Join("\n", errs.Select(e => e.ToString())) + "\n--binder\n" + string.Join("\n", b.Select(x => x.ToString())) + "\n");
                }
            }
        }
    }
}
