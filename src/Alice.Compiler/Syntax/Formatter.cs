using System.Text;

namespace Alice.Compiler.Syntax;

/// <summary>
/// `alice fmt`: canonical layout. Re-indents by block structure (2 spaces), strips trailing whitespace,
/// collapses blank-line runs, ends with one newline. Strings and comments are never inspected as code, and heredoc
/// bodies (with their terminator line) are kept byte for byte.
/// Idempotent: Format(Format(x)) == Format(x).
/// </summary>
public static class Formatter
{
    const string Unit = "  ";
    static readonly HashSet<string> Openers = new() { "def", "class", "struct", "module", "interface", "enum", "lib", "namespace", "if", "unless", "while", "until", "for", "case", "begin" };
    static readonly HashSet<string> Midders = new() { "else", "elsif", "when", "rescue", "ensure" };

    public static string Format(string src)
    {
        var text = src.Replace("\r\n", "\n");
        if (text.EndsWith('\n')) text = text[..^1];   // the final newline ends the last line, it is no empty line (a heredoc opener at EOF stayed open over it, fuzz P4)
        var lines = text.Split('\n');
        var outp = new StringBuilder();
        var stack = new List<string>();   // open block kinds
        int blank = 0;
        bool started = false;
        var heredocs = new Queue<(string Id, bool Plain)>();   // opened on an earlier line, bodies still to copy
        bool inString = false;   // a "..." literal is open across the line break: its lines are text (fuzz P3)
        for (int li = 0; li < lines.Length; li++)
        {
            string raw = lines[li];
            if (inString)
            {
                if (blank > 0) outp.Append('\n', blank);
                blank = 0;
                outp.Append(raw).Append('\n');
                inString = StringOpenAtEnd(raw, true);
                continue;
            }
            if (heredocs.Count > 0)
            {
                var (id, plain) = heredocs.Peek();
                if (blank > 0) outp.Append('\n', 1);
                blank = 0;
                outp.Append(raw).Append('\n');
                if (plain ? raw.TrimEnd('\r') == id : raw.Trim() == id) heredocs.Dequeue();
                continue;
            }
            bool opens = heredocs.Count == 0 && StringOpenAtEnd(raw, false);
            string line = opens ? raw.TrimStart() : raw.Trim();   // trailing spaces inside the open string are its text
            if (line.Length == 0)
            {
                if (started) blank++;
                continue;
            }
            if (blank > 0) outp.Append('\n');
            blank = 0;
            started = true;
            string code = CodeOf(line);
            foreach (var h in HeredocOpeners(code, lines, li)) heredocs.Enqueue(h);
            var words = Words(code);
            string first = words.Count > 0 ? words[0] : "";
            int level = stack.Count;
            bool closes = first == "end" || code.StartsWith("}");
            if (closes || Midders.Contains(first)) level = Math.Max(0, level - 1);
            outp.Append(string.Concat(Enumerable.Repeat(Unit, level))).Append(line).Append('\n');
            inString = opens;

            // update block structure
            if (first == "end" && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            string owner = stack.Count > 0 ? stack[^1] : "";
            foreach (var kw in OpenerKeywords(words, owner))
                if (!ClosesOnSameLine(words, kw)) stack.Add(kw);
            if (first == "record" && RecordHasBody(lines, li)) stack.Add("record");
            if (owner == "interface" && first == "def" && InterfaceDefHasBody(lines, li)) stack.Add("def");   // default method
            // `using r = x` / `await using r = x` resource blocks
            if (first == "lock" && words.Count > 1 && !ClosesOnSameLine(words, "lock")) stack.Add("lock");
            int u = first == "await" ? 1 : 0;
            if (words.Count > u + 2 && words[u] == "using" && words[u + 2] == "=") stack.Add("using");
            if (words.Count > 0 && words[^1] == "do" || EndsWithDoParams(code)) stack.Add("do");
            int braces = code.Count(c => c == '{') - code.Count(c => c == '}');
            if (code.StartsWith("}")) braces++;   // its closing brace was consumed by the dedent above
            for (; braces > 0; braces--) stack.Add("{");
            for (; braces < 0 && stack.Count > 0 && stack[^1] == "{"; braces++) stack.RemoveAt(stack.Count - 1);
            if (code.StartsWith("}") && stack.Count > 0 && stack[^1] == "{") stack.RemoveAt(stack.Count - 1);
        }
        return outp.ToString();
    }

    /// <summary>Opening keywords on this line: at line start, or right after `=`, `(`, `,`, `return` (if/case as values).</summary>
    static IEnumerable<string> OpenerKeywords(List<string> words, string owner)
    {
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            if (w is "unchecked" or "scope" or "unsafe" && words.Count == 1) { yield return w; continue; }   // `unchecked` / `scope` block (a call has arguments or a receiver)
            if (!Openers.Contains(w)) continue;
            bool atStart = i == 0 || (i == 1 && words[0] is "abstract" or "affine" or "private" or "protected" or "async" or "par" or "gpu" or "pure" or "safe" or "unsafe" or "partial" or "inline" or "cm")
                || (i == 2 && words[0] == "cm" && words[1] == "ordered");   // `cm for` / `cm ordered for` (ADR 0039)
            bool asValue = i > 0 && words[i - 1] is "=" or "(" or "," or "+=" or "||=";   // `return if c` is always postfix
            if (!atStart && !asValue) continue;
            if (w == "def" && (owner == "interface" || (i > 0 && words[0] is "abstract" or "partial"))) continue;   // signatures without bodies
            yield return w;
        }
    }

