namespace Alice.Compiler.Syntax;

public sealed partial class Parser
{
    const string Levels = "|| ;&& ;== != === ;< > <= >= <=> ;| ;^ ;& ;<< >> ;+ - ;* / % ";
    static readonly string[] LevelOps = Levels.Split(';');

    public Expr Expression()
    {
        Token tok = Cur;
        Expr l = NotOperand();
        while (IsKw("and") || IsKw("or"))
        {
            string op = Next().Text == "and" ? "&&" : "||";
            SkipNl();
            l = At(new BinaryExpr { Op = op, L = l, R = NotOperand() }, tok);
        }
        return l;
    }

    Expr NotOperand()
    {
        Token tok = Cur;
        if (IsKw("not"))
        {
            Next();
            return At(new UnaryExpr { Op = "!", E = NotOperand() }, tok);
        }
        return Ternary();
    }

    Expr Ternary()
    {
        Token tok = Cur;
        Expr c = RangeExpr();
        if (!IsOp("?")) return c;
        Next();
        SkipNl();
        Expr a = Ternary();
        SkipNl();
        Expect(":");
        SkipNl();
        return At(new TernaryExpr { C = c, A = a, B = Ternary() }, tok);
    }

    Expr RangeExpr()
    {
        Token tok = Cur;
        Expr a = Binary(0);
        if (!IsOp("..") && !IsOp("...")) return a;
        bool excl = Next().Text == "...";
        return At(new RangeExpr { A = a, B = Binary(0), Excl = excl }, tok);
    }

    Expr Binary(int level)
    {
        if (level == LevelOps.Length) return Unary();
        Token tok = Cur;
        string ops = " " + LevelOps[level];
        Expr l = Binary(level + 1);
        while (t[p].Kind == "op" && ops.Contains(" " + Cur.Text + " "))
        {
            string op = Next().Text;
            SkipNl();
            l = At(new BinaryExpr { Op = op == "===" ? "==" : op, L = l, R = Binary(level + 1) }, tok);
        }
        return l;
    }

    Expr Unary()
    {
        Token tok = Cur;
        if (IsOp("-") || IsOp("!") || IsOp("~") || IsOp("+"))
        {
            string op = Next().Text;
            Expr e = Unary();
            if (op == "+") return e;
            if (op == "-" && e is LitExpr lit && (lit.Kind == "int" || lit.Kind == "float") && !lit.Text.StartsWith("-") && !lit.Text.StartsWith("("))
                return At(new LitExpr { Kind = lit.Kind, Text = "-" + lit.Text }, tok);
            return At(new UnaryExpr { Op = op, E = e }, tok);
        }
        if (IsKw("await"))
        {
            Next();
            return At(new AwaitExpr { E = Unary() }, tok);
        }
        if (IsOp("&") && Peek(1).Kind == "id")
        {
            Next();
            return At(new UnaryExpr { Op = "&", E = Unary() }, tok);
        }
        return Power();
    }

    Expr Power()
    {
        Token tok = Cur;
        Expr b = Postfix();
        if (!IsOp("**")) return b;
        Next();
        return At(new BinaryExpr { Op = "**", L = b, R = Unary() }, tok);
    }

    bool BlockFollows() => IsOp("{") || (IsKw("do") && !noDo);

    LambdaExpr? OptBlock() => BlockFollows() ? BlockLambda() : null;

