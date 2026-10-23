using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alice.Compiler;

namespace Alice.Cli.Lsp;

/// <summary>
/// Find references and rename. Matches come from the Alice lexer, so strings and comments are never touched, except
/// names inside `#{...}` interpolations. A name declared at the top level (type, method, constant) is project-wide;
/// any other name is a local and stays inside its enclosing `def`.
/// </summary>
public static class References
{
    static readonly Regex Ident = new(@"^@{0,2}[A-Za-z_][A-Za-z0-9_]*[?!]?$");

    /// <summary>Occurrences of <paramref name="name"/> in a document: (line, column, length), 0-based.</summary>
    public static List<(int Line, int Col, int Len)> Occurrences(Document doc, string name, (int From, int To)? lines = null)
    {
        var hits = new List<(int, int, int)>();
        List<Token> toks;
        try { toks = new Lexer(doc.Text, doc.Path, 1).Lex(); }
        catch (Exception) { return hits; }   // unlexable text (mid-edit): no answer rather than a wrong one
        var inHole = new Regex(@"#\{([^}]*)\}");
        var word = new Regex(@"(?<![A-Za-z0-9_@])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])");
        foreach (var t in toks)
        {
            if (t.Kind is "id" or "const" or "ivar" && t.Text == name) hits.Add((t.Line - 1, t.Col - 1, name.Length));
            else if (t.Kind == "str" && t.Text.Contains("#{") && !t.Text.Contains('\n'))
                foreach (Match h in inHole.Matches(t.Text))
                    foreach (Match w in word.Matches(h.Groups[1].Value))
                        hits.Add((t.Line - 1, t.Col - 1 + 1 + h.Groups[1].Index + w.Index, name.Length));   // +1: the opening quote
        }
        return lines is { } r ? hits.Where(h => h.Item1 >= r.From && h.Item1 <= r.To).ToList() : hits;
    }

    /// <summary>The `def ... end` around a line (0-based, inclusive), by indentation.</summary>
    public static (int From, int To)? EnclosingDef(Document doc, int line)
    {
        var def = new Regex(@"^(\s*)(?:(?:private|protected|abstract|async|pure|safe|unsafe|inline|partial)\s+)*def\b");
        for (int l = line; l >= 0; l--)
        {
            var m = def.Match(doc.Line(l));
            if (!m.Success) continue;
            string end = m.Groups[1].Value + "end";
            for (int k = l + 1; ; k++)
            {
                string text = doc.Line(k);
                if (k > line + 100000 || (text.Length == 0 && k >= doc.Text.Count(c => c == '\n'))) return null;
                if (text.TrimEnd() == end) return k >= line ? (l, k) : null;
            }
        }
        return null;
    }

    /// <summary>Documents and line range the name lives in.</summary>
    static IEnumerable<(Document Doc, (int, int)? Lines)> Scope(Document doc, IEnumerable<Document> project, string name, int line)
    {
        if (Features.IsDeclared(doc, name) || project.Any(d => d.Uri != doc.Uri && Features.IsDeclared(d, name)))
        {
            yield return (doc, null);
            foreach (var d in project) if (d.Uri != doc.Uri) yield return (d, null);
            yield break;
        }
        yield return (doc, EnclosingDef(doc, line));
    }

    public static JsonArray Find(Document doc, IEnumerable<Document> project, int line, int ch)
    {
        var arr = new JsonArray();
        string name = doc.WordAt(line, ch).Word;
        if (name.Length == 0) return arr;
        foreach (var (d, lines) in Scope(doc, project, name, line))
            foreach (var (l, c, len) in Occurrences(d, name, lines))
                arr.Add(new JsonObject { ["uri"] = d.Uri, ["range"] = Range(l, c, len) });
        return arr;
    }

    /// <summary>A WorkspaceEdit, or null when the new name is not an identifier.</summary>
    public static JsonObject? Rename(Document doc, IEnumerable<Document> project, int line, int ch, string newName)
    {
        string name = doc.WordAt(line, ch).Word;
        if (name.Length == 0 || !Ident.IsMatch(newName)) return null;
        var changes = new JsonObject();
        foreach (var (d, lines) in Scope(doc, project, name, line))
        {
            var edits = new JsonArray();
            foreach (var (l, c, len) in Occurrences(d, name, lines))
                edits.Add(new JsonObject { ["range"] = Range(l, c, len), ["newText"] = newName });
            if (edits.Count > 0) changes[d.Uri] = edits;
        }
        return new JsonObject { ["changes"] = changes };
    }

    static JsonObject Range(int line, int col, int len) => new()
    {
        ["start"] = new JsonObject { ["line"] = line, ["character"] = col },
        ["end"] = new JsonObject { ["line"] = line, ["character"] = col + len },
    };
}
