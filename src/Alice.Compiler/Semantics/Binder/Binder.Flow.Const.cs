using System.Globalization;
using System.Numerics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Compile-time constant overflow (AL0300), what C#'s constant folding reported on the generated code (CS0220,
/// CS0221): integer literal arithmetic (`+ - *`, unary `-`) folded with C#'s literal types and promotions, checked
/// unless inside `unchecked ... end` or a wrapping operator (`&+ &- &*`, whose operands stay checked); a typed literal
/// out of its type's range (`200_i8`). Messages are the C# path's (Diagnostics/CSharpErrors.cs catch-all).
/// </summary>
public sealed partial class Binder
{
    public const string ConstOverflowCode = "AL0300";

    void CheckConstants(FileNode f)
    {
        var unchecked_ = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var done = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var roots = new List<Node>();
        void Items(IEnumerable<Item> items)
        {
            foreach (var it in items)
                switch (it)
                {
                    case StmtItem si: roots.Add(si.Stmt); break;
                    case NamespaceItem n: Items(n.Items); break;
                    case TypeDecl td: Items(td.Members); break;
                    case MethodDecl { IsTemplate: false } md:
                        roots.AddRange(md.Body);
                        roots.AddRange(md.Params.Where(p => p.Default != null).Select(p => (Node)p.Default!));
                        break;
                    case FieldDecl { Init: { } i }: roots.Add(i); break;
                    case AccessorDecl { Init: { } i }: roots.Add(i); break;
                    case ConstDecl c: roots.Add(c.Value); break;
                }
        }
        Items(f.Items);
        AstWalk.Each(roots, n =>
        {
            if (n is BeginStmt { Unchecked: true } b) AstWalk.Each(b.Body, x => unchecked_.Add(x));
        });
        AstWalk.Each(roots, n =>
        {
            if (n is BinaryExpr or UnaryExpr or LitExpr && !done.Contains(n))
                Fold((Expr)n, !unchecked_.Contains(n), f.Path, done);
        });
    }

    static readonly Dictionary<string, (BigInteger Min, BigInteger Max)> IntRange = new()
    {
        ["sbyte"] = (sbyte.MinValue, sbyte.MaxValue), ["byte"] = (byte.MinValue, byte.MaxValue),
        ["short"] = (short.MinValue, short.MaxValue), ["ushort"] = (ushort.MinValue, ushort.MaxValue),
        ["int"] = (int.MinValue, int.MaxValue), ["uint"] = (uint.MinValue, uint.MaxValue),
        ["long"] = (long.MinValue, long.MaxValue), ["ulong"] = (ulong.MinValue, ulong.MaxValue),
    };

    static bool Fits(BigInteger v, string t) => IntRange[t].Min <= v && v <= IntRange[t].Max;

    static BigInteger Wrap(BigInteger v, string t)
    {
        var (min, max) = IntRange[t];
        var m = max - min + 1;
        var r = ((v - min) % m + m) % m + min;
        return r;
    }

    /// <summary>A C# integer literal's value and type: `12`, `0xFF`, `3U`, `5L`, `7UL`, `((sbyte)200)`.</summary>
    (BigInteger V, string T)? Literal(LitExpr l, string path, bool chk)
    {
        if (l.Kind != "int") return null;
        string s = l.Text.Replace("_", "");
        var cast = System.Text.RegularExpressions.Regex.Match(s, @"^\(\((?<t>sbyte|byte|short|ushort|int|uint|long|ulong)\)(?<v>-?[0-9A-Fa-fxXuUlL]+)\)$");
        if (cast.Success)
        {
            var inner = Literal(new LitExpr { Kind = "int", Text = cast.Groups["v"].Value.TrimStart('-'), Line = l.Line, Col = l.Col }, path, chk);
            if (inner == null) return null;
            var v = cast.Groups["v"].Value.StartsWith('-') ? -inner.Value.V : inner.Value.V;
            string t = cast.Groups["t"].Value;
            if (!Fits(v, t))
            {
                if (!chk) return (Wrap(v, t), Promote(t));
                FlowError(path, l, ConstOverflowCode, Diagnostics.AliceNames.Quoted($"Constant value '{v}' cannot be converted to a '{t}' (use 'unchecked' syntax to override)"));
                return null;
            }
            return (v, Promote(t));
        }
        if (s.StartsWith('-') && Literal(new LitExpr { Kind = "int", Text = s[1..], Line = l.Line, Col = l.Col }, path, chk) is { } pos)
        {
            // `-2147483648` written as one literal: C# negates it, the minimum value keeps the narrow type
            var nv = -pos.V;
            if (nv == int.MinValue && pos.T == "uint") return (nv, "int");
            if (nv == long.MinValue && pos.T == "ulong") return (nv, "long");
            return pos.T switch { "int" => (nv, "int"), "uint" or "long" => (nv, "long"), _ => null };
        }
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(?<n>0[xX][0-9A-Fa-f]+|0[bB][01]+|[0-9]+)(?<s>[uU][lL]?|[lL][uU]?)?$");
        if (!m.Success) return null;
        string n = m.Groups["n"].Value;
        BigInteger val = n.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? BigInteger.Parse("0" + n[2..], NumberStyles.HexNumber)
            : n.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? n[2..].Aggregate(BigInteger.Zero, (a, c) => a * 2 + (c - '0'))
            : BigInteger.Parse(n, CultureInfo.InvariantCulture);
        string suf = m.Groups["s"].Value.ToLowerInvariant();
        string[] order = suf switch
        {
            "" => ["int", "uint", "long", "ulong"],
            "u" => ["uint", "ulong"],
            "l" => ["long", "ulong"],
            _ => ["ulong"],
        };
        foreach (var t in order) if (Fits(val, t)) return (val, t);
        return null;   // CS1021 (integral constant too large): not a flow diagnostic
    }