    Expr Postfix()
    {
        Expr e = Primary();
        while (true)
        {
            Token tok = Cur;
            if (IsOp(".") || IsOp("&."))
            {
                bool safe = Next().Text == "&.";
                if (IsOp("("))
                {
                    Next();
                    e = At(new InvokeExpr { Target = e, Args = Args(")") }, tok);
                    continue;
                }
                Token nt = Next();
                if (nt.Kind != "id" && nt.Kind != "const") throw Err("expected member name");
                string name = nt.Text;
                if (e is ConstExpr ce && name == "new")
                {
                    var n = At(new NewExpr { TypeRef = ce.Ref }, tok);
                    CallTail(n.Args, out n.Block);
                    e = n;
                    continue;
                }
                if (e is ConstExpr ce2 && nt.Kind == "const" && !(IsOp("(") && !Cur.Space) && ce2.Ref.Args.Count == 0)
                {
                    ce2.Ref.Name += "." + name;
                    if (IsOp("(") && !Cur.Space) ce2.Ref = TypeFromName(ce2.Ref.Name, tok);
                    continue;
                }
                if (name == "call")
                {
                    var inv = At(new InvokeExpr { Target = e }, tok);
                    CallTail(inv.Args, out _);
                    e = inv;
                    continue;
                }
                if (name == "nil?" || name == "not_nil!") { e = At(new TypeTestExpr { Kind = name, E = e }, tok); continue; }
                if (name == "is_a?" || name == "as")
                {
                    Expect("(");
                    e = At(new TypeTestExpr { Kind = name == "as" ? "as" : "is", E = e, Ref = Type() }, tok);
                    Expect(")");
                    continue;
                }
                var call = At(new CallExpr { Recv = e, Name = name, Safe = safe }, tok);
                call.Parens = IsOp("(") && !Cur.Space;
                CallTail(call.Args, out call.Block);
                e = call;
            }
            else if (IsOp("::"))
            {
                Next();
                string seg = Next().Text;
                if (e is ConstExpr ce)
                {
                    ce.Ref.Name += "." + seg;
                    if (IsCtorCall()) e = CtorCall(ce.Ref, tok);
                    else if (IsOp("(") && !Cur.Space) ce.Ref = TypeFromName(ce.Ref.Name, tok);
                }
                else throw Err("'::' needs a type on the left");
            }
            else if (IsOp("[") && !Cur.Space)
            {
                Next();
                e = At(new IndexExpr { Target = e, Args = Args("]") }, tok);
            }
            else return e;
        }
    }

    /// <summary>Arguments after a callee: `(a, b)`, command args `a, b`, then an optional block.</summary>
    void CallTail(List<Arg> args, out LambdaExpr? block)
    {
        if (IsOp("(") && !Cur.Space)
        {
            Next();
            args.AddRange(Args(")"));
        }
        else if (CanStartArg()) args.AddRange(CommandArgs());
        block = OptBlock();
    }

    bool CanStartArg()
    {
        Token c = Cur;
        if (!c.Space) return false;
        switch (c.Kind)
        {
            case "int":
            case "float":
            case "str":
            case "char":
            case "ivar":
            case "const":
            case "sym":
                return true;
            case "id":
                return c.Text == "not" || c.Text == "nil" || c.Text == "true" || c.Text == "false" || c.Text == "self" || !IsKeyword(c.Text);
            case "op":
                if (c.Text == "->" || c.Text == "(" || c.Text == "[" || c.Text == "!") return true;
                if (c.Text == "-" || c.Text == "*" || c.Text == "&") return !Peek(1).Space;
                return false;
        }
        return false;
    }

    List<Arg> CommandArgs()
    {
        bool saved = noDo;
        noDo = true;
        var parts = new List<Arg> { Argument() };
        while (IsOp(","))
        {
            Next();
            SkipNl();
            parts.Add(Argument());
        }
        noDo = saved;
        return parts;
    }

    List<Arg> Args(string close)
    {
        bool saved = noDo;
        noDo = false;
        var parts = new List<Arg>();
        SkipNl();
        while (!IsOp(close))
        {
            parts.Add(Argument());
            SkipNl();
            if (IsOp(",")) { Next(); SkipNl(); }
            else if (!IsOp(close)) throw Err($"expected ',' or '{close}'");
        }
        Next();
        noDo = saved;
        return parts;
    }

    Arg Argument()
    {
        var a = At(new Arg(), Cur);
        if ((IsKw("out") || IsKw("ref")) && Peek(1).Kind == "id")
        {
            a.Mod = Next().Text;
            string n = Next().Text;
            if (a.Mod == "out" && !IsLocal(n)) { a.OutName = n; DeclareLocal(n); }
            else a.Value = At(new NameExpr { Name = n }, t[p - 1]);
            return a;
        }
        if (t[p].Kind == "id" && Peek(1).Kind == "op" && Peek(1).Text == ":" && !Peek(1).Space)
        {
            a.Name = Next().Text;
            Next();
        }
        else if (IsOp("*") && !Peek(1).Space) Next();
        a.Value = Expression();
        return a;
    }

