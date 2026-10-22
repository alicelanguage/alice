using System.Text.RegularExpressions;
using Alice.Compiler;
using Spectre.Console;

namespace Alice.Cli.Commands;

/// <summary>
/// `alice explain file.al`: per method, the optimizations the compiler applied (forks, parallel loops, owned and stack
/// arrays, in-place maps, shared objects, SIMD, fast native calls). Inference is silent; this makes it visible, so a
/// method that misses an optimization can be fixed or given an explicit contract (`pure`, `par`, `own`, ADR 0017).
/// Read from the generated C#, so it always matches what was emitted.
/// </summary>
public static partial class Explain
{
    static readonly (Regex Pattern, string Finding)[] Findings =
    [
        (new(@"Par\.Fork\("), "fork/join: independent recursive calls run in parallel"),
        (new(@"__parfor_|Par\.Range\(|Par\.Reduce"), "parallel loop"),
        (new(@"Own<\w+>\.Rent\("), "owned array: pooled buffer, no GC allocation once warm"),
        (new(@"for \(int __i\d+ = 0; __i\d+ < \w+\.Length; [^\n]*\n\s*\{\s*\n(\s*#line[^\n]*\n)?\s*var \w+ = (\w+)\[__i\d+\];"), "map updated in place (single owner)"),
        (new(@"(?<!readonly [\w.]+ )__shared_\w+"), "shared immutable object instead of an allocation"),
        (new(@"stackalloc"), "array on the stack"),
        (new(@"\.(reserve|EnsureCapacity)\(global::System\.Math\.Max"), "collection presized for its fill loop"),
        (new(@"Vector(256|512|128)?<"), "SIMD"),
        (new(@"SuppressGCTransition"), "native call without GC transition"),
    ];

    [GeneratedRegex(@"^(?:(?:\[[^\n]*\]|#line[^\n]*)\n)*(?:public|private|internal)(?: static)?(?: unsafe)? [\w<>\[\]?, ().:]+ @?(\w+)\(", RegexOptions.Multiline)]
    private static partial Regex MethodHead();

    /// <summary>Method name -> findings. Helpers (`__fork_f_1`, `__parfor_f_2`) count for the method they came from.</summary>
    public static Dictionary<string, List<string>> Report(string cs)
    {
        var heads = MethodHead().Matches(cs).Cast<Match>().ToList();
        var report = new Dictionary<string, List<string>>();
        for (int i = 0; i < heads.Count; i++)
        {
            string name = heads[i].Groups[1].Value;
            var h = Regex.Match(name, @"^__(?:fork|parfor|fused|parwords|managed)_(\w+?)_?\d*$");
            if (name.StartsWith("__") && !h.Success) continue;
            string owner = h.Success ? h.Groups[1].Value : name;
            int end = i + 1 < heads.Count ? heads[i + 1].Index : cs.Length;
            string body = cs[heads[i].Index..end];
            var list = report.TryGetValue(owner, out var l) ? l : report[owner] = new List<string>();
            if (!h.Success && heads[i].Value.Contains("AggressiveOptimization")) Add(list, "compiled fully optimized from the first call");
            foreach (var (p, f) in Findings) if (p.IsMatch(body)) Add(list, f);
        }
        return report;
    }

    static void Add(List<string> list, string f) { if (!list.Contains(f)) list.Add(f); }

    public static int Run(List<SourceFile> files, List<string> refs)
    {
        var cs = string.Join("\n", AliceCompiler.TranspileAll(files, refs));
        var table = new Table().Border(TableBorder.Rounded).AddColumn("method").AddColumn("optimizations");
        foreach (var (m, f) in Report(cs).OrderBy(kv => kv.Key))
            table.AddRow(Markup.Escape(m), f.Count == 0 ? "[grey]none[/]" : Markup.Escape(string.Join("\n", f)));
        AnsiConsole.Write(table);
        return 0;
    }
}
