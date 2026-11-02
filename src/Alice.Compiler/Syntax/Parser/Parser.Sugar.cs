namespace Alice.Compiler.Syntax;

/// <summary>A rescue clause being parsed: a bare `raise` inside re-raises its exception (named or hidden).</summary>
sealed class RescueScope
{
    public string? Name;
    public string? Hidden;
}

/// <summary>
/// Ruby/Crystal sugar that the parser lowers to the existing AST, so the binder sees nothing new: implicit block
/// parameters (`_1`, `it`), `break value`, rescue clause forms, `expr rescue v`, `begin ... end while c`, modifier
/// loops, endless/beginless ranges.
/// </summary>
public sealed partial class Parser
{
    string Hidden(string stem) => "__" + stem + ++hidden;

    CompileException ErrAt(Node n, string msg) => new($"{file}:{n.Line}:{n.Col}: {msg}");

    static T Mk<T>(T node, Node at) where T : Node
    {
        node.Line = at.Line;
        node.Col = at.Col;
        return node;
    }

    // ---- implicit block parameters: `{ _1 * _2 }`, `{ it * 2 }` (Ruby 2.7 / 3.4)

    static bool IsNumbered(string n) => n.Length == 2 && n[0] == '_' && n[1] is >= '1' and <= '9';

    static bool IsImplicitParam(string n) => n == "it" || IsNumbered(n);

    /// <summary>A block written without `|params|` that reads `_1`..`_9` gets `|_1, .., _N|`; one that reads `it` gets `|it|`.</summary>
    void ImplicitParams(LambdaExpr lam)
    {
        if (lam.Params.Count > 0 || lam.MethodRef != null) return;
        var excluded = new HashSet<NameExpr>();
        var uses = new List<NameExpr>();
        AstWalk.Each(lam.Body.Cast<Node>(), n =>
        {
            if (n is LambdaExpr inner && inner.Params.Any(p => IsImplicitParam(p.Name)))
                AstWalk.Each(inner.Body.Cast<Node>(), x => { if (x is NameExpr ix) excluded.Add(ix); });
            else if (n is NameExpr ne && IsImplicitParam(ne.Name) && !IsLocal(ne.Name)) uses.Add(ne);
        });
        uses.RemoveAll(excluded.Contains);
        if (uses.Count == 0) return;
        bool it = uses.Any(u => u.Name == "it");
        var numbered = uses.Where(u => u.Name != "it").ToList();
        if (it && numbered.Count > 0) throw ErrAt(numbered[0], "unexpected numbered parameter in a block that also uses `it`; use one of them");
        if (it) { lam.Params.Add(Mk(new Param { Name = "it" }, lam)); return; }
        int max = numbered.Max(u => u.Name[1] - '0');
        for (int i = 1; i <= max; i++) lam.Params.Add(Mk(new Param { Name = "_" + i }, lam));
    }

    // ---- break with a value

    /// <summary>
    /// Applies `f` to every `break value` this construct owns (not those of nested loops or blocks): `f` returns the
    /// statements replacing it, or null to keep a plain `break`.
    /// </summary>
    void OwnBreaks(List<Stmt> body, Func<BreakStmt, Expr, List<Stmt>?> f)
    {
        for (int i = 0; i < body.Count; i++)
        {
            switch (body[i])
            {
                case BreakStmt b when breakValues.Remove(b, out var v):
                    if (f(b, v) is { } rep)
                    {
                        body.RemoveAt(i);
                        body.InsertRange(i, rep);
                        i += rep.Count - 1;
                    }
                    break;
                case IfStmt s:
                    foreach (var b in s.Bodies) OwnBreaks(b, f);
                    if (s.Else != null) OwnBreaks(s.Else, f);
                    break;
                case CaseStmt c:
                    foreach (var w in c.Whens) OwnBreaks(w.Body, f);
                    if (c.Else != null) OwnBreaks(c.Else, f);
                    break;
                case BeginStmt bs:
                    OwnBreaks(bs.Body, f);
                    foreach (var r in bs.Rescues) OwnBreaks(r.Body, f);
                    if (bs.Ensure != null) OwnBreaks(bs.Ensure, f);
                    break;
                case UsingStmt us:
                    OwnBreaks(us.Body, f);
                    break;
            }
        }
    }

    /// <summary>A statement-level loop drops a side-effect-free break value; any other value is reported.</summary>
    void LoopBreaks(List<Stmt> body) => OwnBreaks(body, (b, v) => v is LitExpr or NameExpr or IvarExpr or SelfExpr ? null
        : throw ErrAt(b, "unexpected `break` value: a loop keeps its break value only as `x = loop do ... break v ... end`"));

    void BlockBreaks(List<Stmt> body) => OwnBreaks(body, (b, _) =>
        throw ErrAt(b, "unexpected `break` value inside a block: blocks cannot return a value through break; use `x = loop do ... end`, `find`, or `next value`"));

