using System.Text;
using System.Text.RegularExpressions;

namespace Alice.Cli.Repl;

/// <summary>
/// Tolerant, line-local syntax highlighter for the REPL (works on incomplete input, unlike the compiler lexer).
/// Output only adds ANSI escapes, so the visible text is unchanged.
/// </summary>
public static class Highlighter
{
    public const string Reset = "\u001b[0m";
    public const string Keyword = "\u001b[35m";
    public const string Function = "\u001b[94m";
    public const string Type = "\u001b[36m";
    public const string String = "\u001b[32m";
    public const string Number = "\u001b[33m";
    public const string Symbol = "\u001b[93m";
    public const string Comment = "\u001b[90m";
    public const string Ivar = "\u001b[96m";
    public const string Constant = "\u001b[91m";
    public const string Match = "\u001b[1;4m";

    static readonly HashSet<string> Keywords = ("def class module struct enum interface record lib fun if elsif else unless while until for in do end case when then " +
        "begin rescue ensure return break next yield self super and or not require using include extend getter setter property " +
        "spawn async await val var raise loop lambda proc describe context it expect macro static private protected public abstract").Split(' ').ToHashSet();

    static readonly HashSet<string> Constants = ["nil", "true", "false"];

    static readonly Regex Number_ = new(@"\G(0x[0-9A-Fa-f_]+|0b[01_]+|\d[\d_]*(\.\d[\d_]*)?([eE][+-]?\d+)?)(_?[iuf](8|16|32|64))?");
    static readonly Regex Ident = new(@"\G[A-Za-z_][\w]*[?!]?");

    enum K { Plain, Kw, Fn, Ty, Str, Num, Sym, Com, Ivar, Const }

    /// <summary>Highlights `src`; `mark` (an index) is underlined, used for the bracket matching the cursor.</summary>
    public static string Highlight(string src, int mark = -1)
    {
        var kinds = Classify(src);
        var sb = new StringBuilder(src.Length * 2);
        K cur = K.Plain;
        for (int i = 0; i < src.Length; i++)
        {
            if (kinds[i] != cur || i == mark || i - 1 == mark)
            {
                sb.Append(Reset).Append(Color(kinds[i]));
                cur = kinds[i];
            }
            if (i == mark) sb.Append(Match);
            sb.Append(src[i]);
        }
        if (src.Length > 0) sb.Append(Reset);
        return sb.ToString();
    }

    static string Color(K k) => k switch
    {
        K.Kw => Keyword, K.Fn => Function, K.Ty => Type, K.Str => String, K.Num => Number,
        K.Sym => Symbol, K.Com => Comment, K.Ivar => Ivar, K.Const => Constant, _ => "",
    };

    static K[] Classify(string s)
    {
        var k = new K[s.Length];
        int i = 0;
        string? prevWord = null;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '#') { Fill(k, i, s.Length, K.Com); break; }
            if (c is '"' or '\'' or '`') { i = StringLit(s, k, i); prevWord = null; continue; }
            if (c == ':' && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '_') && (i == 0 || !char.IsLetterOrDigit(s[i - 1]) && s[i - 1] != ':') && !(i > 0 && s[i - 1] == ' ' && i + 1 < s.Length && char.IsUpper(s[i + 1])))
            {
                var m = Ident.Match(s, i + 1);
                Fill(k, i, i + 1 + m.Length, K.Sym); i += 1 + m.Length; continue;
            }
            if (c == '@')
            {
                int j = i + 1; while (j < s.Length && s[j] == '@') j++;
                var m = Ident.Match(s, j);
                if (m.Success) { Fill(k, i, j + m.Length, K.Ivar); i = j + m.Length; continue; }
            }
            if (char.IsDigit(c) && (i == 0 || !char.IsLetter(s[i - 1]) && s[i - 1] != '_'))
            {
                var m = Number_.Match(s, i);
                Fill(k, i, i + m.Length, K.Num); i += Math.Max(1, m.Length); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var m = Ident.Match(s, i);
                string w = m.Value;
                int end = i + w.Length;
                K kind = Keywords.Contains(w) ? K.Kw
                    : Constants.Contains(w) ? K.Const
                    : char.IsUpper(w[0]) ? K.Ty
                    : prevWord == "def" || (end < s.Length && s[end] == '(') ? K.Fn
                    : K.Plain;
                Fill(k, i, end, kind);
                i = end;
                // `def self.name`: keep looking for the function name after `self.`
                if (prevWord == "def" && w == "self" && i < s.Length && s[i] == '.') { i++; continue; }
                prevWord = w;
                continue;
            }
            if (!char.IsWhiteSpace(c)) prevWord = null;
            i++;
        }
        return k;
    }

    /// <summary>A string literal from `start`; `#{...}` inside double quotes is highlighted as code.</summary>
    static int StringLit(string s, K[] k, int start)
    {
        char q = s[start];
        int i = start + 1;
        k[start] = K.Str;
        while (i < s.Length)
        {
            if (s[i] == '\\') { Fill(k, i, Math.Min(s.Length, i + 2), K.Str); i += 2; continue; }
            if (q == '"' && s[i] == '#' && i + 1 < s.Length && s[i + 1] == '{')
            {
                int depth = 1, j = i + 2;
                while (j < s.Length && depth > 0) { if (s[j] == '{') depth++; else if (s[j] == '}') depth--; j++; }
                int innerEnd = depth == 0 ? j - 1 : j;
                Fill(k, i, i + 2, K.Sym);
                var inner = Classify(s[(i + 2)..innerEnd]);
                Array.Copy(inner, 0, k, i + 2, inner.Length);
                if (depth == 0) k[j - 1] = K.Sym;
                i = j;
                continue;
            }
            k[i] = K.Str;
            if (s[i] == q) return i + 1;
            i++;
        }
        return i;
    }

    static void Fill(K[] k, int from, int to, K v) { for (int i = from; i < Math.Min(to, k.Length); i++) k[i] = v; }

    /// <summary>Index of the bracket matching the one at `pos`, skipping strings and comments; -1 if none.</summary>
    public static int MatchingBracket(string s, int pos)
    {
        if (pos < 0 || pos >= s.Length) return -1;
        var kinds = Classify(s);
        if (kinds[pos] is K.Str or K.Com) return -1;
        const string open = "([{", close = ")]}";
        int o = open.IndexOf(s[pos]), c = close.IndexOf(s[pos]);
        if (o < 0 && c < 0) return -1;
        int dir = o >= 0 ? 1 : -1;
        char me = s[pos], other = o >= 0 ? close[o] : open[c];
        int depth = 0;
        for (int i = pos; i >= 0 && i < s.Length; i += dir)
        {
            if (kinds[i] is K.Str or K.Com) continue;
            if (s[i] == me) depth++;
            else if (s[i] == other && --depth == 0) return i;
        }
        return -1;
    }

    static readonly Regex Ansi = new("\u001b\\[[0-9;]*[A-Za-z]");

    public static string StripAnsi(string s) => Ansi.Replace(s, "");
}
