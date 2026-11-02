using System.Text;

namespace Alice.Compiler;

/// <summary>Alice lexer. Newlines are statement terminators except inside ()/[] or after a continuing operator.</summary>
public sealed class Lexer
{
    readonly string src;
    readonly string file;
    int pos;
    int line;
    int col = 1;
    /// <summary>Open brackets, innermost last. Newlines inside ( and [ are not statement ends; a { ... } block nested
    /// in them (`Task.Run(->() { a\n b })`) has statements again.</summary>
    readonly Stack<char> open = new();
    int depth => open.Count > 0 && open.Peek() != '{' ? 1 : 0;
    bool space;
    readonly List<Token> toks = new();

    static readonly string[] Ops3 = { "**=", "<=>", "===", "...", "||=", "&&=", "<<=", ">>=" };
    static readonly string[] Ops2 = { "..", "::", "->", "=>", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "<<", ">>", "**", "&.", "@[", "=~", "!~" };
    const string Ops1 = "+-*/%=<>!&|^~.,:;?()[]{}";
    const string Cont = " , . :: + - * / % &+ &- &* ** = == != < > <= >= && || -> => += -= *= /= %= |= &= ^= ||= &&= **= & ^ << >> ? : ";

    public Lexer(string src, string file, int line)
    {
        this.src = src;
        this.file = file;
        this.line = line;
    }

    char At(int k) => pos + k < src.Length ? src[pos + k] : '\0';

    void Adv()
    {
        if (src[pos] == '\n') { line++; col = 1; } else col++;
        pos++;
    }

    void Add(string kind, string text, int l, int c)
    {
        toks.Add(new Token { Kind = kind, Text = text, Line = l, Col = c, Space = space });
        space = false;
    }

    Token? Last() => toks.Count == 0 ? null : toks[toks.Count - 1];

    CompileException Err(string msg) => new($"{file}:{line}:{col}: {msg}");

    int skipAt = -1, skipTo = -1;   // heredoc bodies: when reaching `skipAt` (end of the opener line) jump to `skipTo`