    /// <summary>An interface `def` has a default body when its next non-blank line is not a member or `end`
    /// (Parser.InterfaceBodyAhead rule).</summary>
    static bool InterfaceDefHasBody(string[] lines, int at)
    {
        for (int k = at + 1; k < lines.Length; k++)
        {
            var w = Words(CodeOf(lines[k].Trim()));
            if (w.Count == 0) continue;
            return !(w[0] is "def" or "end" or "getter" or "property" or "setter" or "event" or "abstract" or "private" or "protected" or "async" or "pure" or "safe" or "unsafe" or "inline" || w[0].StartsWith("@["));
        }
        return false;
    }

    /// <summary>A record has a body when the next non-blank line is a member declaration indented under the record
    /// (Parser.RecordHasBody rule: a `def` at the record's column is a top-level method, fuzz P5).</summary>
    static bool RecordHasBody(string[] lines, int at)
    {
        int col = Indent(lines[at]);
        for (int k = at + 1; k < lines.Length; k++)
        {
            var t = lines[k].Trim();
            if (t.Length == 0) continue;
            if (Indent(lines[k]) <= col) return false;
            var w = Words(CodeOf(t));
            return w.Count > 0 && w[0] is "def" or "getter" or "property" or "setter" or "include" or "private" or "protected" or "abstract" or "@[" or "attr_accessor" or "attr_reader";
        }
        return false;
    }

    /// <summary>`<<~ID`, `<<-ID`, `<<~'ID'`, and plain `<<ID` when a line `ID` follows (Lexer.HeredocStart rule).</summary>
    static IEnumerable<(string Id, bool Plain)> HeredocOpeners(string code, string[] lines, int at)
    {
        for (int i = code.IndexOf("<<", StringComparison.Ordinal); i >= 0; i = code.IndexOf("<<", i + 2, StringComparison.Ordinal))
        {
            int k = i + 2;
            bool plain = !(k < code.Length && code[k] is '~' or '-');
            if (!plain) k++;
            if (k < code.Length && code[k] is '\'' or '"') k++;
            int s = k;
            if (k >= code.Length || !char.IsUpper(code[k])) continue;
            while (k < code.Length && (char.IsLetterOrDigit(code[k]) || code[k] == '_')) k++;
            string id = code[s..k];
            if (plain && !lines.Skip(at + 1).Any(l => l.TrimEnd('\r') == id)) continue;
            yield return (id, plain);
        }
    }