    /// <summary>The type C# arithmetic gives an operand (byte, short... promote to int).</summary>
    static string Promote(string t) => t is "sbyte" or "byte" or "short" or "ushort" ? "int" : t;

    /// <summary>The C# binary-operator type of two integer constants, or null when no built-in operator applies.</summary>
    static string? Common((BigInteger V, string T) a, (BigInteger V, string T) b)
    {
        string x = a.T, y = b.T;
        if (x == y) return x;
        bool Conv((BigInteger V, string T) c, string to) => c.V >= 0 && Fits(c.V, to);   // implicit constant conversion
        if (x == "ulong" || y == "ulong")
        {
            var o = x == "ulong" ? b : a;
            return o.T == "uint" || Conv(o, "ulong") ? "ulong" : null;
        }
        if (x == "long" || y == "long") return "long";
        if (x == "uint" || y == "uint")
        {
            var o = x == "uint" ? b : a;
            return Conv(o, "uint") ? "uint" : "long";
        }
        return "int";
    }

    (BigInteger V, string T)? Fold(Expr e, bool chk, string path, HashSet<Node> done)
    {
        done.Add(e);
        switch (e)
        {
            case LitExpr l:
                return Literal(l, path, chk);
            case UnaryExpr { Op: "-" } u:
                {
                    done.Add(u.E);
                    // `-2147483648`, `-9223372036854775808`: C# reads the literal as the minimum value itself
                    if (u.E is LitExpr { Kind: "int" } ml && Literal(ml, path, chk) is { } mv && (mv is { T: "uint", V: var a } && a == -(BigInteger)int.MinValue || mv is { T: "ulong", V: var b2 } && b2 == -(BigInteger)long.MinValue))
                        return (-mv.V, mv.T == "uint" ? "int" : "long");
                    if (Fold(u.E, chk, path, done) is not { } x) return null;
                    string t = x.T switch { "uint" => "long", "ulong" => "", _ => x.T };
                    if (t == "") return null;
                    var v = -x.V;
                    if (Fits(v, t)) return (v, t);
                    if (!chk) return (Wrap(v, t), t);
                    FlowError(path, u, ConstOverflowCode, Diagnostics.AliceNames.Quoted("The operation overflows at compile time in checked mode"));
                    return null;
                }
            case UnaryExpr { Op: "+" } up:
                return Fold(up.E, chk, path, done);
            case BinaryExpr { Op: "+" or "-" or "*" or "&+" or "&-" or "&*" } be:
                {
                    bool wrap = be.Op.StartsWith('&');
                    var l = Fold(be.L, chk || wrap && chk, path, done);
                    var r = Fold(be.R, chk || wrap && chk, path, done);
                    if (l is not { } a || r is not { } c) return null;
                    if (Common(a, c) is not { } t) return null;
                    char op = be.Op[^1];
                    var v = op == '+' ? a.V + c.V : op == '-' ? a.V - c.V : a.V * c.V;
                    if (Fits(v, t)) return (v, t);
                    if (!chk || wrap) return (Wrap(v, t), t);
                    FlowError(path, be, ConstOverflowCode, Diagnostics.AliceNames.Quoted("The operation overflows at compile time in checked mode"));
                    return null;
                }
            case BinaryExpr bo:
                Fold(bo.L, chk, path, done);
                Fold(bo.R, chk, path, done);
                return null;
        }
        return null;
    }
}
