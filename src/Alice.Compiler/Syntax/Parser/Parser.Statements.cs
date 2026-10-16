namespace Alice.Compiler.Syntax;

public sealed partial class Parser
{
    bool IsStop(string stops)
    {
        if (t[p].Kind == "eof") return true;
        if (t[p].Kind == "id" && stops.Contains(" " + t[p].Text + " ")) return true;
        return stops.Contains(" } ") && IsOp("}");
    }

    List<Stmt> Block(string stops)
    {
        var list = new List<Stmt>();
        while (true)
        {
            SkipNl();
            if (t[p].Kind == "eof")
            {
                var words = stops.Trim().Split(' ');
                throw Err($"expected '{words[^1]}'");
            }
            if (IsStop(stops)) return list;
            list.Add(Statement());
        }
    }

    Stmt Statement()
    {
        Token tok = Cur;
        Stmt s;
        if (IsKw("if") || IsKw("unless")) s = If();
        else if (IsKw("while") || IsKw("until")) s = While();
        else if (IsKw("for")) s = For();
        else if (IsKw("case")) s = Case();
        else if (IsKw("begin")) s = Begin();
        else if (IsKw("loop") && (Peek(1).Text == "do" || Peek(1).Text == "{")) s = Loop();
        else s = Simple();
        s.Line = tok.Line;
        while (IsKw("if") || IsKw("unless"))
        {
            bool neg = Next().Text == "unless";
            Expr c = Expression();
            if (neg) c = At(new UnaryExpr { Op = "!", E = c }, tok);
            var w = At(new IfStmt(), tok);
            w.Conds.Add(c);
            w.Bodies.Add(new List<Stmt> { s });
            s = w;
        }
        EndStmt();
        return s;
    }

    Stmt Simple()
    {
        Token tok = Cur;
        if (IsKw("return"))
        {
            Next();
            return At(new ReturnStmt { Value = AtStmtEnd() ? null : ExprList() }, tok);
        }
        if (IsKw("break")) { Next(); return At(new BreakStmt(), tok); }
        if (IsKw("retry")) { Next(); return At(new RetryStmt(), tok); }
        if (IsKw("next"))
        {
            Next();
            return At(new NextStmt { Value = AtStmtEnd() ? null : Expression() }, tok);
        }
        if (IsKw("raise")) { Next(); return At(new RaiseStmt { Value = Expression() }, tok); }
        if (IsKw("val") && Peek(1).Kind == "id")
        {
            Next();
            var d = At(new LocalDecl { Name = Next().Text, IsVal = true }, tok);
            if (IsOp(":")) { Next(); d.Type = Type(); }
            Expect("=");
            SkipNl();
            d.Value = Expression();
            DeclareLocal(d.Name);
            return d;
        }
        if (t[p].Kind == "id" && !IsKeyword(Cur.Text) && Peek(1).Kind == "op" && Peek(1).Text == ":" && Peek(1).Space)
        {
            var d = At(new LocalDecl { Name = Next().Text }, tok);
            Next();
            d.Type = Type();
            if (IsOp("=")) { Next(); SkipNl(); d.Value = Expression(); }
            DeclareLocal(d.Name);
            return d;
        }
        if (t[p].Kind == "id" && !IsKeyword(Cur.Text) && Peek(1).Kind == "op" && Peek(1).Text == "=")
        {
            string name = Next().Text;
            Next();
            SkipNl();
            Expr v = Expression();
            DeclareLocal(name);
            return At(new Assign { Target = At(new NameExpr { Name = name }, tok), Value = v }, tok);
        }
        if (t[p].Kind == "id" && !IsKeyword(Cur.Text) && Peek(1).Kind == "op" && Peek(1).Text == "," && IsMultiAssign())
        {
            var m = At(new MultiAssign(), tok);
            m.Names.Add(Next().Text);
            while (IsOp(",")) { Next(); m.Names.Add(Next().Text); }
            Expect("=");
            m.Values.Add(Expression());
            while (IsOp(",")) { Next(); m.Values.Add(Expression()); }
            foreach (var n in m.Names) DeclareLocal(n);
            return m;
        }
        Expr ex = Expression();
        if (IsOp("="))
        {
            Next();
            SkipNl();
            return At(new Assign { Target = ex, Value = Expression() }, tok);
        }
        if (t[p].Kind == "op" && " += -= *= /= %= |= &= ^= <<= >>= **= ||= &&= ".Contains(" " + Cur.Text + " "))
        {
            string op = Next().Text;
            SkipNl();
            return At(new Assign { Target = ex, Op = op, Value = Expression() }, tok);
        }
        return At(new ExprStmt { Expr = ex }, tok);
    }