    Expr Primary()
    {
        Token c = Cur;
        switch (c.Kind)
        {
            case "int":
            case "float":
            case "char":
                Next();
                return At(new LitExpr { Kind = c.Kind, Text = c.Text }, c);
            case "sym":
                Next();
                return At(new LitExpr { Kind = "str", Text = c.Text }, c);
            case "str":
                Next();
                return StringLit(c);
            case "ivar":
                Next();
                return At(new IvarExpr { Name = c.Text }, c);
            case "const":
                Next();
                if (IsCtorCall()) return CtorCall(new TypeRef { Name = c.Text, Line = c.Line }, c);
                return At(new ConstExpr { Ref = TypeFromName(c.Text, c) }, c);
            case "id":
                return IdPrimary();
            case "op":
                if (c.Text == "(")
                {
                    Next();
                    SkipNl();
                    Expr e = ExprList();
                    SkipNl();
                    Expect(")");
                    return e;
                }
                if (c.Text == "[") return ArrayLiteral();
                if (c.Text == "{") return HashLiteral();
                if (c.Text == "->") return LambdaLiteral();
                if (c.Text == "::")
                {
                    Next();
                    Token n = Next();
                    return At(new ConstExpr { Ref = TypeFromName("global::" + n.Text, n) }, c);
                }
                break;
        }
        throw Err("unexpected token");
    }

    Expr IdPrimary()
    {
        Token c = Cur;
        string n = c.Text;
        switch (n)
        {
            case "nil": Next(); return At(new LitExpr { Kind = "nil", Text = "null" }, c);
            case "true":
            case "false": Next(); return At(new LitExpr { Kind = "bool", Text = n }, c);
            case "self": Next(); return At(new SelfExpr(), c);
            case "if":
            case "unless":
            case "case":
            case "begin":
                {
                    Stmt s = n == "case" ? Case() : n == "begin" ? Begin() : If();
                    s.Line = c.Line;
                    return At(new StmtExpr { Stmt = s }, c);
                }
            case "super":
                {
                    Next();
                    var s = At(new SuperExpr(), c);
                    if (IsOp("(") && !Cur.Space) { Next(); s.Args = Args(")"); }
                    return s;
                }
            case "typeof":
            case "sizeof":
                {
                    Next();
                    Expect("(");
                    var tt = At(new TypeTestExpr { Kind = n, Ref = Type() }, c);
                    Expect(")");
                    return tt;
                }
            case "uninitialized":
                Next();
                return At(new TypeTestExpr { Kind = "default", Ref = Type() }, c);
            case "spawn":
                Next();
                return At(new SpawnExpr { Body = LambdaBody(new List<Param>()) }, c);
            case "yield":
                {
                    Next();
                    var y = At(new YieldExpr(), c);
                    CallTail(y.Args, out _);
                    return y;
                }
        }
        if (IsKeyword(n)) throw Err("unexpected keyword");
        Next();
        if (IsLocal(n))
        {
            if (IsOp("(") && !Cur.Space)
            {
                Next();
                return At(new InvokeExpr { Target = At(new NameExpr { Name = n }, c), Args = Args(")") }, c);
            }
            return At(new NameExpr { Name = n }, c);
        }
        var call = At(new CallExpr { Name = n }, c);
        if (IsOp("<") && !Cur.Space && Peek(1).Kind == "const" && GenericCallAhead())
        {
            Next();
            call.TypeArgs.Add(Type());
            while (IsOp(",")) { Next(); call.TypeArgs.Add(Type()); }
            Expect(">");
        }
        call.Parens = IsOp("(") && !Cur.Space;
        CallTail(call.Args, out call.Block);
        if (!call.Parens && call.Args.Count == 0 && call.Block == null && call.TypeArgs.Count == 0)
            return At(new NameExpr { Name = n }, c);
        return call;
    }

    /// <summary>`Name(expr, ...)` constructs (Rust tuple-struct style) unless the parens hold type arguments.</summary>
    bool IsCtorCall()
    {
        if (!IsOp("(") || Cur.Space) return false;
        var n = Peek(1);
        if (n.Kind == "op" && n.Text == ")") return true;
        if (n.Kind == "const") return Peek(2).Kind == "op" && (Peek(2).Text == "." || Peek(2).Text == "::") && Peek(3).Kind != "const";
        return !(n.Kind == "op" && n.Text == "{");
    }

    Expr CtorCall(TypeRef type, Token tok)
    {
        Next();
        return At(new NewExpr { TypeRef = type, Args = Args(")") }, tok);
    }

    bool GenericCallAhead()
    {
        int k = p + 1;
        int d = 1;
        while (k < t.Count && d > 0)
        {
            Token x = t[k];
            if (x.Kind == "op" && x.Text == "<") d++;
            else if (x.Kind == "op" && x.Text == ">") d--;
            else if (!(x.Kind == "const" || (x.Kind == "op" && (x.Text == "," || x.Text == "(" || x.Text == ")" || x.Text == "::")))) return false;
            k++;
        }
        return d == 0 && k < t.Count && t[k].Kind == "op" && t[k].Text == "(";
    }
}