    public List<Token> Lex()
    {
        while (pos < src.Length)
        {
            if (pos == skipAt)
            {
                // jump over the heredoc body; the newline ending the terminator line still ends the statement
                int target = skipTo > 0 && skipTo <= src.Length && src[skipTo - 1] == '\n' ? skipTo - 1 : src.Length;
                for (int k = skipAt; k < target; k++) if (src[k] == '\n') line++;
                pos = target;
                skipAt = -1;
                col = 1;
                if (pos >= src.Length) break;
            }
            char c = src[pos];
            int l = line, cl = col;
            if (c == ' ' || c == '\t' || c == '\r') { Adv(); space = true; }
            else if (c == '\\' && At(1) == '\n') { Adv(); Adv(); space = true; }
            else if (c == '\\' && At(1) == '\r' && At(2) == '\n') { Adv(); Adv(); Adv(); space = true; }
            else if (c == '#') { while (pos < src.Length && src[pos] != '\n') Adv(); }
            else if (c == '\n') { Adv(); Newline(l, cl); }
            else if (char.IsDigit(c)) Number(l, cl);
            else if (char.IsLetter(c) || c == '_') Ident(l, cl);
            else if (c == '@' && At(1) != '[')
            {
                Adv();
                bool cvar = pos < src.Length && src[pos] == '@';   // @@class_var -> ivar "@name" (a static field)
                if (cvar) Adv();
                int s = pos;
                while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) Adv();
                if (pos == s) throw Err("expected instance variable name after '@'");
                Add("ivar", (cvar ? "@" : "") + src.Substring(s, pos - s), l, cl);
            }
            else if (c == '"') Str(l, cl);
            else if (c == '\'') Chr(l, cl);
            else Op(l, cl);
        }
        Token? last = Last();
        if (last != null && last.Kind != "nl") Add("nl", "\n", line, col);
        int el = last == null ? line : last.Line;
        Add("eof", "", el, 1);
        return toks;
    }

    /// <summary>`/re/` starts a regex only in prefix position (after an operator, `(`, `,`, a keyword or line start).</summary>
    bool RegexContext()
    {
        if (At(1) == ' ' || At(1) == '=') return false;
        Token? last = Last();
        if (last == null || last.Kind == "nl") return true;
        if (last.Kind == "op") return last.Text is not (")" or "]" or "}");
        return last.Kind == "id" && space && " if unless when and or not return puts print p elsif while until in ".Contains(" " + last.Text + " ");
    }

    void Regex(int l, int c)
    {
        Adv();
        var sb = new StringBuilder();
        while (pos < src.Length && src[pos] != '/')
        {
            if (src[pos] == '\\') { sb.Append(src[pos]); Adv(); }
            if (pos >= src.Length || src[pos] == '\n') throw Err("unterminated regex literal");
            sb.Append(src[pos]);
            Adv();
        }
        if (pos >= src.Length) throw Err("unterminated regex literal");
        Adv();
        int fs = pos;
        while (pos < src.Length && "imxs".Contains(src[pos])) Adv();
        Add("regex", sb.ToString() + "\u0000" + src.Substring(fs, pos - fs), l, c);
    }

    /// <summary>Closing delimiter of a %-literal; bracket pairs nest, any other punctuation closes itself.</summary>
    static char CloseOf(char open) => open switch { '(' => ')', '[' => ']', '{' => '}', '<' => '>', _ => open };

    /// <summary>The body of a %-literal starting at the opening delimiter (consumed, as is the closer); `\` escapes are kept.</summary>
    string PercentBody(string what)
    {
        char open = src[pos], close = CloseOf(open);
        Adv();
        var sb = new StringBuilder();
        int d = 1;
        while (true)
        {
            if (pos >= src.Length) throw Err($"unterminated {what} literal");
            char ch = src[pos];
            if (ch == '\\' && pos + 1 < src.Length) { sb.Append(ch); Adv(); sb.Append(src[pos]); Adv(); continue; }
            if (ch == close && open != close && --d == 0) { Adv(); break; }
            if (ch == close && open == close) { Adv(); break; }
            if (ch == open && open != close) d++;
            sb.Append(ch);
            Adv();
        }
        return sb.ToString();
    }

    /// <summary>%w[a b c] -> ["a", "b", "c"] (and %i[] for symbols, which Alice represents as strings); any delimiter.</summary>
    void WordArray(int l, int c)
    {
        Adv(); Adv();
        var words = PercentBody("%w").Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        Add("op", "[", l, c);
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0) Add("op", ",", l, c);
            Add("str", words[i].Replace("\\", "\\\\").Replace("\"", "\\\""), l, c);
        }
        Add("op", "]", l, c);
    }

    /// <summary>`%q(raw)` (no interpolation or escapes), `%Q(text #{x})` and `%(text #{x})` (like a "..." string).</summary>
    void PercentString(int l, int c)
    {
        Adv();
        char kind = src[pos] is 'q' or 'Q' ? src[pos] : 'Q';
        if (src[pos] is 'q' or 'Q') Adv();
        char open = src[pos], close = CloseOf(open);
        string body = PercentBody("%" + kind);
        // `\)` inside %( ... ) is the delimiter itself; in %q only `\\` and the delimiters are escapes
        var sb = new StringBuilder();
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length && (body[i + 1] == open || body[i + 1] == close || (kind == 'q' && body[i + 1] == '\\')))
            {
                sb.Append(body[++i]);
                continue;
            }
            if (body[i] == '\\' && i + 1 < body.Length && kind != 'q') { sb.Append(body[i]).Append(body[++i]); continue; }
            sb.Append(body[i]);
        }
        Add("str", kind == 'q' ? Raw(sb.ToString()) : QuoteOutsideHoles(sb.ToString()), l, c);
    }

    /// <summary>`%q`, `%Q`, `%(`: a literal only where an operand starts (`x %(y)` stays a modulo when x is not a command).</summary>
    bool PercentStringStart()
    {
        if (src[pos] != '%') return false;
        bool letter = At(1) is 'q' or 'Q';
        char open = letter ? At(2) : At(1);
        if (!(open is '(' or '[' or '{' or '<' or '|' or '!' || (letter && open is '/' or '^' or '~'))) return false;
        Token? last = Last();
        if (last == null || last.Kind == "nl") return true;
        if (last.Kind == "op") return last.Text is not (")" or "]" or "}");
        if (letter) return space && last.Kind == "id";
        return last.Kind == "id" && space && " if unless when and or not return puts print p pp elsif while until in raise yield then else ".Contains(" " + last.Text + " ");
    }

    /// <summary>A string token body for text with no escapes or interpolation (`%q`, `'...'`, `<<~'EOT'`).</summary>
    static string Raw(string text) => text.Replace("\\", "\\\\").Replace("#{", "\\#{").Replace("\"", "\\\"");

    /// <summary>Escapes bare `"` of a double-quoted-style body (heredoc, %Q), leaving `\x` escapes and `#{...}` holes intact.</summary>
    static string QuoteOutsideHoles(string text)
    {
        var sb = new StringBuilder();
        int d = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && d == 0) { sb.Append(ch).Append(text[++i]); continue; }
            if (ch == '#' && i + 1 < text.Length && text[i + 1] == '{') { d++; sb.Append("#{"); i++; continue; }
            if (d > 0 && ch == '{') d++;
            if (d > 0 && ch == '}') d--;
            if (ch == '"' && d == 0) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// `<<~EOS` / `<<~'EOS'` / `<<-EOS` / `<<EOS` (plain, only where an operand starts and an `EOS` line follows).
    /// </summary>
    bool HeredocStart()
    {
        if (src[pos] != '<' || At(1) != '<') return false;
        int k = 2;
        bool plain = !(At(2) is '~' or '-');
        if (!plain) k = 3;
        bool quoted = At(k) is '\'' or '"';
        char first = quoted ? At(k + 1) : At(k);
        if (!char.IsUpper(first)) return false;
        if (!plain) return true;
        Token? last = Last();
        bool prefix = last == null || last.Kind == "nl" || (last.Kind == "op" && last.Text is not (")" or "]" or "}")) || (last.Kind == "id" && space);
        if (!prefix) return false;
        int s = pos + k + (quoted ? 1 : 0), e = s;
        while (e < src.Length && (char.IsLetterOrDigit(src[e]) || src[e] == '_')) e++;
        string id = src[s..e];
        int eol = src.IndexOf('\n', e);
        if (eol < 0) return false;
        foreach (var ln in src[(eol + 1)..].Split('\n'))
            if (ln.TrimEnd('\r') == id) return true;
        return false;
    }

    /// <summary>
    /// <<~EOS (indentation stripped) / <<-EOS (terminator may be indented) / <<EOS (terminator at column 0); a quoted
    /// `'EOS'` opener makes the body raw (no escapes, no interpolation). The body is lifted out of the source:
    /// the rest of the opener line keeps lexing, then lexing resumes after the terminator line.
    /// </summary>
    void Heredoc(int l, int c)
    {
        Adv(); Adv();
        char mode = src[pos] is '~' or '-' ? src[pos] : ' ';
        if (mode != ' ') Adv();
        char quote = src[pos] is '\'' or '"' ? src[pos] : '\0';
        if (quote != '\0') Adv();
        int s = pos;
        while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) Adv();
        string id = src.Substring(s, pos - s);
        if (quote != '\0')
        {
            if (pos >= src.Length || src[pos] != quote) throw Err($"expected closing {quote} after heredoc {id}");
            Adv();
        }
        int eol = src.IndexOf('\n', pos);
        if (eol < 0) throw Err($"heredoc {id} has no body");
        var lines = new List<string>();
        int k = eol + 1;
        int after = -1;
        while (k <= src.Length)
        {
            int e = src.IndexOf('\n', k);
            string ln = e < 0 ? src[k..] : src[k..e];
            if (mode == ' ' ? ln.TrimEnd('\r') == id : ln.Trim() == id) { after = e < 0 ? src.Length : e + 1; break; }
            lines.Add(ln.TrimEnd('\r'));
            if (e < 0) break;
            k = e + 1;
        }
        if (after < 0) throw Err($"heredoc terminator {id} not found");
        if (mode == '~')
        {
            int indent = lines.Where(x => x.Trim().Length > 0).Select(x => x.Length - x.TrimStart().Length).DefaultIfEmpty(0).Min();
            lines = lines.Select(x => x.Length >= indent ? x[indent..] : x.TrimStart()).ToList();
        }
        string body = string.Join("\n", lines) + "\n";
        Add("str", quote == '\'' ? Raw(body) : QuoteOutsideHoles(body), l, c);
        skipAt = eol;
        skipTo = after;
    }

    /// <summary>`:name` is a symbol after whitespace or an opening/separator token (not `a ? b :c`, not `x: 1`).</summary>
    bool SymbolContext()
    {
        Token? last = Last();
        if (last == null || last.Kind == "nl") return true;
        if (last.Kind == "op") return " ( [ { , => = == != << & ".Contains(" " + last.Text + " ");
        return space && last.Kind == "id";
    }

    /// <summary>Like <see cref="SymbolContext"/>, and after any operator (`:a <=> :b`, `x == :+`).</summary>
    bool OpSymbolContext()
    {
        Token? last = Last();
        if (last != null && last.Kind == "op") return last.Text is not (")" or "]" or "}");
        return SymbolContext();
    }

    void Newline(int l, int c)
    {
        Token? last = Last();
        if (depth > 0 || last == null || last.Kind == "nl") return;
        if (last.Kind == "op" && Cont.Contains(" " + last.Text + " ")) return;
        int j = pos;
        while (j < src.Length && (src[j] == ' ' || src[j] == '\t' || src[j] == '\r' || src[j] == '\n')) j++;
        if (j + 1 < src.Length && src[j] == '.' && src[j + 1] != '.') return;
        if (j + 1 < src.Length && src[j] == '&' && src[j + 1] == '.') return;
        Add("nl", "\n", l, c);
    }

    void Number(int l, int c)
    {
        var sb = new StringBuilder();
        string kind = "int";
        if (src[pos] == '0' && (At(1) == 'x' || At(1) == 'b'))
        {
            sb.Append(src[pos]); Adv(); sb.Append(src[pos]); Adv();
            // `_` separates digits (0xFFFF_FFFF) unless it starts a type suffix (0xFF_u8, 0x1_f32)
            bool Suffix() => src[pos] == '_' && ((At(1) is 'i' or 'u' && char.IsDigit(At(2)))
                || (At(1) == 'f' && (At(2), At(3)) is ('3', '2') or ('6', '4') && !char.IsLetterOrDigit(At(4))));
            while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_') && !Suffix())
            {
                if (src[pos] != '_') sb.Append(src[pos]);
                Adv();
            }
        }
        else Number(sb, ref kind);
        string num = sb.ToString();
        bool radix = num.Length > 1 && num[1] is 'x' or 'b';
        if (At(0) == '_' && char.IsLetter(At(1))) Adv();
        int s = pos;
        while (pos < src.Length && char.IsLetterOrDigit(src[pos])) Adv();
        string suf = src.Substring(s, pos - s);
        string text = num;
        if (suf == "i64" || suf == "L") text = num + "L";
        else if (suf == "u64" || suf == "UL") text = num + "UL";
        else if (suf == "u32" || suf == "U") text = num + "U";
        else if (radix && suf is "f32" or "f64") { text = $"(({(suf == "f32" ? "float" : "double")}){num})"; kind = "float"; }   // C# has no hex float literal
        else if (suf == "f32" || suf == "f") { text = num + "f"; kind = "float"; }
        else if (suf == "f64" || suf == "d") { text = num + "d"; kind = "float"; }
        else if (suf == "m") { text = num + "m"; kind = "float"; }
        else if (suf == "u8") text = "((byte)" + num + ")";
        else if (suf == "i8") text = "((sbyte)" + num + ")";
        else if (suf == "i16") text = "((short)" + num + ")";
        else if (suf == "u16") text = "((ushort)" + num + ")";
        else if (suf != "" && suf != "i32") throw Err("unknown number suffix '" + suf + "'");
        Add(kind, text, l, c);
    }

    void Number(StringBuilder sb, ref string kind)
    {
        Digits(sb);
        if (At(0) == '.' && char.IsDigit(At(1)))
        {
            kind = "float";
            sb.Append('.'); Adv();
            Digits(sb);
        }
        if ((At(0) == 'e' || At(0) == 'E') && (char.IsDigit(At(1)) || ((At(1) == '-' || At(1) == '+') && char.IsDigit(At(2)))))
        {
            kind = "float";
            sb.Append('e'); Adv();
            if (At(0) == '-' || At(0) == '+') { sb.Append(src[pos]); Adv(); }
            Digits(sb);
        }
    }

    void Digits(StringBuilder sb)
    {
        while (pos < src.Length && (char.IsDigit(src[pos]) || (src[pos] == '_' && char.IsDigit(At(1)))))
        {
            if (src[pos] != '_') sb.Append(src[pos]);
            Adv();
        }
    }

    void Ident(int l, int c)
    {
        int s = pos;
        while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) Adv();
        if ((At(0) == '?' || At(0) == '!') && At(1) != '=') Adv();
        else if ((At(0) == '?' || At(0) == '!') && At(1) == '=' && At(2) == '=') Adv();
        string text = src.Substring(s, pos - s);
        Add(char.IsUpper(text[0]) ? "const" : "id", text, l, c);
    }

    void Str(int l, int c)
    {
        var sb = new StringBuilder();
        Adv();
        while (true)
        {
            if (pos >= src.Length) throw new CompileException($"{file}:{l}:{c}: unterminated string");
            char ch = src[pos];
            if (ch == '"') { Adv(); break; }
            if (ch == '\\') { sb.Append(ch); Adv(); sb.Append(src[pos]); Adv(); continue; }
            if (ch == '#' && At(1) == '{')
            {
                sb.Append("#{"); Adv(); Adv();
                int d = 1;
                while (d > 0)
                {
                    if (pos >= src.Length) throw new CompileException($"{file}:{l}:{c}: unterminated interpolation");
                    char x = src[pos];
                    if (x == '"')
                    {
                        sb.Append(x); Adv();
                        while (pos < src.Length && src[pos] != '"')
                        {
                            if (src[pos] == '\\') { sb.Append(src[pos]); Adv(); }
                            sb.Append(src[pos]); Adv();
                        }
                        sb.Append('"'); Adv();
                        continue;
                    }
                    if (x == '{') d++;
                    if (x == '}') d--;
                    sb.Append(x); Adv();
                }
                continue;
            }
            sb.Append(ch); Adv();
        }
        Add("str", sb.ToString(), l, c);
    }

    /// <summary>
    /// `'a'`, `'\n'`, `'\u{41}'` is a Char (Crystal). A quoted text of any other length is Ruby's single-quoted
    /// String: no interpolation, `\\` and `\'` are the only escapes.
    /// </summary>
    void Chr(int l, int c)
    {
        var sb = new StringBuilder();
        Adv();
        while (pos < src.Length && src[pos] != '\'' && src[pos] != '\n')
        {
            if (src[pos] == '\\' && pos + 1 < src.Length) { sb.Append(src[pos]); Adv(); }
            sb.Append(src[pos]); Adv();
        }
        if (pos >= src.Length || src[pos] != '\'') throw new CompileException($"{file}:{l}:{c}: unterminated char literal");
        Adv();
        string body = sb.ToString();
        if (IsOneChar(body))
        {
            int k = 0;
            string cs;
            try { cs = body[0] == '\\' ? CsEscape(body, ref k) : body; }
            catch (CompileException e) { throw new CompileException($"{file}:{l}:{c}: {e.Message}"); }
            if (cs.StartsWith("\\U")) throw new CompileException($"{file}:{l}:{c}: unexpected character literal '{body}': it does not fit in a Char (UTF-16); use a String \"{body}\"");
            Add("char", "'" + (cs == "\"" ? "\\\"" : cs.Length == 0 ? "\\n" : cs) + "'", l, c);
            return;
        }
        var text = new StringBuilder();
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length && body[i + 1] is '\\' or '\'') { text.Append(body[++i]); continue; }
            text.Append(body[i]);
        }
        Add("str", Raw(text.ToString()), l, c);
    }

    /// <summary>
    /// The C# spelling of the Ruby/Crystal escape at `s[k]` (a backslash); advances `k` past it. `\u{1F600}` and
    /// `\u{41 42}` become `\U0001F600` / two `\uXXXX`, octal `\101` and `\x41` become `A`, `\e` is ESC,
    /// `\s` a space, a backslash-newline nothing, and an unknown `\c` is `c` (as in Ruby).
    /// </summary>
    public static string CsEscape(string s, ref int k)
    {
        static string U(int v) => v > 0xFFFF ? $"\\U{v:X8}" : $"\\u{v:X4}";
        char n = k + 1 < s.Length ? s[k + 1] : '\\';
        k += 2;
        switch (n)
        {
            case 'n' or 't' or 'r' or 'a' or 'b' or 'f' or 'v' or '\\' or '"' or '\'':
                return "\\" + n;
            case 'e': return "\\u001b";
            case 's': return " ";
            case '\n': return "";
            case 'u' when k < s.Length && s[k] == '{':
                {
                    int close = s.IndexOf('}', k);
                    if (close < 0) throw new CompileException("unterminated \\u{...} escape");
                    var sb = new StringBuilder();
                    foreach (var hex in s[(k + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v) || v > 0x10FFFF || v is >= 0xD800 and <= 0xDFFF)
                            throw new CompileException($"invalid unicode escape \\u{{{hex}}}: expected a code point up to 10FFFF");
                        sb.Append(U(v));
                    }
                    k = close + 1;
                    return sb.ToString();
                }
            case 'u':
                {
                    int e = k;
                    while (e < s.Length && e < k + 4 && char.IsAsciiHexDigit(s[e])) e++;
                    if (e - k != 4) throw new CompileException("invalid unicode escape: expected \\uXXXX or \\u{X...}");
                    string r = "\\u" + s[k..e];
                    k = e;
                    return r;
                }
            case 'x':
                {
                    int e = k;
                    while (e < s.Length && e < k + 2 && char.IsAsciiHexDigit(s[e])) e++;
                    if (e == k) throw new CompileException("invalid hex escape: expected \\xHH");
                    int v = Convert.ToInt32(s[k..e], 16);
                    k = e;
                    return U(v);
                }
            case >= '0' and <= '7':
                {
                    int e = k - 1;
                    while (e < s.Length && e < k + 2 && s[e] is >= '0' and <= '7') e++;
                    int v = Convert.ToInt32(s[(k - 1)..e], 8);
                    k = e;
                    return v == 0 ? "\\0" : U(v);
                }
            default:
                return n.ToString();
        }
    }

    /// <summary>One character or one escape (`\n`, `A`, `\u{1F}`, `\x41`, `\101`).</summary>
    static bool IsOneChar(string b)
    {
        if (b.Length == 1) return true;
        if (b.Length < 2 || b[0] != '\\') return false;
        if (b.Length == 2) return true;
        if (b[1] == 'u') return b.Length == 6 || (b[2] == '{' && b[^1] == '}' && !b.Contains(' '));
        if (b[1] == 'x') return b.Length <= 4 && b[2..].All(char.IsAsciiHexDigit);
        return b.Length <= 4 && b[1..].All(ch => ch is >= '0' and <= '7');
    }

    /// <summary>Operator method names lexed as one token after `def` / `def self.` (`def []=(i, v)`, `def -@`).</summary>
    static readonly string[] DefOps = ["[]=", "[]", "===", "<=>", "==", "=~", "!=", "<<", ">>", "<=", ">=", "**", "+@", "-@", "~@", "!@", "+", "-", "*", "/", "%", "<", ">", "!", "~", "&", "|", "^"];

    /// <summary>Operator symbols `:+`, `:<=>`, `:[]` (`inject(:+)`, `send(:<<, x)`).</summary>
    static readonly string[] SymOps = ["[]=", "[]", "===", "<=>", "==", "=~", "!=", "<<", ">>", "<=", ">=", "**", "+@", "-@", "+", "-", "*", "/", "%", "<", ">", "!", "~", "&", "|", "^"];

    bool AfterDef()
    {
        int n = toks.Count;
        if (n >= 1 && toks[n - 1] is { Kind: "id", Text: "def" }) return true;
        return n >= 3 && toks[n - 1] is { Kind: "op", Text: "." } && toks[n - 2] is { Kind: "id", Text: "self" } && toks[n - 3] is { Kind: "id", Text: "def" };
    }

    string? MatchOp(string[] ops)
    {
        foreach (var o in ops) if (string.CompareOrdinal(src, pos, o, 0, o.Length) == 0 && pos + o.Length <= src.Length) return o;
        return null;
    }

    void Op(int l, int c)
    {
        if (AfterDef() && MatchOp(DefOps) is { } dop)
        {
            for (int i = 0; i < dop.Length; i++) Adv();
            Add("op", dop, l, c);
            return;
        }
        if (HeredocStart()) { Heredoc(l, c); return; }
        if (src[pos] == '%' && At(1) is 'w' or 'i' or 'W' or 'I' && At(2) is '[' or '(' or '{' or '<' or '|' && SymbolContext()) { WordArray(l, c); return; }
        if (PercentStringStart()) { PercentString(l, c); return; }
        if (src[pos] == '/' && RegexContext()) { Regex(l, c); return; }
        if (src[pos] == ':' && (char.IsLetter(At(1)) || At(1) == '_') && OpSymbolContext())
        {
            Adv();
            int s = pos;
            while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) Adv();
            if (At(0) == '?' || At(0) == '!') Adv();
            Add("sym", src.Substring(s, pos - s), l, c);
            return;
        }
        if (src[pos] == ':' && At(1) != ':' && OpSymbolContext())
        {
            pos++;
            string? so = MatchOp(SymOps);
            pos--;
            // `:-1` / `:+x` are not symbols (`c ? a :-1`)
            if (so != null && !(so is "+" or "-" or "*" or "&" && char.IsLetterOrDigit(At(1 + so.Length))))
            {
                for (int i = 0; i <= so.Length; i++) Adv();
                Add("sym", so, l, c);
                return;
            }
        }
        string? op = null;
        foreach (var o in Ops3) if (string.CompareOrdinal(src, pos, o, 0, 3) == 0) { op = o; break; }
        if (op == null) foreach (var o in Ops2) if (string.CompareOrdinal(src, pos, o, 0, 2) == 0) { op = o; break; }
        // wrapping arithmetic `&+ &- &*` (but `&->` stays a block-pass of a lambda)
        if (op == null && At(0) == '&' && At(1) is '+' or '-' or '*' && !(At(1) == '-' && At(2) == '>')) op = src.Substring(pos, 2);
        if (op == null && Ops1.Contains(src[pos])) op = src[pos].ToString();
        if (op == null) throw Err($"unexpected character '{src[pos]}'");
        for (int i = 0; i < op.Length; i++) Adv();
        if (op == ";")
        {
            Token? last = Last();
            if (last != null && last.Kind != "nl") Add("nl", "\n", l, c);
            return;
        }
        if (op == "(" || op == "[" || op == "@[") open.Push('(');
        else if (op == "{" || op == "#{") open.Push('{');
        else if ((op == ")" || op == "]" || op == "}") && open.Count > 0) open.Pop();
        Add("op", op, l, c);
    }
}
