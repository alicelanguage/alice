using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// Parses the C# expression text of an <see cref="MCode"/> leaf (what lowerings not yet ported to typed AIR still
/// produce: `c.Length`, `((byte)105)`, `unchecked(j + 1)`, `Vector512.IsHardwareAccelerated &amp;&amp; n >= 8`) into MIR
/// nodes the emitter types like any other. Unknown syntax returns null (the leaf stays unsupported).
/// </summary>
internal sealed class CsText
{
    readonly string s;
    readonly Func<string, bool> isType;
    int p;

    CsText(string s, Func<string, bool> isType) { this.s = s; this.isType = isType; }

    /// <summary>The MIR of <paramref name="text"/>, or null when it is not a supported C# expression.</summary>
    public static MExpr? Parse(string text, Func<string, bool> isType)
    {
        var c = new CsText(text, isType);
        try
        {
            var e = c.Conditional();
            c.Ws();
            return c.p == c.s.Length ? e : null;
        }
        catch (FormatException) { return null; }
    }

    static FormatException Bad() => new();

    void Ws() { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

    bool At(string t)
    {
        Ws();
        return string.CompareOrdinal(s, p, t, 0, t.Length) == 0;
    }

    bool Eat(string t)
    {
        if (!At(t)) return false;
        p += t.Length;
        return true;
    }

    void Expect(string t) { if (!Eat(t)) throw Bad(); }

    MExpr Conditional()
    {
        var c = Binary(0);
        if (At("??"))
        {
            p += 2;
            return new MBinary { Op = AirBinOp.Coalesce, L = c, R = Conditional() };
        }
        if (At("?") && !At("?.") && !At("?["))
        {
            p++;
            var a = Conditional();
            Expect(":");
            var b = Conditional();
            return new MCond { C = c, A = a, B = b };
        }
        return c;
    }

    static readonly (string Op, AirBinOp Bin, int Prec)[] Ops =
    [
        ("||", AirBinOp.Or, 1), ("&&", AirBinOp.And, 2), ("|", AirBinOp.BitOr, 3), ("^", AirBinOp.BitXor, 4), ("&", AirBinOp.BitAnd, 5),
        ("==", AirBinOp.Eq, 6), ("!=", AirBinOp.Ne, 6),
        ("<=", AirBinOp.Le, 7), (">=", AirBinOp.Ge, 7), ("<", AirBinOp.Lt, 7), (">", AirBinOp.Gt, 7),
        (">>>", AirBinOp.UShr, 8), ("<<", AirBinOp.Shl, 8), (">>", AirBinOp.Shr, 8),
        ("+", AirBinOp.Add, 9), ("-", AirBinOp.Sub, 9),
        ("*", AirBinOp.Mul, 10), ("/", AirBinOp.Div, 10), ("%", AirBinOp.Mod, 10),
    ];

    (string Op, AirBinOp Bin, int Prec)? PeekOp()
    {
        Ws();
        foreach (var o in Ops)
        {
            if (!At(o.Op)) continue;
            // `|` vs `||`, `&` vs `&&`, `<` vs `<<`, `=` compound assignments are not expressions here
            string rest = s[(p + o.Op.Length)..];
            if (o.Op is "|" && rest.StartsWith('|') || o.Op is "&" && rest.StartsWith('&')) continue;
            if (rest.StartsWith('=') && o.Op is not ("==" or "!=" or "<=" or ">=")) return null;
            return o;
        }
        return null;
    }

    MExpr Binary(int min)
    {
        var l = Unary();
        while (true)
        {
            Ws();
            if (At("is ") && min <= 7)
            {
                p += 3;
                Ws();
                int start = p;
                while (p < s.Length && s[p] is not (')' or ',' or '&' or '|' or '?' or ':')) p++;
                l = new MIs { E = l, TypeCs = s[start..p].Trim() };
                continue;
            }
            var o = PeekOp();
            if (o == null || o.Value.Prec < min) return l;
            p += o.Value.Op.Length;
            var r = Binary(o.Value.Prec + 1);
            l = new MBinary { Op = o.Value.Bin, L = l, R = r };
        }
    }

    MExpr Unary()
    {
        Ws();
        if (Eat("-")) { var e = Unary(); return e is MCode lit && lit.Text.Length > 0 && char.IsDigit(lit.Text[0]) ? new MCode("-" + lit.Text) : new MUnary { Op = AirUnOp.Neg, E = e }; }
        if (Eat("!")) return new MUnary { Op = AirUnOp.Not, E = Unary() };
        if (Eat("~")) return new MUnary { Op = AirUnOp.BitNot, E = Unary() };
        if (Eat("+")) return Unary();
        if (At("(") && TryCast() is { } cast) return cast;
        return Postfix(Primary());
    }

    /// <summary>`(T)x`: a parenthesized type followed by an operand.</summary>
    MExpr? TryCast()
    {
        int save = p;
        p++;
        int depth = 1, start = p;
        while (p < s.Length && depth > 0)
        {
            if (s[p] is '(' or '<' or '[') depth += s[p] == '(' ? 1 : 0;
            if (s[p] == ')') depth--;
            if (depth > 0) p++;
        }
        if (p >= s.Length) { p = save; return null; }
        string inner = s[start..p].Trim();
        p++;
        Ws();
        bool operand = p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] is '(' or '_' or '@' or '"' or '\'' or '!' or '~');
        if (inner.Length == 0 || !operand || !isType(inner)) { p = save; return null; }
        return new MCast { TypeCs = inner, E = Unary() };
    }

    MExpr Postfix(MExpr e)
    {
        while (true)
        {
            Ws();
            if (At("?.")) { p += 2; var n = Ident(); e = Member(e, n, safe: true); continue; }
            if (At(".") && !(p + 1 < s.Length && char.IsDigit(s[p + 1]))) { p++; var n = Ident(); e = Member(e, n, safe: false); continue; }
            if (At("["))
            {
                p++;
                var args = Args("]");
                e = new MIndex { Target = e, Args = args };
                continue;
            }
            if (At("!") && !At("!=")) { p++; continue; }   // null-forgiving
            if (At("++")) { p += 2; e = new MUnary { Op = AirUnOp.Inc, E = e, Postfix = true }; continue; }
            if (At("--")) { p += 2; e = new MUnary { Op = AirUnOp.Dec, E = e, Postfix = true }; continue; }
            return e;
        }
    }

    MExpr Member(MExpr target, string name, bool safe)
    {
        string gen = GenericArgs();
        Ws();
        if (At("("))
        {
            p++;
            return new MCall { Target = target, Name = name + gen, Args = Args(")"), Safe = safe };
        }
        if (gen.Length > 0) return new MCode(Text(target) + "." + name + gen);
        // a dotted name stays one leaf (`global::System.Math.PI`, `c.Length`): the emitter splits types from variables
        if (!safe && target is MCode { Text: var t } && IsPath(t)) return new MCode(t + "." + name);
        return new MMember { Target = target, Name = name, Safe = safe };
    }

    static bool IsPath(string t) => t.Length > 0 && !char.IsDigit(t[0]) && t[0] is not ('"' or '\'' or '-') && t.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or ':' or '@' or '<' or '>' or ',' or ' ' or '[' or ']');

    static string Text(MExpr e) => e is MCode c ? c.Text : throw Bad();

    /// <summary>`&lt;double&gt;` right after a name when it is a type-argument list (followed by `.` `(` or the end).</summary>
    string GenericArgs()
    {
        if (p >= s.Length || s[p] != '<') return "";
        int save = p, depth = 0;
        for (int i = p; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '<') depth++;
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    int j = i + 1;
                    if (j >= s.Length || s[j] is '.' or '(' or ')' or ',' or ']')
                    {
                        p = j;
                        return s[save..j];
                    }
                    return "";
                }
            }
            else if (!(char.IsLetterOrDigit(ch) || ch is '_' or '.' or ':' or ',' or ' ' or '[' or ']' or '?' or '(' or ')')) return "";
        }
        return "";
    }

    List<MExpr> Args(string close)
    {
        var r = new List<MExpr>();
        Ws();
        if (Eat(close)) return r;
        while (true)
        {
            Ws();
            string prefix = "";
            foreach (var k in new[] { "out var ", "out ", "ref ", "in " })
                if (At(k)) { prefix = k; p += k.Length; break; }
            var e = Conditional();
            r.Add(prefix.Length > 0 ? new MArg { Prefix = prefix, E = e } : e);
            if (Eat(",")) continue;
            Expect(close);
            return r;
        }
    }

    string Ident()
    {
        Ws();
        int start = p;
        if (p < s.Length && s[p] == '@') p++;
        while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) p++;
        if (p == start) throw Bad();
        return s[start..p];
    }

    MExpr Primary()
    {
        Ws();
        if (p >= s.Length) throw Bad();
        char ch = s[p];
        if (Eat("("))
        {
            var e = Conditional();
            Expect(")");
            return e;
        }
        if (ch == '"' || ch == '@' && p + 1 < s.Length && s[p + 1] == '"')
        {
            int start = p;
            if (ch == '@') p++;
            p++;
            while (p < s.Length && s[p] != '"') { if (s[p] == '\\' && ch != '@') p++; p++; }
            p++;
            return new MCode(s[start..p]);
        }
        if (ch == '\'')
        {
            int start = p++;
            while (p < s.Length && s[p] != '\'') { if (s[p] == '\\') p++; p++; }
            p++;
            return new MCode(s[start..p]);
        }
        if (char.IsDigit(ch) || ch == '.' && p + 1 < s.Length && char.IsDigit(s[p + 1]))
        {
            int start = p;
            while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] is '.' or '_' || (s[p] is '+' or '-') && p > start && s[p - 1] is 'e' or 'E' && !s[start..p].StartsWith("0x"))) p++;
            return new MCode(s[start..p]);
        }
        if (At("new "))
        {
            p += 4;
            Ws();
            int start = p;
            if (At("[]")) throw Bad();
            while (p < s.Length && s[p] is not ('(' or '[' or '{')) p++;
            string type = s[start..p].Trim();
            if (Eat("("))
            {
                var args = Args(")");
                if (At("{")) throw Bad();
                return new MNew { TypeCs = type, Args = args };
            }
            if (Eat("["))
            {
                Ws();
                if (Eat("]"))
                {
                    Expect("{");
                    return new MNewArray { ElemCs = type, Items = Args("}") };
                }
                var size = Conditional();
                Expect("]");
                if (p < s.Length && s[p] == '[') throw Bad();
                return new MNewArray { ElemCs = type, Size = size };
            }
            throw Bad();
        }
        foreach (var kw in new[] { "unchecked", "checked" })
            if (At(kw + "(") || At(kw + " ("))
            {
                p += kw.Length;
                Expect("(");
                var e = Conditional();
                Expect(")");
                return kw == "unchecked" ? new MCall { Name = "unchecked", Args = [e] } : e;
            }
        if (At("default("))
        {
            p += 8;
            int start = p, depth = 1;
            while (p < s.Length && depth > 0) { if (s[p] == '(') depth++; if (s[p] == ')') depth--; p++; }
            return new AirDefault(new AirType.ClrRef(s[start..(p - 1)], null, []));
        }
        if (char.IsLetter(ch) || ch is '_' or '@')
        {
            string id = Ident();
            if (id == "global" && Eat("::")) id = "global::" + Ident();
            string gen = GenericArgs();
            Ws();
            if (At("(") && gen.Length >= 0 && id is not ("typeof" or "sizeof" or "nameof" or "stackalloc"))
            {
                p++;
                return new MCall { Name = id + gen, Args = Args(")") };
            }
            if (id is "typeof" or "sizeof" or "nameof" or "stackalloc" or "await" or "throw") throw Bad();
            return new MCode(id + gen);
        }
        throw Bad();
    }
}
