using Alice.Compiler;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;

namespace Alice.Cli.Ui;

/// <summary>
/// rustc-style diagnostics:
///   error[AL0202]: data race: ...
///     --> src/main.al:2:16
///      |
///    2 | t = spawn { total += 1 }
///      |             ^^^^^
///      = help: send the value through a Channel(T) ...
/// </summary>
public static class DiagnosticRenderer
{
    public static void Render(IEnumerable<Diagnostic> diags, IReadOnlyDictionary<string, string>? sources = null)
    {
        int errors = 0;
        foreach (var d in diags)
        {
            if (d.Severity == "error") errors++;
            RenderOne(d, sources);
        }
        if (errors > 0) Ui.Error(T("aborting", errors));
    }

    static void RenderOne(Diagnostic d, IReadOnlyDictionary<string, string>? sources)
    {
        string sev = d.Severity == "warning" ? T("warning") : T("error");
        string color = d.Severity == "warning" ? "yellow" : "red";
        AnsiConsole.MarkupLine($"[bold {color}]{sev}[[{d.Code}]][/][bold]: {Markup.Escape(d.Message)}[/]");
        if (d.Line <= 0) return;
        string gutter = new(' ', d.Line.ToString().Length);
        AnsiConsole.MarkupLine($"[blue]{gutter}-->[/] {Markup.Escape(Relative(d.File))}:{d.Line}:{d.Col}");
        string? line = SourceLine(d.File, d.Line, sources);
        if (line != null)
        {
            AnsiConsole.MarkupLine($"[blue]{gutter} |[/]");
            AnsiConsole.MarkupLine($"[blue]{d.Line} |[/] {Markup.Escape(line.Replace("\t", "    "))}");
            int col = Math.Max(1, d.Col);
            int len = Math.Max(1, Math.Min(d.Length, Math.Max(1, line.Length - col + 1)));
            if (d.Length <= 1) len = TokenLength(line, col - 1);
            AnsiConsole.MarkupLine($"[blue]{gutter} |[/] {new string(' ', col - 1)}[bold {color}]{new string('^', len)}[/]");
        }
        string hintKey = "hint." + d.Code;
        string hint = T(hintKey);
        if (hint != hintKey) AnsiConsole.MarkupLine($"[blue]{gutter} =[/] [bold]{T("help")}[/]: {Markup.Escape(hint)}");
        AnsiConsole.WriteLine();
    }

    static int TokenLength(string line, int start)
    {
        if (start >= line.Length) return 1;
        int k = start;
        while (k < line.Length && (char.IsLetterOrDigit(line[k]) || line[k] is '_' or '?' or '!' or '@')) k++;
        return Math.Max(1, k - start);
    }

    static string? SourceLine(string file, int line, IReadOnlyDictionary<string, string>? sources)
    {
        string? text = null;
        if (string.IsNullOrEmpty(file)) return null;
        if (sources != null && sources.TryGetValue(file, out var s)) text = s;
        else if (File.Exists(file)) text = File.ReadAllText(file);
        if (text == null) return null;
        var lines = text.Split('\n');
        return line - 1 < lines.Length ? lines[line - 1].TrimEnd('\r') : null;
    }

    static string Relative(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return "<generated>";
            if (!Path.IsPathRooted(path)) return path;
            var rel = Path.GetRelativePath(Environment.CurrentDirectory, path);
            return rel.StartsWith("..") ? path : rel;
        }
        catch (ArgumentException) { return path; }
    }
}
