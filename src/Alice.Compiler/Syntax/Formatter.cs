using System.Text;

namespace Alice.Compiler.Syntax;

/// <summary>
/// `alice fmt`: canonical layout. Re-indents by block structure (2 spaces), strips trailing whitespace,
/// collapses blank-line runs, ends with one newline. Strings and comments are never inspected as code.
/// Idempotent: Format(Format(x)) == Format(x).
/// </summary>
public static class Formatter
{
    const string Unit = "  ";
    static readonly HashSet<string> Openers = new() { "def", "class", "struct", "module", "interface", "enum", "lib", "namespace", "if", "unless", "while", "until", "for", "case", "begin" };
    static readonly HashSet<string> Midders = new() { "else", "elsif", "when", "rescue", "ensure" };

    public static string Format(string src)
    {
        var lines = src.Replace("\r\n", "\n").Split('\n');
        var outp = new StringBuilder();
        var stack = new List<string>();   // open block kinds
        int blank = 0;
        bool started = false;
        for (int li = 0; li < lines.Length; li++)
        {
            string raw = lines[li];
            string line = raw.Trim();
            if (line.Length == 0)
            {
                if (started) blank++;
                continue;
            }
            if (blank > 0) outp.Append('\n');
            blank = 0;
            started = true;
            string code = CodeOf(line);
            var words = Words(code);
            string first = words.Count > 0 ? words[0] : "";
            int level = stack.Count;
            bool closes = first == "end" || code.StartsWith("}");
            if (closes || Midders.Contains(first)) level = Math.Max(0, level - 1);
            outp.Append(string.Concat(Enumerable.Repeat(Unit, level))).Append(line).Append('\n');

            // update block structure
            if (first == "end" && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            string owner = stack.Count > 0 ? stack[^1] : "";
            foreach (var kw in OpenerKeywords(words, owner))
                if (!ClosesOnSameLine(words, kw)) stack.Add(kw);
            if (first == "record" && RecordHasBody(lines, li)) stack.Add("record");
            if (owner == "interface" && first == "def" && InterfaceDefHasBody(lines, li)) stack.Add("def");   // default method
            // `using r = x` / `await using r = x` resource blocks
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
            bool atStart = i == 0 || (i == 1 && words[0] is "abstract" or "private" or "protected" or "async" or "par" or "pure" or "safe" or "unsafe" or "inline");
            bool asValue = i > 0 && words[i - 1] is "=" or "(" or "," or "+=" or "||=";   // `return if c` is always postfix
            if (!atStart && !asValue) continue;
            if (w == "def" && (owner == "interface" || (i > 0 && words[0] == "abstract"))) continue;   // signatures without bodies
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

    /// <summary>A record has a body when the next non-blank line is a member declaration (Parser.RecordHasBody rule).</summary>
    static bool RecordHasBody(string[] lines, int at)
    {
        for (int k = at + 1; k < lines.Length; k++)
        {
            var t = lines[k].Trim();
            if (t.Length == 0) continue;
            var w = Words(CodeOf(t));
            return w.Count > 0 && w[0] is "def" or "getter" or "property" or "setter" or "include" or "private" or "protected" or "abstract" or "@[" or "attr_accessor" or "attr_reader";
        }
        return false;
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
