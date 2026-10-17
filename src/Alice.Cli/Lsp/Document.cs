using System.Text;
using System.Text.Json.Nodes;
using Alice.Compiler.Syntax;

namespace Alice.Cli.Lsp;

/// <summary>An open file: text kept as one string (LSP positions are UTF-16 offsets, like .NET strings).</summary>
public sealed class Document(string uri, string text, int version)
{
    public string Uri { get; } = uri;
    public string Text { get; private set; } = text;
    public int Version { get; set; } = version;
    public long AnalysisGeneration { get; set; }
    public List<Document> Related { get; set; } = [];
    public Project? AnalyzedProject { get; set; }
    public int AnalyzedVersion { get; set; } = -1;
    /// <summary>Last AST that parsed; kept while the user types an incomplete line.</summary>
    public FileNode? Ast { get; set; }
    /// <summary>Diagnostics from the last full (Roslyn) check, kept until the next save.</summary>
    public List<Alice.Compiler.Diagnostic> DeepDiagnostics { get; set; } = new();

    public string Path => UriToPath(Uri);

    public static string UriToPath(string uri) =>
        uri.StartsWith("file://") ? new Uri(uri).LocalPath : uri;

    public void Apply(JsonObject change)
    {
        if (change["range"] is not JsonObject range) { Text = change["text"]!.GetValue<string>(); return; }
        int start = Offset(range["start"]!), end = Offset(range["end"]!);
        Text = string.Concat(Text.AsSpan(0, start), change["text"]!.GetValue<string>(), Text.AsSpan(end));
    }

    public int Offset(JsonNode pos) => Offset(pos["line"]!.GetValue<int>(), pos["character"]!.GetValue<int>());

    public int Offset(int line, int character)
    {
        line = Math.Max(0, line);
        character = Math.Max(0, character);
        int i = 0;
        for (int l = 0; l < line; l++)
        {
            int nl = Text.IndexOf('\n', i);
            if (nl < 0) return Text.Length;
            i = nl + 1;
        }
        int lineEnd = Text.IndexOf('\n', i);
        if (lineEnd < 0) lineEnd = Text.Length;
        return Math.Min(i + character, lineEnd);
    }

    public string Line(int line)
    {
        int start = Offset(line, 0);
        int end = Text.IndexOf('\n', start);
        return Text[start..(end < 0 ? Text.Length : end)].TrimEnd('\r');
    }

    /// <summary>Word (identifier, `?`/`!` suffix, dotted receiver) around a position.</summary>
    public (string Word, string Qualified) WordAt(int line, int character)
    {
        var text = Line(line);
        int s = Math.Clamp(character, 0, text.Length), e = s;
        while (s > 0 && IsIdent(text[s - 1])) s--;
        while (e < text.Length && IsIdent(text[e])) e++;
        if (e < text.Length && text[e] is '?' or '!') e++;
        int q = s;
        while (q > 1 && text[q - 1] == '.' && IsIdent(text[q - 2]))
        {
            q--;
            while (q > 0 && IsIdent(text[q - 1])) q--;
        }
        return (text[s..e], text[q..e]);
    }

    public static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    /// <summary>Line index where a new `using` belongs: after the last existing `using`/`require`, else 0.</summary>
    public int UsingInsertLine()
    {
        int line = 0, i = 0;
        foreach (var raw in Text.Split('\n'))
        {
            var t = raw.TrimStart();
            if (t.StartsWith("using ") || t.StartsWith("require ")) line = i + 1;
            else if (t.Length > 0 && !t.StartsWith('#')) break;
            i++;
        }
        return line;
    }
}
