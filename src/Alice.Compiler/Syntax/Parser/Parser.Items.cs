using System.Text;

namespace Alice.Compiler.Syntax;

public sealed partial class Parser
{
    FileNode File()
    {
        var f = new FileNode { Path = file, Line = 1 };
        PushLocals();
        DeclareLocal("ARGV");
        Items(f.Items, top: true);
        if (t[p].Kind != "eof") throw Err("unexpected 'end'");
        return f;
    }

    void Items(List<Item> into, bool top)
    {
        while (true)
        {
            SkipNl();
            if (t[p].Kind == "eof" || IsKw("end")) return;
            Token tok = Cur;
            if (IsKw("using"))
            {
                Next();
                into.Add(At(new UsingItem { Name = DottedName() }, tok));
                EndStmt();
            }
            else if (IsKw("require")) { Next(); Next(); EndStmt(); }
            else if (IsOp("@[")) Attr();
            else if (IsKw("namespace"))
            {
                Next();
                var ns = At(new NamespaceItem { Name = DottedName() }, tok);
                Items(ns.Items, top: false);
                ExpectKw("end");
                into.Add(ns);
            }
            else if (IsTypeStart()) into.Add(TypeItem());
            else if (IsDefStart()) into.Add(Def("program"));
            else if (IsConstDecl()) into.Add(Const());
            else
            {
                if (!top) throw Err("statements are not allowed inside a namespace");
                into.Add(At(new StmtItem { Stmt = Statement() }, tok));
            }
        }
    }

    bool IsTypeStart()
    {
        if (IsKw("abstract") && Peek(1).Kind == "id" && Peek(1).Text != "def") return true;
        return t[p].Kind == "id" && " class struct module interface enum lib ".Contains(" " + t[p].Text + " ");
    }

    bool IsDefStart() => IsKw("def") || ((IsKw("private") || IsKw("protected") || IsKw("abstract")) && Peek(1).Text == "def");

    bool IsConstDecl() => t[p].Kind == "const" && Peek(1).Kind == "op" && (Peek(1).Text == "=" || (Peek(1).Text == ":" && Peek(1).Space));

    string DottedName()
    {
        if (t[p].Kind != "const" && t[p].Kind != "id") throw Err("expected name");
        string n = Next().Text;
        while (IsOp(".") || IsOp("::"))
        {
            Next();
            n += "." + Next().Text;
        }
        return n;
    }

    void Attr()
    {
        Next();
        var sb = new StringBuilder();
        int d = 1;
        while (true)
        {
            Token c = t[p];
            if (c.Kind == "eof") throw Err("unterminated attribute");
            if (c.Kind == "op" && (c.Text == "[" || c.Text == "(")) d++;
            if (c.Kind == "op" && (c.Text == "]" || c.Text == ")")) d--;
            if (d == 0) { Next(); break; }
            if (c.Space && sb.Length > 0) sb.Append(' ');
            if (c.Kind == "str") sb.Append('"').Append(c.Text).Append('"');
            else if (c.Kind == "op" && c.Text == "::") sb.Append('.');
            else sb.Append(c.Text);
            Next();
        }
        attrs.Add(sb.ToString());
        SkipNl();
    }

    Item TypeItem()
    {
        Token tok = Cur;
        bool isAbstract = false;
        if (IsKw("abstract")) { Next(); isAbstract = true; }
        string kw = Next().Text;
        if (kw == "lib") return Lib(tok);
        if (kw == "enum") return Enum(tok);
        if (t[p].Kind != "const") throw Err("expected type name");
        var d = At(new TypeDecl { Kind = kw, Name = Next().Text, IsAbstract = isAbstract }, tok);
        d.Attrs = TakeAttrs();
        if (IsOp("(") && !Cur.Space)
        {
            Next();
            d.TypeParams.Add(Next().Text);
            while (IsOp(",")) { Next(); d.TypeParams.Add(Next().Text); }
            Expect(")");
        }
        if (IsOp("<"))
        {
            Next();
            d.Bases.Add(Type());
            while (IsOp(",")) { Next(); d.Bases.Add(Type()); }
        }
        Body(d);
        ExpectKw("end");
        return d;
    }

    void Body(TypeDecl d)
    {
        while (true)
        {
            SkipNl();
            if (IsKw("end") || t[p].Kind == "eof") return;
            Token tok = Cur;
            if (IsOp("@[")) Attr();
            else if (IsKw("include"))
            {
                Next();
                d.Bases.Add(Type());
                EndStmt();
            }
            else if (IsKw("getter") || IsKw("property") || IsKw("setter"))
            {
                var a = At(new AccessorDecl { Kind = Next().Text, Attrs = TakeAttrs() }, tok);
                a.Name = Next().Text;
                if (IsOp(":")) { Next(); a.Type = Type(); }
                if (IsOp("=")) { Next(); a.Init = FreshExpr(); }
                if (a.Type == null && a.Init == null) throw Err($"type required for {a.Kind} {a.Name}");
                d.Members.Add(a);
                EndStmt();
            }
            else if (t[p].Kind == "ivar")
            {
                var f = At(new FieldDecl { Name = Next().Text, Attrs = TakeAttrs() }, tok);
                Expect(":");
                f.Type = Type();
                if (IsOp("=")) { Next(); f.Init = FreshExpr(); }
                d.Members.Add(f);
                EndStmt();
            }
            else if (IsConstDecl()) d.Members.Add(Const());
            else if (IsTypeStart()) d.Members.Add(TypeItem());
            else if (IsDefStart()) d.Members.Add(Def(d.Kind));
            else throw Err("unexpected token in type body");
        }
    }

