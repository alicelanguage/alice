using System.Globalization;
using System.Text;

namespace Alice.Core.Toml;

/// <summary>
/// TOML 1.0 (https://toml.io/en/v1.0.0): <see cref="Parse(string)"/> to a <see cref="TomlTable"/> of
/// <see cref="TomlAny"/> values and <see cref="Write"/> back to text. The parser enforces the table-definition rules
/// (no redefinition, inline tables immutable, dotted keys cannot reopen headers) and reports errors with line and
/// column (<see cref="TomlParseException"/>). Comments are not preserved.
/// </summary>
public static class Toml
{
    /// <summary>Parses a TOML document.</summary>
    public static TomlTable Parse(string text) => new TomlParser(text).Document();

    /// <summary>Parses a UTF-8 TOML document (invalid UTF-8 is an error).</summary>
    public static TomlTable Parse(ReadOnlySpan<byte> utf8)
    {
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8); }
        catch (DecoderFallbackException) { throw new TomlParseException("invalid UTF-8", 1, 1); }
        return Parse(text);
    }

    /// <summary>Like <see cref="Parse(string)"/>; false with the error instead of throwing.</summary>
    public static bool TryParse(string text, out TomlTable? table, out TomlParseException? error)
    {
        try { table = Parse(text); error = null; return true; }
        catch (TomlParseException e) { table = null; error = e; return false; }
    }

    /// <summary>Reads and parses a file.</summary>
    public static TomlTable ParseFile(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Writes <paramref name="table"/> as a TOML document (scalars first, then `[tables]` and `[[arrays]]`).</summary>
    public static string Write(TomlTable table) => TomlWriter.Write(table);
}

sealed class TomlParser(string src)
{
    readonly string s = src;
    int p;
    readonly TomlTable root = new();
    TomlTable current = null!;

    TomlParseException Err(string msg, int at = -1)
    {
        if (at < 0) at = p;
        at = Math.Min(at, s.Length);
        int line = 1, col = 1;
        for (int i = 0; i < at; i++) { if (s[i] == '\n') { line++; col = 1; } else col++; }
        return new TomlParseException(msg, line, col);
    }

    bool Eof => p >= s.Length;
    char Cur => p < s.Length ? s[p] : '\0';
    char At(int k) => p + k < s.Length ? s[p + k] : '\0';

    public TomlTable Document()
    {
        current = root;
        if (s.Length > 0 && s[0] == '\uFEFF') p = 1;
        while (true)
        {
            SkipWs();
            if (Eof) break;
            char c = Cur;
            if (c == '#') { Comment(); NewlineOrEof(); continue; }
            if (c == '\n' || c == '\r') { Newline(); continue; }
            if (c == '[') Header();
            else KeyValue(current, inline: false);
            SkipWs();
            if (Cur == '#') Comment();
            NewlineOrEof();
        }
        return root;
    }

    void SkipWs() { while (p < s.Length && (s[p] == ' ' || s[p] == '\t')) p++; }

    void Comment()
    {
        p++;
        while (p < s.Length && s[p] != '\n')
        {
            char c = s[p];
            if (c == '\r') { if (At(1) == '\n') break; throw Err("bare carriage return in comment"); }
            if (IsControl(c)) throw Err("control character in comment");
            p++;
        }
    }

    static bool IsControl(char c) => (c < 0x20 && c != '\t') || c == 0x7F;

    void Newline()
    {
        if (Cur == '\n') { p++; return; }
        if (Cur == '\r' && At(1) == '\n') { p += 2; return; }
        throw Err(Cur == '\r' ? "bare carriage return" : "expected a newline");
    }

    void NewlineOrEof()
    {
        if (Eof) return;
        if (Cur is '\n' or '\r') { Newline(); return; }
        throw Err($"expected end of line, found '{Cur}'");
    }

    // ---- keys ----

    List<string> Key()
    {
        var parts = new List<string>();
        while (true)
        {
            SkipWs();
            parts.Add(SimpleKey());
            SkipWs();
            if (Cur != '.') return parts;
            p++;
        }
    }

    string SimpleKey()
    {
        char c = Cur;
        if (c == '"') { if (At(1) == '"' && At(2) == '"') throw Err("multi-line strings cannot be keys"); return BasicString(); }
        if (c == '\'') { if (At(1) == '\'' && At(2) == '\'') throw Err("multi-line strings cannot be keys"); return LiteralString(); }
        int st = p;
        while (p < s.Length && IsBare(s[p])) p++;
        if (p == st) throw Err(Eof || Cur is '\n' or '\r' ? "expected a key" : $"invalid character '{Cur}' in key");
        return s[st..p];
    }

