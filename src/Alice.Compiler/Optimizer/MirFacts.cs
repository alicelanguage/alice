using System.Globalization;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer;

/// <summary>Primitive facts about MIR that passes share instead of re-deriving them.</summary>
public static class MirFacts
{
    /// <summary>An integer literal as the lowerer prints it: `42`, `-7`, `0x1F`, `5L`.</summary>
    public static bool IntLiteral(MExpr e, out long value)
    {
        value = 0;
        if (e is not MCode { Text: var t } || t.Length == 0) return false;
        t = t.TrimEnd('L', 'l');
        bool neg = t.StartsWith('-');
        if (neg) t = t[1..];
        if (t.StartsWith('(') || t.Length == 0) return false;
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : t.All(char.IsAsciiDigit) && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (ok && neg) value = -value;
        return ok;
    }

    /// <summary>Positive power of two: returns log2.</summary>
    public static bool PowerOfTwo(long v, out int log2)
    {
        log2 = v > 0 && (v & (v - 1)) == 0 ? System.Numerics.BitOperations.Log2((ulong)v) : -1;
        return log2 >= 0;
    }

    public static MCode Int(long v, bool isLong) => new(v.ToString(CultureInfo.InvariantCulture) + (isLong ? "L" : ""));

    /// <summary>Whether a statement list can write the local `name` (assignment, ++/--, ref/out argument, or opaque code).</summary>
    public static bool Writes(List<MStmt>? body, string name) => Writes(body, name, false);

    /// <summary>
    /// <see cref="Writes(List{MStmt}?, string)"/> for a local of a primitive number type: opaque code writes it only
    /// through a write form (`name =`, `name +=`, `++name`, `ref name`), since a number has no mutating members. The
    /// parallel-loop probe mentions its counter in opaque conditions and dispatch calls, by value.
    /// </summary>
    public static bool WritesNumber(List<MStmt>? body, string name) => Writes(body, name, true);

    static bool Writes(List<MStmt>? body, string name, bool num) => body != null && body.Any(s => Writes(s, name, num));

    static bool Writes(MStmt s, string name, bool num) => s switch
    {
        MAssign a => Names(a.Target, name) || Mentions(a.Value, name, true, num),
        MDecl d => d.Name == name || (d.Init != null && Mentions(d.Init, name, true, num)),
        MDeconstruct dc => dc.Names.Contains(name) || Mentions(dc.Value, name, true, num),
        MExprStmt es => Mentions(es.E, name, true, num),
        MIf i => Mentions(i.Cond, name, true, num) || Writes(i.Then, name, num) || Writes(i.Else, name, num),
        MWhile w => Mentions(w.Cond, name, true, num) || Writes(w.Body, name, num),
        MFor f => f.Var == name || Writes(f.Body, name, num),
        MForeach fe => fe.Vars.Contains(name) || Writes(fe.Body, name, num),
        MReturn r => r.Value != null && Mentions(r.Value, name, true, num),
        MThrow t => Mentions(t.Value, name, true, num),
        MTry t => Writes(t.Body, name, num) || t.Catches.Any(c => Writes(c.Body, name, num)) || Writes(t.Finally, name, num),
        MBlock b => Writes(b.Body, name, num),
        MUsing u => u.Name == name || Writes(u.Body, name, num),
        _ => false,
    };

    static bool Names(MExpr e, string name) => e is MCode c && c.Text == name;

    /// <summary>
    /// With writesOnly: true when the expression may write `name` (++, --, ref/out, nested assignment in a lambda,
    /// or opaque code mentioning it). Otherwise any mention.
    /// </summary>
    static bool Mentions(MExpr e, string name, bool writesOnly, bool num = false)
    {
        switch (e)
        {
            case MCode c:
                return !writesOnly ? c.Text == name : c.Text != name && (num ? WriteForm(c.Text, name) : ContainsWord(c.Text, name));   // opaque text using it: assume a write
            case MUnary u when u.Op is AirUnOp.Inc or AirUnOp.Dec:
                return Names(u.E, name) || Mentions(u.E, name, writesOnly, num);
            case MArg a when a.Prefix.StartsWith("ref") || a.Prefix.StartsWith("out"):
                return Names(a.E, name) || Mentions(a.E, name, writesOnly, num);
            case MLambda l:
                return Writes(l.Body, name, num) || (l.ExprBody != null && Mentions(l.ExprBody, name, writesOnly, num));
        }
        return Children(e).Any(x => Mentions(x, name, writesOnly, num));
    }

    /// <summary>Opaque text with a write form for `name`: an assignment, ++/--, or a ref/out/in argument.</summary>
    public static bool WriteForm(string text, string name)
    {
        var n = Alice.Core.Text.Regex.Escape(name);
        return Alice.Core.Text.Regex.Cached(
            $@"(?<![\w.])({n}\s*([-+*/%&|^]|<<|>>>?|\?\?)?=(?!=)|{n}\s*(\+\+|--)|(\+\+|--)\s*{n}(?![\w])|\b(ref|out|in)\s+{n}(?![\w]))").IsMatch(text);
    }

