using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// An owned array (ADR 0016) is `Own&lt;T&gt;.Rent(n)`, an opaque call, so the JIT cannot tell that its length is `n`
/// and keeps a bounds check on `buf[k]` in `for (k = 0; k &lt; n; k++)`, which `new T[n]` would not need. When `n` is a
/// local or parameter written only by its declaration and the array is written only by its own, `buf.Length`
/// equals `n` everywhere, so loops bounded by `n` that use the array are bounded by `buf.Length` instead.
/// </summary>
public sealed class OwnedBounds : MirRewriter, IMirPass
{
    public string Name => "owned-bounds";

    List<MStmt> method = null!;
    readonly List<(string Array, string Length)> stable = [];

    public new void Run(List<MStmt> body)
    {
        method = body;
        Collect(body);
        if (stable.Count > 0) base.Run(body);
    }

    void Collect(List<MStmt>? body)
    {
        foreach (var s in body ?? [])
        {
            if (s is MDecl { RentLength: { } n } d && WrittenOnlyBy(d.Name, d) && WrittenOnlyBy(n, null))
                stable.Add((d.Name, n));
            foreach (var inner in Bodies(s)) Collect(inner);
        }
    }

    /// <summary>No statement but `decl` (and the length's own declaration, if any) writes `name`.</summary>
    bool WrittenOnlyBy(string name, MDecl? decl)
    {
        int decls = 0;
        bool other = false;
        void Scan(List<MStmt>? body)
        {
            foreach (var s in body ?? [])
            {
                if (s is MDecl d && d.Name == name)
                {
                    decls++;
                    if (decl != null && d != decl) other = true;
                    if (d.Init != null && Writes(d.Init, name)) other = true;
                }
                else if (Bodies(s).Any() ? Shallow(s, name) : Writes(s, name)) other = true;
                foreach (var inner in Bodies(s)) Scan(inner);
            }
        }
        Scan(method);
        return !other && decls <= 1;
    }

    /// <summary>Writes by a compound statement's own header, not its nested bodies (those are scanned separately).</summary>
    static bool Shallow(MStmt s, string name) => s switch
    {
        MFor f => f.Var == name,
        MForeach fe => fe.Vars.Contains(name),
        MUsing u => u.Name == name,
        MIf i => Writes(i.Cond, name),
        MWhile w => Writes(w.Cond, name),
        _ => false,
    };

    /// <summary>
    /// A non-compound statement that may write `name`. Unlike <see cref="MirFacts.Writes(List{MStmt}?, string)"/>,
    /// opaque code counts only when it has a write form for the name (`name =`, `name +=`, `++name`, `ref name`):
    /// the parallel-loop probe passes locals by value to generated helpers as text.
    /// </summary>
    static bool Writes(MStmt s, string name) => s switch
    {
        MAssign a => a.Target is MCode { Text: var t } && t == name || Writes(a.Target, name) || Writes(a.Value, name),
        MDecl d => d.Name == name || (d.Init != null && Writes(d.Init, name)),
        MDeconstruct dc => dc.Names.Contains(name) || Writes(dc.Value, name),
        MExprStmt es => Writes(es.E, name),
        MReturn r => r.Value != null && Writes(r.Value, name),
        MThrow t => Writes(t.Value, name),
        _ => Bodies(s).Any() ? Shallow(s, name) : MirFacts.Writes([s], name),
    };

    static bool Writes(MExpr e, string name) => e switch
    {
        MCode c => WriteForm(c.Text, name),
        MUnary { Op: "++" or "--", E: MCode { Text: var t } } when t == name => true,
        MArg a when (a.Prefix.StartsWith("ref") || a.Prefix.StartsWith("out")) && a.E is MCode { Text: var t } && t == name => true,
        MLambda l => MirFacts.Writes(l.Body, name) || (l.ExprBody != null && Writes(l.ExprBody, name)),
        _ => MirFacts.Children(e).Any(x => Writes(x, name)),
    };

    static bool WriteForm(string text, string name)
    {
        var n = System.Text.RegularExpressions.Regex.Escape(name);
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            $@"(?<![\w.])({n}\s*([-+*/%&|^]|<<|>>>?|\?\?)?=(?!=)|{n}\s*(\+\+|--)|(\+\+|--)\s*{n}(?![\w])|\b(ref|out|in)\s+{n}(?![\w]))");
    }

    static IEnumerable<List<MStmt>> Bodies(MStmt s) => s switch
    {
        MIf i => new[] { i.Then, i.Else }.OfType<List<MStmt>>(),
        MWhile w => [w.Body],
        MFor f => [f.Body],
        MForeach fe => [fe.Body],
        MTry t => t.Catches.Select(c => c.Body).Prepend(t.Body).Append(t.Finally).OfType<List<MStmt>>(),
        MBlock b => [b.Body],
        MUsing u => [u.Body],
        _ => [],
    };

    protected override void Visit(MStmt s)
    {
        if (s is not MFor { Cmp: "<", End: MCode { Text: var end } } f) return;
        foreach (var (array, length) in stable)
            if (end == length && Uses(f.Body, array))
            {
                f.End = new MCode(array + ".Length");
                return;
            }
    }

    static bool Uses(List<MStmt> body, string name)
    {
        var finder = new NameFinder(name);
        finder.Run(body);
        return finder.Found;
    }

    sealed class NameFinder(string name) : MirRewriter
    {
        public bool Found;
        protected override MExpr Rewrite(MExpr e)
        {
            if (e is MIndex { Target: MCode { Text: var t } } && t == name) Found = true;
            return e;
        }
    }
}
