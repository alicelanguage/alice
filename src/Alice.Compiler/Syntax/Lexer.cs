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
    int depth;
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

    /// <summary>%w[a b c] -> ["a", "b", "c"] (and %i[] for symbols, which Alice represents as strings).</summary>
    void WordArray(int l, int c)
    {
        Adv(); Adv();
        char close = src[pos] == '[' ? ']' : ')';
        Adv();
        int s = pos;
        while (pos < src.Length && src[pos] != close) Adv();
        if (pos >= src.Length) throw Err("unterminated %w literal");
        var words = src.Substring(s, pos - s).Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        Adv();
        Add("op", "[", l, c);
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0) Add("op", ",", l, c);
            Add("str", words[i].Replace("\\", "\\\\").Replace("\"", "\\\""), l, c);
        }
        Add("op", "]", l, c);
    }

    /// <summary>
    /// <<~EOS (indentation stripped) / <<-EOS (terminator may be indented). The body is lifted out of the source:
    /// the rest of the opener line keeps lexing, then lexing resumes after the terminator line.
    /// </summary>
    void Heredoc(int l, int c)
    {
        bool squiggly = At(2) == '~';
        Adv(); Adv(); Adv();
        int s = pos;
        while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) Adv();
        string id = src.Substring(s, pos - s);
        int eol = src.IndexOf('\n', pos);
        if (eol < 0) throw Err($"heredoc {id} has no body");
        var lines = new List<string>();
        int k = eol + 1;
        int after = -1;
        while (k <= src.Length)
        {
            int e = src.IndexOf('\n', k);
            string ln = e < 0 ? src[k..] : src[k..e];
            if (ln.Trim() == id) { after = e < 0 ? src.Length : e + 1; break; }
            lines.Add(ln.TrimEnd('\r'));
            if (e < 0) break;
            k = e + 1;
        }
        if (after < 0) throw Err($"heredoc terminator {id} not found");
        if (squiggly)
        {
            int indent = lines.Where(x => x.Trim().Length > 0).Select(x => x.Length - x.TrimStart().Length).DefaultIfEmpty(0).Min();
            lines = lines.Select(x => x.Length >= indent ? x[indent..] : x.TrimStart()).ToList();
        }
        string body = string.Join("\n", lines) + "\n";
        Add("str", body.Replace("\"", "\\\""), l, c);
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

    void Chr(int l, int c)
    {
        var sb = new StringBuilder("'");
        Adv();
        while (pos < src.Length && src[pos] != '\'')
        {
            if (src[pos] == '\\') { sb.Append(src[pos]); Adv(); }
            sb.Append(src[pos]); Adv();
        }
        if (pos >= src.Length) throw new CompileException($"{file}:{l}:{c}: unterminated char literal");
        Adv();
        sb.Append('\'');
        Add("char", sb.ToString(), l, c);
    }

    void Op(int l, int c)
    {
        if (src[pos] == '<' && At(1) == '<' && (At(2) == '~' || At(2) == '-') && char.IsUpper(At(3))) { Heredoc(l, c); return; }
        if (src[pos] == '%' && (At(1) == 'w' || At(1) == 'i') && (At(2) == '[' || At(2) == '(') && SymbolContext()) { WordArray(l, c); return; }
        if (src[pos] == '/' && RegexContext()) { Regex(l, c); return; }
        if (src[pos] == ':' && (char.IsLetter(At(1)) || At(1) == '_') && SymbolContext())
        {
            Adv();
            int s = pos;
            while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) Adv();
            if (At(0) == '?' || At(0) == '!') Adv();
            Add("sym", src.Substring(s, pos - s), l, c);
            return;
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
        if (op == "(" || op == "[" || op == "@[") depth++;
        if ((op == ")" || op == "]") && depth > 0) depth--;
        Add("op", op, l, c);
    }
}