    /// <summary>`x = loop do ... break v ... end`: `x` is declared before the loop and each `break v` becomes `x = v; break`.</summary>
    WhileStmt LoopValue(Expr target, bool declare)
    {
        if (target is not (NameExpr or IvarExpr)) throw ErrAt(target, "expected a variable or @field before `= loop do`");
        Expr Fresh() => target is NameExpr n ? Mk(new NameExpr { Name = n.Name }, target) : Mk(new IvarExpr { Name = ((IvarExpr)target).Name }, target);
        var w = Loop((b, v) => [Mk(new Assign { Target = Fresh(), Value = v }, b), b]);
        if (declare) spliced.Add(Mk(new LocalDecl { Name = ((NameExpr)target).Name }, target));
        return w;
    }

    bool LoopAhead() => IsKw("loop") && (Peek(1).Text == "do" || (Peek(1).Kind == "op" && Peek(1).Text == "{"));

    // ---- rescue clauses: `rescue => e`, `rescue A, B => e`, `rescue e : A | B`, `else`, `ensure`

    const string RescueStops = " rescue else ensure ";

    /// <summary>Parses the rescue/else/ensure tail after `body` (up to, not including, `end`).</summary>
    BeginStmt RescueTail(List<Stmt> body, Token tok, bool asValue)
    {
        var b = At(new BeginStmt { Body = body }, tok);
        while (IsKw("rescue")) RescueClause(b);
        List<Stmt>? elseBody = null;
        Token? elseTok = null;
        if (IsKw("else"))
        {
            if (b.Rescues.Count == 0) throw Err("unexpected 'else' without a rescue clause");
            elseTok = Next();
            elseBody = Block(" ensure end ");
        }
        if (IsKw("ensure"))
        {
            Next();
            b.Ensure = Block(" end ");
        }
        if (elseBody == null) return b;
        if (asValue) throw Err("unexpected `else` in a begin/rescue used as a value; assign inside the branches instead");
        // `begin body rescue .. else e ensure f end` ->
        // `__okN = false; begin body; __okN = true rescue .. end; if __okN then e end` (+ ensure around all)
        string ok = Hidden("ok");
        NameExpr Ok() => At(new NameExpr { Name = ok }, elseTok!);
        Assign SetOk(bool v) => At(new Assign { Target = Ok(), Value = At(new LitExpr { Kind = "bool", Text = v ? "true" : "false" }, elseTok!) }, elseTok!);
        var inner = At(new BeginStmt { Body = body, Rescues = b.Rescues }, tok);
        if (body.Count == 0 || body[^1] is not (ReturnStmt or BreakStmt or NextStmt or RaiseStmt or RetryStmt)) body.Add(SetOk(true));
        var check = At(new IfStmt(), elseTok!);
        check.Conds.Add(Ok());
        check.Bodies.Add(elseBody);
        return At(new BeginStmt { Body = [SetOk(false), inner, check], Ensure = b.Ensure }, tok);
    }

    void RescueClause(BeginStmt b)
    {
        Token rt = Next();
        var types = new List<TypeRef>();
        string? name = null;
        if (t[p].Kind == "id" && !IsKw("then"))
        {
            name = Next().Text;   // rescue e : A | B
            if (IsOp(":"))
            {
                Next();
                types.Add(Type());
                while (IsOp("|")) { Next(); types.Add(Type()); }
            }
        }
        else if (t[p].Kind == "const")
        {
            types.Add(Type());   // rescue A, B => e / rescue A | B
            while (IsOp(",") || IsOp("|")) { Next(); SkipNl(); types.Add(Type()); }
        }
        if (IsOp("=>"))
        {
            Next();
            if (t[p].Kind != "id" || IsKeyword(Cur.Text)) throw Err("expected a variable name after `rescue ... =>`");
            name = Next().Text;
        }
        if (IsKw("then")) Next();
        if (name != null) DeclareLocal(name);
        // one clause per type: the body is parsed once for each, so every catch binds its own typed variable
        int start = p;
        IEnumerable<TypeRef?> tys = types.Count == 0 ? new TypeRef?[] { null } : types;
        foreach (var ty in tys)
        {
            p = start;
            var scope = new RescueScope { Name = name };
            rescues.Insert(0, scope);
            var body = Block(RescueStops + "end ");
            rescues.RemoveAt(0);
            b.Rescues.Add(At(new Rescue { Name = name ?? scope.Hidden, Type = ty, Body = body }, rt));
        }
    }

    /// <summary>A bare `raise` re-raises the exception of the innermost rescue clause.</summary>
    RaiseStmt Reraise(Token tok)
    {
        if (rescues.Count == 0) throw Err("unexpected bare `raise` outside a rescue clause: give the exception, `raise \"message\"`");
        var sc = rescues[0];
        string n = sc.Name ?? (sc.Hidden ??= Hidden("exc"));
        return At(new RaiseStmt { Value = At(new NameExpr { Name = n }, tok) }, tok);
    }