    static int Indent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] is ' ' or '\t') n++;
        return n;
    }

    /// <summary>Whether a double-quoted string is still open at the end of <paramref name="line"/>, which starts inside
    /// one when <paramref name="inside"/>. Comments, char/single-quoted literals, %-literals and `#{...}` holes are
    /// skipped; a heredoc opener ends the scan (its body is the heredoc queue's).</summary>
    static bool StringOpenAtEnd(string line, bool inside)
    {
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inside)
            {
                if (c == '\\') { i++; continue; }
                if (depth == 0 && c == '#' && i + 1 < line.Length && line[i + 1] == '{') { depth++; i++; continue; }
                if (depth > 0) { if (c == '{') depth++; else if (c == '}') depth--; continue; }
                if (c == '"') inside = false;
                continue;
            }
            if (c == '#') return false;
            if (c == '%' && PercentOpen(line, i) >= 0) return false;
            if (c == '\'')
            {
                int e = line.IndexOf('\'', i + 1);
                if (e < 0) return false;
                i = e;
                continue;
            }
            if (c == '<' && i + 2 < line.Length && line[i + 1] == '<' && line[i + 2] is '~' or '-' or '"' or '\'') return false;
            if (c == '"') inside = true;
        }
        return inside;
    }

    static bool ClosesOnSameLine(List<string> words, string kw) => words.Count > 1 && words[^1] == "end" && words.IndexOf(kw) < words.Count - 1;

    static bool EndsWithDoParams(string code)
    {
        int bar = code.LastIndexOf("do |", StringComparison.Ordinal);
        return bar >= 0 && code.TrimEnd().EndsWith("|") && code.IndexOf('|', bar + 4) == code.TrimEnd().Length - 1;
    }

    /// <summary>The line with string/char literal contents and comments removed.</summary>
    static string CodeOf(string line)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '#') break;
            if (c == '%' && PercentOpen(line, i) is int open and > 0)
            {
                // %w[...] / %q(...) / %(...): the contents are text, not code
                char o = line[open], close = o switch { '(' => ')', '[' => ']', '{' => '}', '<' => '>', _ => o };
                int d = 1, j = open + 1;
                for (; j < line.Length && d > 0; j++)
                {
                    if (line[j] == '\\') { j++; continue; }
                    if (line[j] == close && o != close) d--;
                    else if (line[j] == close) d = 0;
                    else if (line[j] == o) d++;
                }
                sb.Append('"').Append('"');
                i = j - 1;
                continue;
            }
            if ((c == '"' || c == '\'') && (sb.ToString().EndsWith("<<") || sb.ToString().EndsWith("<<~") || sb.ToString().EndsWith("<<-")) && line.IndexOf(c, i + 1) is int qe and > 0)
            {
                // `<<~'EOT'`: the quoted heredoc name stays visible (HeredocOpeners)
                sb.Append(line, i, qe - i + 1);
                i = qe;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                char q = c;
                sb.Append(q);
                i++;
                int depth = 0;
                while (i < line.Length)
                {
                    if (line[i] == '\\') { i += 2; continue; }
                    if (q == '"' && line[i] == '#' && i + 1 < line.Length && line[i + 1] == '{') { depth++; i += 2; continue; }
                    if (depth > 0 && line[i] == '}') { depth--; i++; continue; }
                    if (depth == 0 && line[i] == q) break;
                    i++;
                }
                sb.Append(q);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Index of the opening delimiter of a %-literal starting at `at`, or -1.</summary>
    static int PercentOpen(string line, int at)
    {
        int k = at + 1;
        bool letter = k < line.Length && line[k] is 'q' or 'Q' or 'w' or 'W' or 'i' or 'I';
        if (letter) k++;
        if (k >= line.Length || !(line[k] is '(' or '[' or '{' or '<' or '|' or '!')) return -1;
        int b = at - 1;
        while (b >= 0 && line[b] == ' ') b--;
        bool prefix = b < 0 || line[b] is '(' or ',' or '=' or '[' or '{' || (b < at - 1 && char.IsLetter(line[b]));
        return prefix ? k : -1;
    }

    static List<string> Words(string code)
    {
        var words = new List<string>();
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int s = i;
            if (char.IsLetterOrDigit(c) || c == '_' || c == '@')
            {
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '@' or '?' or '!')) i++;
            }
            else if (i + 1 < code.Length && "+=|=".Contains(code.Substring(i, 2)) && code[i + 1] == '=') i += 2;
            else i++;
            words.Add(code.Substring(s, i - s));
        }
        return words;
    }
}