    bool IsMultiAssign()
    {
        int k = p;
        while (t[k].Kind == "id" || (t[k].Kind == "op" && t[k].Text == ",")) k++;
        return t[k].Kind == "op" && t[k].Text == "=";
    }

    Expr ExprList()
    {
        Token tok = Cur;
        Expr e = Expression();
        if (!IsOp(",")) return e;
        var tl = At(new TupleLit(), tok);
        tl.Items.Add(e);
        while (IsOp(",")) { Next(); tl.Items.Add(Expression()); }
        return tl;
    }

    IfStmt If()
    {
        Token tok = Cur;
        bool neg = Next().Text == "unless";
        var s = At(new IfStmt(), tok);
        Expr c = Expression();
        if (neg) c = At(new UnaryExpr { Op = "!", E = c }, tok);
        if (IsKw("then")) Next();
        s.Conds.Add(c);
        s.Bodies.Add(Block(" elsif else end "));
        while (IsKw("elsif"))
        {
            Next();
            s.Conds.Add(Expression());
            if (IsKw("then")) Next();
            s.Bodies.Add(Block(" elsif else end "));
        }
        if (IsKw("else"))
        {
            Next();
            s.Else = Block(" end ");
        }
        ExpectKw("end");
        return s;
    }

    WhileStmt While()
    {
        Token tok = Cur;
        bool neg = Next().Text == "until";
        Expr c = Expression();
        if (neg) c = At(new UnaryExpr { Op = "!", E = c }, tok);
        if (IsKw("do")) Next();
        var w = At(new WhileStmt { Cond = c, Body = Block(" end ") }, tok);
        ExpectKw("end");
        return w;
    }

    WhileStmt Loop()
    {
        Token tok = Next();
        bool brace = IsOp("{");
        Next();
        var w = At(new WhileStmt { Cond = At(new LitExpr { Kind = "bool", Text = "true" }, tok), Body = Block(brace ? " } " : " end ") }, tok);
        if (brace) Expect("}"); else ExpectKw("end");
        return w;
    }

    ForStmt For()
    {
        Token tok = Next();
        var f = At(new ForStmt(), tok);
        f.Names.Add(Next().Text);
        while (IsOp(",")) { Next(); f.Names.Add(Next().Text); }
        ExpectKw("in");
        f.Iter = Expression();
        if (IsKw("do")) Next();
        foreach (var n in f.Names) DeclareLocal(n);
        f.Body = Block(" end ");
        ExpectKw("end");
        return f;
    }

    CaseStmt Case()
    {
        Token tok = Next();
        var s = At(new CaseStmt(), tok);
        if (t[p].Kind != "nl") s.Subject = Expression();
        SkipNl();
        while (IsKw("when") || IsKw("in"))
        {
            bool rubyIn = Cur.Text == "in";
            var w = At(new When(), Next());
            while (true)
            {
                w.Patterns.Add(rubyIn && IsOp("[") ? ArrayPattern() : Expression());
                if (!IsOp(",")) break;
                Next();
                SkipNl();
            }
            if (IsKw("then")) Next();
            w.Body = Block(" when in else end ");
            s.Whens.Add(w);
        }
        if (IsKw("else"))
        {
            Next();
            s.Else = Block(" end ");
        }
        ExpectKw("end");
        return s;
    }

    ArrayPatternExpr ArrayPattern()
    {
        var ap = At(new ArrayPatternExpr(), Next());
        while (!IsOp("]"))
        {
            if (IsOp("*")) { Next(); ap.Rest = Next().Text; DeclareLocal(ap.Rest); }
            else
            {
                if (ap.Rest != null) throw Err("the *rest element must be last");
                string n = Next().Text;
                ap.Names.Add(n);
                DeclareLocal(n);
            }
            if (IsOp(",")) Next();
        }
        Next();
        return ap;
    }

    BeginStmt Begin()
    {
        Token tok = Next();
        var b = At(new BeginStmt { Body = Block(" rescue ensure end ") }, tok);
        while (IsKw("rescue"))
        {
            var r = At(new Rescue(), Next());
            if (t[p].Kind == "id")
            {
                r.Name = Next().Text;
                DeclareLocal(r.Name);
                if (IsOp(":")) { Next(); r.Type = Type(); }
            }
            else if (t[p].Kind == "const") r.Type = Type();
            r.Body = Block(" rescue ensure end ");
            b.Rescues.Add(r);
        }
        if (IsKw("ensure"))
        {
            Next();
            b.Ensure = Block(" end ");
        }
        ExpectKw("end");
        return b;
    }
}