    /// <summary>`stmt rescue value`: the statement (or the assigned value) runs in a begin/rescue whose rescue yields `value`.</summary>
    Stmt RescueModifier(Stmt s, Token tok)
    {
        Token rt = Next();
        Expr v = Expression();
        Expr Guard(Expr e)
        {
            var b = At(new BeginStmt { Body = [At(new ExprStmt { Expr = e }, tok)] }, rt);
            b.Rescues.Add(At(new Rescue { Body = [At(new ExprStmt { Expr = v }, rt)] }, rt));
            return At(new StmtExpr { Stmt = b }, rt);
        }
        switch (s)
        {
            case Assign a:
                a.Value = Guard(a.Value);
                return a;
            case LocalDecl { Value: not null } d:
                d.Value = Guard(d.Value);
                return d;
            case ExprStmt es:
                {
                    var b = At(new BeginStmt { Body = [es] }, rt);
                    // `risky rescue nil`: a value with no effect is dropped (it is not a statement in C#)
                    var body = v is LitExpr or NameExpr or IvarExpr or SelfExpr ? new List<Stmt>() : [At(new ExprStmt { Expr = v }, rt)];
                    b.Rescues.Add(At(new Rescue { Body = body }, rt));
                    return b;
                }
        }
        throw Err("unexpected `rescue` modifier here; use begin ... rescue ... end");
    }

    // ---- modifier loops: `stmt while c`, `stmt until c`, `begin ... end while c` (body first, like do-while)

    Stmt ModifierLoop(Stmt s, Token tok, bool fromBegin)
    {
        bool until = Next().Text == "until";
        Expr c = Expression();
        if (until) c = At(new UnaryExpr { Op = "!", E = c }, tok);
        if (!fromBegin)
        {
            var body = new List<Stmt> { s };
            LoopBreaks(body);
            return At(new WhileStmt { Cond = c, Body = body }, tok);
        }
        // `begin body end while c` -> `__dwN = true; while __dwN || c; __dwN = false; body; end`
        string first = Hidden("dw");
        NameExpr F() => At(new NameExpr { Name = first }, tok);
        Assign SetF(bool v) => At(new Assign { Target = F(), Value = At(new LitExpr { Kind = "bool", Text = v ? "true" : "false" }, tok) }, tok);
        spliced.Add(SetF(true));
        var b = (BeginStmt)s;
        var loopBody = new List<Stmt> { SetF(false) };
        if (b.Rescues.Count == 0 && b.Ensure == null) loopBody.AddRange(b.Body); else loopBody.Add(b);
        LoopBreaks(loopBody);
        return At(new WhileStmt { Cond = At(new BinaryExpr { Op = "||", L = F(), R = c }, tok), Body = loopBody }, tok);
    }

    // ---- endless / beginless ranges

    /// <summary>Nothing can follow `a..` here: an endless range.</summary>
    bool RangeEndHere()
    {
        Token c = Cur;
        if (c.Kind is "nl" or "eof") return true;
        if (c.Kind == "op") return c.Text is ")" or "]" or "}" or "," or "|";
        return c.Kind == "id" && c.Text is "then" or "do" or "if" or "unless";
    }

    /// <summary>
    /// `xs[a..]` is `xs[a..-1]`; `xs[..b]` is `xs[0..b]`; in a `when`/`in` pattern the open side is Int32's bound;
    /// elsewhere `(a..)` counts up lazily to Int32::MAX. A beginless range needs an index or a pattern.
    /// </summary>
    Expr OpenRange(Token tok, Expr? a, Expr? b, bool excl)
    {
        LitExpr Int(string v) => At(new LitExpr { Kind = "int", Text = v }, tok);
        if (a == null)
        {
            if (rangeCtx == "") throw Err("unexpected beginless range: `..b` is supported in `xs[..b]` and in when/in patterns");
            return At(new RangeExpr { A = Int(rangeCtx == "index" ? "0" : "-2147483648"), B = b!, Excl = excl }, tok);
        }
        if (rangeCtx == "index") return At(new RangeExpr { A = a, B = Int("-1"), Excl = false }, tok);
        if (rangeCtx == "pattern") return At(new RangeExpr { A = a, B = Int("2147483647"), Excl = false }, tok);
        return At(new RangeExpr { A = a, B = Int("2147483647"), Excl = true }, tok);
    }

    // ---- compile-time names

    /// <summary>`__FILE__`, `__DIR__` (absolute, as in Crystal), `__LINE__`, `__method__` (nil outside a def).</summary>
    Expr? MagicName(Token c)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        switch (c.Text)
        {
            case "__FILE__":
                return At(new LitExpr { Kind = "str", Text = Esc(Path.GetFullPath(file)) }, c);
            case "__DIR__":
            case "__dir__":
                return At(new LitExpr { Kind = "str", Text = Esc(Path.GetDirectoryName(Path.GetFullPath(file)) ?? "") }, c);
            case "__LINE__":
                return At(new LitExpr { Kind = "int", Text = c.Line.ToString() }, c);
            case "__method__":
                return curDef == null ? At(new LitExpr { Kind = "nil", Text = "null" }, c) : At(new LitExpr { Kind = "str", Text = Esc(curDef) }, c);
        }
        return null;
    }
}