    static bool ContainsWord(string text, string name)
    {
        for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            bool before = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
            int j = i + name.Length;
            bool after = j >= text.Length || !(char.IsLetterOrDigit(text[j]) || text[j] == '_');
            if (before && after) return true;
        }
        return false;
    }

    public static IEnumerable<MExpr> Children(MExpr e) => e switch
    {
        MCall c => (c.Target != null ? new[] { c.Target } : []).Concat(c.Args),
        MMember m => [m.Target],
        MInvoke i => i.Args.Prepend(i.Target),
        MNew n => n.Args.Concat(n.Init ?? []),
        MNewArray a => (a.Size != null ? new[] { a.Size } : []).Concat(a.Items ?? []),
        MIndex ix => ix.Args.Prepend(ix.Target),
        MBinary b => [b.L, b.R],
        MUnary u => [u.E],
        MCond c => [c.C, c.A, c.B],
        MCast c => [c.E],
        MIs i => [i.E],
        MInterp ip => ip.Holes,
        MTuple t => t.Items,
        MPair p => [p.K, p.V],
        MArg a => [a.E],
        AirParExpr p => p.Children(),
        _ => [],
    };

    /// <summary>
    /// Clears <see cref="MBinary.Checked"/> / <see cref="MUnary.Checked"/> on every operator textually inside
    /// <paramref name="e"/>, as C# `unchecked(e)` does; returns <paramref name="e"/> for wrapping. A `checked(...)`
    /// inside (<see cref="KeepChecked"/>) is a boundary, as in C#: the operators under it keep their flags.
    /// </summary>
    public static MExpr Unchecked(MExpr e)
    {
        var keep = new HashSet<MExpr>(ReferenceEqualityComparer.Instance);
        void Collect(MExpr x, bool inside)
        {
            if (inside) keep.Add(x);
            bool boundary = x is MCall { Target: null, Name: "checked", Args.Count: 1 };
            foreach (var c in Children(x)) Collect(c, inside || boundary);
        }
        Collect(e, false);
        new UncheckedMarker(keep).Mark(e);
        return e;
    }

    /// <summary>
    /// An operand of a wrapping operator (`a &amp;+ b` prints as `unchecked(a + b)`): wrapped in `checked(...)` when
    /// it holds arithmetic that traps in a checked context, so only the wrapping operator itself wraps. Without it
    /// `1 &amp;* (b * c)` wrapped the inner `*` too, against ADR 0013 (found by `alice prove`'s differential fuzz).
    /// </summary>
    public static MExpr KeepChecked(MExpr e) => Traps(e) ? new MCall { Name = "checked", Args = { e } } : e;

    /// <summary>Whether <paramref name="e"/> has an operator a checked context makes trap (`+ - *`, negation,
    /// `++`/`--`), outside a nested `unchecked(...)`/`checked(...)`.</summary>
    static bool Traps(MExpr e) => e switch
    {
        MCall { Target: null, Name: "unchecked" or "checked", Args.Count: 1 } => false,
        // floating-point and decimal arithmetic never depends on the context
        MBinary { Checked: true, Op: AirBinOp.Add or AirBinOp.Sub or AirBinOp.Mul } b when b.Type?.ToCs() is not ("float" or "double" or "decimal") => true,
        MUnary { Checked: true, Op: AirUnOp.Neg or AirUnOp.Inc or AirUnOp.Dec } => true,
        // opaque text: anything with an arithmetic operator but a plain (negative) literal
        _ when e.GetType() == typeof(MCode) => WithoutUnchecked(((MCode)e).Text).IndexOfAny(['+', '-', '*']) >= 0 && !IntLiteral(e, out _),
        _ => Children(e).Any(Traps),
    };

    /// <summary>The text with every balanced `unchecked(...)` removed: arithmetic in there wraps already.</summary>
    static string WithoutUnchecked(string t)
    {
        for (int at = t.IndexOf("unchecked(", StringComparison.Ordinal); at >= 0; at = t.IndexOf("unchecked(", StringComparison.Ordinal))
        {
            int depth = 0, end = at + "unchecked".Length;
            for (; end < t.Length; end++)
                if (t[end] == '(') depth++;
                else if (t[end] == ')' && --depth == 0) break;
            if (end >= t.Length) return t;   // unbalanced: keep it, conservatively
            t = t[..at] + t[(end + 1)..];
        }
        return t;
    }

    sealed class UncheckedMarker(HashSet<MExpr> keep) : MirRewriter
    {
        public void Mark(MExpr e) => Expr(e);

        protected override MExpr Rewrite(MExpr e)
        {
            if (keep.Contains(e)) return e;
            if (e is MBinary b) b.Checked = false;
            else if (e is MUnary u) u.Checked = false;
            return e;
        }
    }

    /// <summary>Whether a body calls the unqualified method `name` (a self call when `name` is the enclosing method).</summary>
    public static bool Calls(List<MStmt> body, string name)
    {
        var finder = new CallFinder(name);
        finder.Run(body);
        return finder.Found;
    }

    sealed class CallFinder(string name) : MirRewriter
    {
        public bool Found;
        protected override MExpr Rewrite(MExpr e)
        {
            if (e is MCall { Target: null } c && c.Name == name) Found = true;
            return e;
        }
    }
}