    Expr FreshExpr()
    {
        var saved = locals;
        locals = new List<HashSet<string>> { new() };
        Expr e = Expression();
        locals = saved;
        return e;
    }

    ConstDecl Const()
    {
        var c = At(new ConstDecl { Name = Next().Text, Attrs = TakeAttrs() }, t[p - 1]);
        if (IsOp(":")) { Next(); c.Type = Type(); }
        Expect("=");
        c.Value = FreshExpr();
        EndStmt();
        return c;
    }

    EnumDecl Enum(Token tok)
    {
        var e = At(new EnumDecl { Name = Next().Text, Attrs = TakeAttrs() }, tok);
        if (IsOp(":")) { Next(); e.Underlying = Type(); }
        while (true)
        {
            SkipNl();
            if (IsKw("end")) break;
            e.Names.Add(Next().Text);
            Expr? v = null;
            if (IsOp("=")) { Next(); v = FreshExpr(); }
            e.Values.Add(v);
            if (IsOp(",")) Next();
        }
        ExpectKw("end");
        return e;
    }

    LibDecl Lib(Token tok)
    {
        var l = At(new LibDecl { Name = Next().Text }, tok);
        foreach (var a in TakeAttrs())
            if (a.StartsWith("Link(")) l.LibName = a.Substring(6, a.Length - 8);
        while (true)
        {
            SkipNl();
            if (IsKw("end")) break;
            Token ft = Cur;
            ExpectKw("fun");
            var f = At(new FunDecl { Name = Next().Text }, ft);
            f.Entry = f.Name;
            if (IsOp("=")) { Next(); f.Entry = Next().Text; }
            if (IsOp("("))
            {
                Next();
                while (!IsOp(")"))
                {
                    var pr = new Param { Name = Next().Text };
                    Expect(":");
                    pr.Type = Type();
                    f.Params.Add(pr);
                    if (IsOp(",")) Next();
                }
                Next();
            }
            if (IsOp(":")) { Next(); f.Ret = Type(); }
            l.Funs.Add(f);
            EndStmt();
        }
        ExpectKw("end");
        return l;
    }

    MethodDecl Def(string ownerKind)
    {
        var m = At(new MethodDecl(), Cur);
        while (IsKw("private") || IsKw("protected") || IsKw("abstract"))
        {
            string mod = Next().Text;
            if (mod == "abstract") m.IsAbstract = true; else m.Vis = mod;
        }
        ExpectKw("def");
        m.Attrs = TakeAttrs();
        m.IsStatic = ownerKind == "module" || ownerKind == "program";
        if (IsKw("self") && Peek(1).Kind == "op" && Peek(1).Text == ".")
        {
            Next();
            Next();
            m.IsStatic = true;
        }
        if (IsOp("[") && Peek(1).Text == "]") { Next(); Next(); m.Name = "[]"; }
        else m.Name = Next().Text;
        if (IsOp("<") && !Cur.Space)
        {
            Next();
            m.TypeParams.Add(Next().Text);
            while (IsOp(",")) { Next(); m.TypeParams.Add(Next().Text); }
            Expect(">");
        }
        var saved = locals;
        locals = new List<HashSet<string>> { new() };
        if (IsOp("("))
        {
            Next();
            SkipNl();
            while (!IsOp(")"))
            {
                m.Params.Add(Param());
                if (IsOp(",")) { Next(); SkipNl(); }
            }
            Next();
        }
        if (IsOp(":")) { Next(); m.Ret = Type(); }
        if (IsKw("forall"))
        {
            Next();
            m.TypeParams.Add(Next().Text);
            while (IsOp(",")) { Next(); m.TypeParams.Add(Next().Text); }
        }
        if (m.IsAbstract || ownerKind == "interface")
        {
            m.HasBody = false;
            EndStmt();
            locals = saved;
            return m;
        }
        SkipNl();
        if (m.Name == "initialize" && IsKw("super"))
        {
            Next();
            m.SuperInit = new List<Arg>();
            if (IsOp("(")) { Next(); m.SuperInit = Args(")"); }
            EndStmt();
        }
        m.Body = Block(" end ");
        ExpectKw("end");
        locals = saved;
        return m;
    }

    Param Param()
    {
        var pr = At(new Param(), Cur);
        if (IsOp("*")) { Next(); pr.Splat = true; }
        if (IsOp("&")) { Next(); pr.Block = true; }
        pr.Auto = t[p].Kind == "ivar";
        pr.Name = Next().Text;
        if (IsOp(":")) { Next(); pr.Type = pr.Block ? ProcType() : Type(); }
        if (IsOp("=")) { Next(); pr.Default = Expression(); }
        DeclareLocal(pr.Name);
        return pr;
    }
}
