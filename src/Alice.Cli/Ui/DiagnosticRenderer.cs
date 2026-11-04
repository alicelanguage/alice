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
    /// <summary>Set by the process entry (Program.cs) when stdout is not a terminal (MSBuild Exec, CI, pipes):
    /// diagnostics then go to stderr as plain text, so a build log always carries the code, file:line and message
    /// (Spectre's markup to a redirected stdout lost them on Windows runners). In-process callers keep AnsiConsole.</summary>
    public static bool Plain;

    public static void Render(IEnumerable<Diagnostic> diags, IReadOnlyDictionary<string, string>? sources = null)
    {
        int errors = 0;
        Diagnostic? first = null;
        foreach (var d in diags)
        {
            if (d.Severity == "error") { errors++; first ??= d; }
            RenderOne(d, sources);
        }
        if (errors == 0) return;
        // the summary repeats the first error: a log that keeps only the last line still says what failed
        string summary = T("aborting", errors) + (first != null ? $": {Summary(first)}" : "");
        if (Plain) Console.Error.WriteLine($"{T("error")}: {summary}");
        else Ui.Error(summary);
    }

    /// <summary>`AL0101 src/main.al:2:5: message` (no location when the diagnostic has none).</summary>
    public static string Summary(Diagnostic d) =>
        d.Line > 0 ? $"{d.Code} {Relative(d.File)}:{d.Line}:{d.Col}: {d.Message}" : $"{d.Code}: {d.Message}";

    static void RenderOne(Diagnostic d, IReadOnlyDictionary<string, string>? sources)
    {
        string sev = d.Severity == "warning" ? T("warning") : T("error");
        string color = d.Severity == "warning" ? "yellow" : "red";
        void Line(string markup, string plain)
        {
            if (Plain) Console.Error.WriteLine(plain);
            else AnsiConsole.MarkupLine(markup);
        }
        Line($"[bold {color}]{sev}[[{d.Code}]][/][bold]: {Markup.Escape(d.Message)}[/]", $"{sev}[{d.Code}]: {d.Message}");
        if (d.Line <= 0) return;
        string gutter = new(' ', d.Line.ToString().Length);
        string? line = SourceLine(d.File, d.Line, sources);
        if (line != null) d = d with { Col = AliceColumn(d.Code, d.Message, d.Col, line) };
        string at = $"{Relative(d.File)}:{d.Line}:{d.Col}";
        Line($"[blue]{gutter}-->[/] {Markup.Escape(at)}", $"{gutter}--> {at}");
        if (line != null)
        {
            string text = line.Replace("\t", "    ");
            Line($"[blue]{gutter} |[/]", $"{gutter} |");
            Line($"[blue]{d.Line} |[/] {Markup.Escape(text)}", $"{d.Line} | {text}");
            int col = Math.Max(1, d.Col);
            int len = Math.Max(1, Math.Min(d.Length, Math.Max(1, line.Length - col + 1)));
            if (d.Length <= 1) len = TokenLength(line, col - 1);
            string carets = new string(' ', col - 1) + new string('^', len);
            Line($"[blue]{gutter} |[/] {new string(' ', col - 1)}[bold {color}]{new string('^', len)}[/]", $"{gutter} | {carets}");
        }
        string hintKey = "hint." + d.Code;
        string hint = T(hintKey);
        if (hint != hintKey) Line($"[blue]{gutter} =[/] [bold]{T("help")}[/]: {Markup.Escape(hint)}", $"{gutter} = {T("help")}: {hint}");
        if (Plain) Console.Error.WriteLine();
        else AnsiConsole.WriteLine();
    }

    /// <summary>`#line` maps lines, not columns: an error from the generated C# (AL03xx) carries a C# column. It is
    /// moved to the name the message quotes, as written in Alice (`frob`, or `to_widget` for .NET's `ToWidget`), or
    /// to the line's first token.</summary>
    public static int AliceColumn(string code, string message, int col, string line)
    {
        if (!code.StartsWith("AL03")) return col;
        foreach (var m in Alice.Core.Text.Regex.Cached("'([A-Za-z_][A-Za-z0-9_?!]*)'").MatchAll(message))
        {
            string name = m[1]!;
            string snake = Alice.Core.Text.Regex.Cached("(?<=[a-z0-9])([A-Z])").Replace(name, @"_\1").ToLowerInvariant();
            foreach (var n in new[] { name, snake })
            {
                var hit = Alice.Core.Text.Regex.Cached($@"(?<![A-Za-z0-9_]){Alice.Core.Text.Regex.Escape(n)}(?![A-Za-z0-9_])").Match(line);
                if (hit != null) return hit.Begin(0) + 1;
            }
        }
        int first = line.Length - line.TrimStart().Length;
        return first + 1;
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
