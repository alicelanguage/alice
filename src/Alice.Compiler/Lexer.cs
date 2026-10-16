using System.Text;

namespace Alice.Compiler;

public sealed class CompileException(string message) : Exception(message);

public sealed class Token
{
    public string Kind = "";   // id const ivar int float str char op nl eof
    public string Text = "";
    public int Line;
    public int Col;
    public bool Space;         // whitespace precedes the token
}

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
    static readonly string[] Ops2 = { "..", "::", "->", "=>", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "<<", ">>", "**", "&.", "@[" };
    const string Ops1 = "+-*/%=<>!&|^~.,:;?()[]{}";
    const string Cont = " , . :: + - * / % ** = == != < > <= >= && || -> => += -= *= /= %= |= &= ^= ||= &&= **= & ^ << >> ? : ";

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

    public List<Token> Lex()
    {
        while (pos < src.Length)
        {
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
                int s = pos;
                while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) Adv();
                if (pos == s) throw Err("expected instance variable name after '@'");
                Add("ivar", src.Substring(s, pos - s), l, cl);
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
            while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_') && !(src[pos] == '_' && char.IsLetter(At(1)) && At(1) != 'e'))
            {
                if (src[pos] != '_') sb.Append(src[pos]);
                Adv();
            }
            Add(kind, sb.ToString(), l, c);
            return;
        }
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
        string num = sb.ToString();
        if (At(0) == '_' && char.IsLetter(At(1))) Adv();
        int s = pos;
        while (pos < src.Length && char.IsLetterOrDigit(src[pos])) Adv();
        string suf = src.Substring(s, pos - s);
        string text = num;
        if (suf == "i64" || suf == "L") text = num + "L";
        else if (suf == "u64" || suf == "UL") text = num + "UL";
        else if (suf == "u32" || suf == "U") text = num + "U";
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
        string? op = null;
        foreach (var o in Ops3) if (string.CompareOrdinal(src, pos, o, 0, 3) == 0) { op = o; break; }
        if (op == null) foreach (var o in Ops2) if (string.CompareOrdinal(src, pos, o, 0, 2) == 0) { op = o; break; }
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
