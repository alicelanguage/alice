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
    public static bool Writes(List<MStmt>? body, string name) => body != null && body.Any(s => Writes(s, name));

    static bool Writes(MStmt s, string name) => s switch
    {
        MAssign a => Names(a.Target, name) || Mentions(a.Value, name, writesOnly: true),
        MDecl d => d.Name == name || (d.Init != null && Mentions(d.Init, name, writesOnly: true)),
        MDeconstruct dc => dc.Names.Contains(name) || Mentions(dc.Value, name, writesOnly: true),
        MExprStmt es => Mentions(es.E, name, writesOnly: true),
        MIf i => Mentions(i.Cond, name, true) || Writes(i.Then, name) || Writes(i.Else, name),
        MWhile w => Mentions(w.Cond, name, true) || Writes(w.Body, name),
        MFor f => f.Var == name || Writes(f.Body, name),
        MForeach fe => fe.Vars.Contains(name) || Writes(fe.Body, name),
        MReturn r => r.Value != null && Mentions(r.Value, name, true),
        MThrow t => Mentions(t.Value, name, true),
        MTry t => Writes(t.Body, name) || t.Catches.Any(c => Writes(c.Body, name)) || Writes(t.Finally, name),
        MBlock b => Writes(b.Body, name),
        MUsing u => u.Name == name || Writes(u.Body, name),
        _ => false,
    };

    static bool Names(MExpr e, string name) => e is MCode c && c.Text == name;

    /// <summary>
    /// With writesOnly: true when the expression may write `name` (++, --, ref/out, nested assignment in a lambda,
    /// or opaque code mentioning it). Otherwise any mention.
    /// </summary>
    static bool Mentions(MExpr e, string name, bool writesOnly)
    {
        switch (e)
        {
            case MCode c:
                return !writesOnly ? c.Text == name : c.Text != name && ContainsWord(c.Text, name);   // opaque text using it: assume a write
            case MUnary u when u.Op is "++" or "--":
                return Names(u.E, name) || Mentions(u.E, name, writesOnly);
            case MArg a when a.Prefix.StartsWith("ref") || a.Prefix.StartsWith("out"):
                return Names(a.E, name) || Mentions(a.E, name, writesOnly);
            case MLambda l:
                return Writes(l.Body, name) || (l.ExprBody != null && Mentions(l.ExprBody, name, writesOnly));
        }
        return Children(e).Any(x => Mentions(x, name, writesOnly));
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
        _ => [],
    };

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