    static bool IsBare(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';

    // ---- headers ----

    void Header()
    {
        int at = p;
        bool array = At(1) == '[';
        p += array ? 2 : 1;
        var key = Key();
        if (Cur != ']') throw Err("expected ']' to close the table header");
        p++;
        if (array) { if (Cur != ']') throw Err("expected ']]' to close the array-of-tables header"); p++; }
        TomlTable t = root;
        for (int i = 0; i < key.Count - 1; i++) t = Descend(t, key[i], at);
        string last = key[^1];
        if (array)
        {
            if (!t.TryGetValue(last, out var v))
            {
                var arr = new TomlArray { IsTableArray = true };
                t.TryAdd(last, arr);
                v = arr;
            }
            if (v.Obj is not TomlArray { IsTableArray: true } ta) throw Err($"key '{last}' is already defined and is not an array of tables", at);
            var nt = new TomlTable { Defined = true };
            ta.Add(nt);
            current = nt;
            return;
        }
        if (t.TryGetValue(last, out var ex))
        {
            if (ex.Obj is not TomlTable et || et.Inline) throw Err($"key '{last}' is already defined", at);
            if (et.Defined || et.Dotted) throw Err($"table '{string.Join('.', key)}' is defined twice", at);
            et.Defined = true;
            current = et;
            return;
        }
        var tbl = new TomlTable { Defined = true };
        t.TryAdd(last, tbl);
        current = tbl;
    }

    TomlTable Descend(TomlTable t, string k, int at)
    {
        if (!t.TryGetValue(k, out var v))
        {
            var n = new TomlTable();
            t.TryAdd(k, n);
            return n;
        }
        if (v.Obj is TomlTable nt) { if (nt.Inline) throw Err($"inline table '{k}' cannot be extended", at); return nt; }
        if (v.Obj is TomlArray { IsTableArray: true } a) return (TomlTable)a[a.Count - 1].Obj!;
        throw Err($"key '{k}' is already defined and is not a table", at);
    }

    // ---- key/value ----

    void KeyValue(TomlTable into, bool inline)
    {
        int at = p;
        var key = Key();
        if (Cur != '=') throw Err(Eof || Cur is '\n' or '\r' ? $"expected '=' after key '{key[^1]}'" : $"expected '=' after the key, found '{Cur}'");
        p++;
        SkipWs();
        var t = into;
        for (int i = 0; i < key.Count - 1; i++)
        {
            string k = key[i];
            if (!t.TryGetValue(k, out var v))
            {
                var n = new TomlTable { Dotted = true };
                t.TryAdd(k, n);
                t = n;
                continue;
            }
            if (v.Obj is TomlTable { Inline: false, Dotted: true } nt && !nt.Defined) { t = nt; continue; }
            throw Err($"key '{k}' is already defined; dotted keys cannot extend it", at);
        }
        var value = Value();
        if (!t.TryAdd(key[^1], value)) throw Err($"duplicate key '{key[^1]}'", at);
    }

    // ---- values ----

    TomlAny Value()
    {
        if (Eof) throw Err("expected a value");
        char c = Cur;
        switch (c)
        {
            case '"':
                return At(1) == '"' && At(2) == '"' ? MlBasicString() : BasicString();
            case '\'':
                return At(1) == '\'' && At(2) == '\'' ? MlLiteralString() : LiteralString();
            case '[': return Array();
            case '{': return InlineTable();
            case 't': return Word("true", true);
            case 'f': return Word("false", false);
        }
        return NumberOrDate();
    }

    TomlAny Word(string w, bool v)
    {
        if (string.CompareOrdinal(s, p, w, 0, w.Length) != 0 || (p + w.Length < s.Length && IsBare(s[p + w.Length])))
            throw Err("invalid value (strings need quotes)");
        p += w.Length;
        return v;
    }

    TomlAny Array()
    {
        p++;
        var a = new TomlArray();
        while (true)
        {
            SkipWsNl();
            if (Cur == ']') { p++; return a; }
            a.Add(Value());
            SkipWsNl();
            if (Cur == ',') { p++; continue; }
            if (Cur == ']') { p++; return a; }
            throw Err(Eof ? "unterminated array" : $"expected ',' or ']' in array, found '{Cur}'");
        }
    }

    void SkipWsNl()
    {
        while (true)
        {
            SkipWs();
            if (Cur == '#') { Comment(); continue; }
            if (Cur is '\n' or '\r') { Newline(); continue; }
            return;
        }
    }

    TomlAny InlineTable()
    {
        p++;
        var t = new TomlTable();
        SkipWs();
        if (Cur == '}') { p++; t.Inline = true; return t; }
        while (true)
        {
            SkipWs();
            if (Cur is '\n' or '\r') throw Err("newline in inline table");
            KeyValue(t, inline: true);
            SkipWs();
            if (Cur == ',')
            {
                p++;
                SkipWs();
                if (Cur == '}') throw Err("trailing comma in inline table");
                continue;
            }
            if (Cur == '}') { p++; break; }
            throw Err(Eof ? "unterminated inline table" : Cur is '\n' or '\r' ? "newline in inline table" : $"expected ',' or '}}' in inline table, found '{Cur}'");
        }
        Freeze(t);
        return t;
    }

    static void Freeze(TomlTable t)
    {
        t.Inline = true;
        foreach (var kv in t)
            if (kv.Value.Obj is TomlTable sub) Freeze(sub);
    }

    // ---- strings ----

    string BasicString()
    {
        int at = p;
        p++;
        var sb = new StringBuilder();
        while (true)
        {
            if (Eof || Cur == '\n' || (Cur == '\r')) throw Err("unterminated string", at);
            char c = s[p];
            if (c == '"') { p++; return sb.ToString(); }
            if (c == '\\') { Escape(sb); continue; }
            if (IsControl(c)) throw Err("control character in string");
            sb.Append(c);
            p++;
        }
    }

    void Escape(StringBuilder sb)
    {
        int at = p;
        p++;
        char e = Cur;
        p++;
        switch (e)
        {
            case 'b': sb.Append('\b'); return;
            case 't': sb.Append('\t'); return;
            case 'n': sb.Append('\n'); return;
            case 'f': sb.Append('\f'); return;
            case 'r': sb.Append('\r'); return;
            case '"': sb.Append('"'); return;
            case '\\': sb.Append('\\'); return;
            case 'u': Unicode(sb, 4, at); return;
            case 'U': Unicode(sb, 8, at); return;
            default: throw Err($"invalid escape '\\{e}'", at);
        }
    }

    void Unicode(StringBuilder sb, int n, int at)
    {
        if (p + n > s.Length) throw Err("short unicode escape", at);
        uint cp = 0;
        for (int i = 0; i < n; i++)
        {
            int d = HexVal(s[p + i]);
            if (d < 0) throw Err("invalid unicode escape", at);
            cp = cp * 16 + (uint)d;
        }
        p += n;
        if (!Rune.IsValid(cp)) throw Err("unicode escape is not a scalar value", at);
        sb.Append(new Rune(cp).ToString());
    }

    static int HexVal(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    string LiteralString()
    {
        int at = p;
        p++;
        int st = p;
        while (true)
        {
            if (Eof || Cur == '\n' || Cur == '\r') throw Err("unterminated literal string", at);
            char c = s[p];
            if (c == '\'') { p++; return s[st..(p - 1)]; }
            if (IsControl(c)) throw Err("control character in literal string");
            p++;
        }
    }

    string MlBasicString()
    {
        int at = p;
        p += 3;
        SkipFirstNewline();
        var sb = new StringBuilder();
        while (true)
        {
            if (Eof) throw Err("unterminated multi-line string", at);
            char c = s[p];
            if (c == '"' && At(1) == '"' && At(2) == '"')
            {
                // up to two quotes may directly precede the closing delimiter
                int q = 3;
                while (q < 5 && At(q) == '"') q++;
                sb.Append('"', q - 3);
                p += q;
                return sb.ToString();
            }
            if (c == '\\')
            {
                // line-ending backslash: trims whitespace and newlines up to the next non-blank
                int k = p + 1;
                while (k < s.Length && (s[k] == ' ' || s[k] == '\t')) k++;
                if (k < s.Length && (s[k] == '\n' || (s[k] == '\r' && k + 1 < s.Length && s[k + 1] == '\n')))
                {
                    p = k;
                    while (!Eof && (Cur is ' ' or '\t' or '\n' || (Cur == '\r' && At(1) == '\n'))) p += Cur == '\r' ? 2 : 1;
                    continue;
                }
                Escape(sb);
                continue;
            }
            if (c == '\r') { if (At(1) != '\n') throw Err("bare carriage return in string"); sb.Append("\r\n"); p += 2; continue; }
            if (c != '\n' && IsControl(c)) throw Err("control character in string");
            sb.Append(c);
            p++;
        }
    }

    string MlLiteralString()
    {
        int at = p;
        p += 3;
        SkipFirstNewline();
        var sb = new StringBuilder();
        while (true)
        {
            if (Eof) throw Err("unterminated multi-line literal string", at);
            char c = s[p];
            if (c == '\'' && At(1) == '\'' && At(2) == '\'')
            {
                int q = 3;
                while (q < 5 && At(q) == '\'') q++;
                sb.Append('\'', q - 3);
                p += q;
                return sb.ToString();
            }
            if (c == '\r') { if (At(1) != '\n') throw Err("bare carriage return in string"); sb.Append("\r\n"); p += 2; continue; }
            if (c != '\n' && IsControl(c)) throw Err("control character in string");
            sb.Append(c);
            p++;
        }
    }

    void SkipFirstNewline()
    {
        if (Cur == '\n') p++;
        else if (Cur == '\r' && At(1) == '\n') p += 2;
    }

    // ---- numbers and dates ----

    TomlAny NumberOrDate()
    {
        int st = p;
        // a token ends at whitespace, ',', ']', '}', '#' or a newline; dates may contain ':' '.' 'T' 'Z' '+'
        while (p < s.Length && s[p] is not (' ' or '\t' or ',' or ']' or '}' or '#' or '\n' or '\r')) p++;
        string tok = s[st..p];
        // date-time with a space delimiter: "1979-05-27 07:32:00"
        if (tok.Length == 10 && tok[4] == '-' && tok[7] == '-' && Cur == ' ' && At(1) is >= '0' and <= '9' && At(2) is >= '0' and <= '9' && At(3) == ':')
        {
            int k = p + 1;
            while (k < s.Length && s[k] is not (' ' or '\t' or ',' or ']' or '}' or '#' or '\n' or '\r')) k++;
            tok = tok + "T" + s[(p + 1)..k];
            p = k;
        }
        if (tok.Length == 0) throw Err(Eof ? "expected a value" : $"invalid character '{Cur}' at the start of a value", st);
        if (LooksLikeDate(tok)) return Date(tok, st);
        return Number(tok, st);
    }

    static bool LooksLikeDate(string t) => (t.Length >= 10 && t[4] == '-' && char.IsAsciiDigit(t[0])) || (t.Length >= 8 && t[2] == ':' && char.IsAsciiDigit(t[0]));

    TomlAny Number(string t, int at)
    {
        string body = t;
        switch (t)
        {
            case "inf" or "+inf": return double.PositiveInfinity;
            case "-inf": return double.NegativeInfinity;
            case "nan" or "+nan" or "-nan": return double.NaN;
        }
        if (t.Length > 2 && t[0] == '0' && t[1] is 'x' or 'o' or 'b')
        {
            int radix = t[1] == 'x' ? 16 : t[1] == 'o' ? 8 : 2;
            string digits = Underscores(t[2..], at, radix);
            ulong v = 0;
            foreach (char c in digits)
            {
                int dg = HexVal(c);
                if (dg < 0 || dg >= radix) throw Err($"invalid digit '{c}' in base-{radix} integer", at);
                if (v > (ulong.MaxValue - (ulong)dg) / (ulong)radix) throw Err("integer out of range", at);
                v = v * (ulong)radix + (ulong)dg;
            }
            if (v > long.MaxValue) throw Err("integer out of range", at);
            return (long)v;
        }
        int i = 0;
        if (body[0] is '+' or '-') i = 1;
        if (i >= body.Length || !char.IsAsciiDigit(body[i])) throw Err($"invalid value '{t}' (strings need quotes)", at);
        bool isFloat = body.AsSpan(i).IndexOfAny('.', 'e', 'E') >= 0;
        if (!isFloat)
        {
            string digits = Underscores(body[i..], at, 10);
            if (digits.Length > 1 && digits[0] == '0') throw Err("leading zeros are not allowed", at);
            if (!long.TryParse((body[0] == '-' ? "-" : "") + digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                throw Err("integer out of range", at);
            return l;
        }
        // float: int-part [. frac] [e exp]
        string rest = body[i..];
        int e = rest.IndexOfAny(['e', 'E']);
        string mant = e < 0 ? rest : rest[..e], exp = e < 0 ? "" : rest[(e + 1)..];
        int dot = mant.IndexOf('.');
        string ip = dot < 0 ? mant : mant[..dot], fp = dot < 0 ? "" : mant[(dot + 1)..];
        ip = Underscores(ip, at, 10);
        if (ip.Length > 1 && ip[0] == '0') throw Err("leading zeros are not allowed", at);
        if (dot >= 0) fp = Underscores(fp, at, 10);
        if (e >= 0)
        {
            string ex = exp;
            if (ex.Length > 0 && ex[0] is '+' or '-') ex = ex[1..];
            ex = Underscores(ex, at, 10);
            exp = (exp.Length > 0 && exp[0] == '-' ? "-" : "") + ex;
        }
        string norm = (body[0] == '-' ? "-" : "") + ip + (dot >= 0 ? "." + fp : "") + (e >= 0 ? "e" + exp : "");
        if (!double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) throw Err($"invalid float '{t}'", at);
        return d;
    }

    string Underscores(string t, int at, int radix)
    {
        if (t.Length == 0) throw Err("missing digits", at);
        if (t[0] == '_' || t[^1] == '_' || t.Contains("__")) throw Err("underscores must be between digits", at);
        foreach (char c in t)
            if (c != '_' && (radix == 10 ? !char.IsAsciiDigit(c) : HexVal(c) < 0)) throw Err($"invalid character '{c}' in number", at);
        return t.Replace("_", "");
    }

    TomlAny Date(string t, int at)
    {
        int i = 0;
        if (t.Length >= 3 && t[2] == ':') return Time(t, ref i, at) is var tm && i == t.Length ? TomlAny.From(tm) : throw Err($"invalid time '{t}'", at);
        int y = Digits(t, ref i, 4, at); Expect(t, ref i, '-', at);
        int mo = Digits(t, ref i, 2, at); Expect(t, ref i, '-', at);
        int d = Digits(t, ref i, 2, at);
        if (mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Max(1, y), mo)) throw Err($"invalid date '{t}'", at);
        var date = new DateOnly(y, mo, d);
        if (i == t.Length) return TomlAny.From(date);
        if (t[i] is not ('T' or 't')) throw Err($"invalid date-time '{t}'", at);
        i++;
        var time = Time(t, ref i, at);
        var local = date.ToDateTime(time);
        if (i == t.Length) return TomlAny.From(local);
        TimeSpan off;
        if (t[i] is 'Z' or 'z') { off = TimeSpan.Zero; i++; }
        else if (t[i] is '+' or '-')
        {
            int sign = t[i] == '-' ? -1 : 1;
            i++;
            int oh = Digits(t, ref i, 2, at); Expect(t, ref i, ':', at);
            int om = Digits(t, ref i, 2, at);
            if (oh > 23 || om > 59) throw Err($"invalid offset in '{t}'", at);
            off = TimeSpan.FromMinutes(sign * (oh * 60 + om));
        }
        else throw Err($"invalid date-time '{t}'", at);
        if (i != t.Length) throw Err($"invalid date-time '{t}'", at);
        return TomlAny.From(new DateTimeOffset(local, off));
    }

    TimeOnly Time(string t, ref int i, int at)
    {
        int h = Digits(t, ref i, 2, at); Expect(t, ref i, ':', at);
        int m = Digits(t, ref i, 2, at); Expect(t, ref i, ':', at);
        int sec = Digits(t, ref i, 2, at);
        if (h > 23 || m > 59 || sec > 59) throw Err($"invalid time in '{t}'", at);
        long ticks = 0;
        if (i < t.Length && t[i] == '.')
        {
            i++;
            int st = i;
            long scale = TimeSpan.TicksPerSecond;
            while (i < t.Length && char.IsAsciiDigit(t[i]))
            {
                scale /= 10;
                ticks += (t[i] - '0') * scale;   // digits beyond 100 ns are truncated
                i++;
            }
            if (i == st) throw Err($"missing fraction digits in '{t}'", at);
        }
        return new TimeOnly(h, m, sec).Add(TimeSpan.FromTicks(ticks));
    }

    int Digits(string t, ref int i, int n, int at)
    {
        if (i + n > t.Length) throw Err($"invalid date or time '{t}'", at);
        int v = 0;
        for (int k = 0; k < n; k++)
        {
            char c = t[i + k];
            if (!char.IsAsciiDigit(c)) throw Err($"invalid date or time '{t}'", at);
            v = v * 10 + (c - '0');
        }
        i += n;
        return v;
    }

    void Expect(string t, ref int i, char c, int at)
    {
        if (i >= t.Length || t[i] != c) throw Err($"invalid date or time '{t}'", at);
        i++;
    }
}
