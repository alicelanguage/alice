using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Analysis;

/// <summary>
/// An integer expression affine in the loop counters of a nest, whose coefficients are themselves affine in at most
/// one loop-invariant symbol: `i * n + j` is {(i, n): 1, (j, 1): 1}, `(i + 1) * n` is {(i, n): 1, (1, n): 1}.
/// Keys are (Var, Sym) with "" for "no counter" / "the constant 1". Products of two counters or two symbols are
/// not affine and fail to parse.
/// </summary>
public sealed class Affine
{
    public readonly Dictionary<(string Var, string Sym), long> Terms = [];

    public static Affine Const(long v) { var a = new Affine(); a.Add(("", ""), v); return a; }

    void Add((string, string) key, long v)
    {
        long r = Terms.GetValueOrDefault(key) + v;
        if (r == 0) Terms.Remove(key); else Terms[key] = r;
    }

    public IEnumerable<string> Vars => Terms.Keys.Select(k => k.Var).Where(v => v != "").Distinct();
    public IEnumerable<string> Syms => Terms.Keys.Select(k => k.Sym).Where(s => s != "").Distinct();

    /// <summary>Coefficient of a counter with no symbol (its stride when the symbol part is empty).</summary>
    public long Coef(string var, string sym = "") => Terms.GetValueOrDefault((var, sym));

    public bool HasSymCoef(string var) => Terms.Keys.Any(k => k.Var == var && k.Sym != "");

    static Affine Sum(Affine x, Affine y, long sign)
    {
        var r = new Affine();
        foreach (var (k, v) in x.Terms) r.Add(k, v);
        foreach (var (k, v) in y.Terms) r.Add(k, sign * v);
        return r;
    }

    static Affine? Product(Affine x, Affine y)
    {
        var r = new Affine();
        foreach (var ((xv, xs), a) in x.Terms)
            foreach (var ((yv, ys), b) in y.Terms)
            {
                if (xv != "" && yv != "" || xs != "" && ys != "") return null;
                r.Add((xv != "" ? xv : yv, xs != "" ? xs : ys), checked(a * b));
            }
        return r;
    }

    /// <summary>Parses `e` over the counters `vars`; any other plain name or `x.Length` is a symbol.</summary>
    public static Affine? Parse(MExpr e, IReadOnlySet<string> vars)
    {
        try { return P(e, vars); } catch (OverflowException) { return null; }
    }

    static Affine? P(MExpr e, IReadOnlySet<string> vars)
    {
        switch (e)
        {
            case MCode c when MirFacts.IntLiteral(c, out long v):
                return Const(v);
            case MCode { Text: var t } when Identifier(t):
                var a = new Affine();
                a.Add(vars.Contains(t) ? (t, "") : ("", t), 1);
                return a;
            case MMember { Name: "Length", Target: MCode { Text: var arr } } when Identifier(arr) && !vars.Contains(arr):
                var l = new Affine();
                l.Add(("", arr + ".Length"), 1);
                return l;
            case MCall { Target: null, Name: "unchecked", Args: [var inner] }:
                return P(inner, vars);   // the runtime guard proves the exact value fits, so wrapping equals exact
            case MUnary { Op: "-", Postfix: false, E: var x }:
                return P(x, vars) is { } n ? Sum(Const(0), n, -1) : null;
            case MBinary { Op: "+" or "-" or "*" } b:
                if (P(b.L, vars) is not { } L || P(b.R, vars) is not { } R) return null;
                return b.Op == "*" ? Product(L, R) : Sum(L, R, b.Op == "+" ? 1 : -1);
        }
        return null;
    }

    public static bool Identifier(string t) =>
        t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    /// <summary>The part of this form whose coefficients carry `sym` (divided by it), and the part with none.</summary>
    public (Affine High, Affine Low)? Split(string sym)
    {
        Affine hi = new(), lo = new();
        foreach (var ((v, s), c) in Terms)
        {
            if (s == sym) hi.Add((v, ""), c);
            else if (s == "") lo.Add((v, ""), c);
            else return null;
        }
        return (hi, lo);
    }

    /// <summary>`var + offset` (Var null for a constant), when the form has that shape.</summary>
    public (string? Var, long Offset)? Uniform()
    {
        if (Syms.Any()) return null;
        var vs = Vars.ToList();
        long off = Coef("");
        if (vs.Count == 0) return (null, off);
        if (vs.Count == 1 && Coef(vs[0]) == 1) return (vs[0], off);
        return null;
    }

    /// <summary>
    /// The form as a C# Int64 expression with each counter replaced by `value(var)`:
    /// `3L * (long)(n) * (long)(i)`. Used by the runtime guard.
    /// </summary>
    public MExpr ToLong(Func<string, MExpr> value)
    {
        MExpr sum = new MCode("0L");
        foreach (var ((v, s), c) in Terms)
        {
            MExpr term = new MCode(c.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L");
            if (s != "") term = Mul(term, Long(new MCode(s)));
            if (v != "") term = Mul(term, Long(value(v)));
            sum = Add(sum, term);
        }
        return sum;
    }

    /// <summary>`(long)e`, or the Int64 literal when e is an integer literal.</summary>
    public static MExpr Long(MExpr e) => MirFacts.IntLiteral(e, out long v) ? LongLit(v) : new MCast { TypeCs = "long", E = e };

    static MCode LongLit(long v) => new(v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L");

    public static bool LongLiteral(MExpr e, out long v) => MirFacts.IntLiteral(e, out v) && e is MCode { Text: [.., 'L'] };

    static MExpr Mul(MExpr a, MExpr b)
    {
        bool la = LongLiteral(a, out long x), lb = LongLiteral(b, out long y);
        if (la && lb) return LongLit(checked(x * y));
        if (la && x == 1 || lb && y == 0) return b;
        if (lb && y == 1 || la && x == 0) return a;
        return new MBinary { Op = "*", L = a, R = b };
    }

    static MExpr Add(MExpr a, MExpr b)
    {
        bool la = LongLiteral(a, out long x), lb = LongLiteral(b, out long y);
        if (la && lb) return LongLit(checked(x + y));
        if (la && x == 0) return b;
        if (lb && y == 0) return a;
        return new MBinary { Op = "+", L = a, R = b };
    }
}
