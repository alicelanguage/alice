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
            if (IsKw("using") && !(Peek(1).Kind == "id" && Peek(2).Text == "="))
            {
                Next();
                into.Add(At(new UsingItem { Name = DottedName() }, tok));
                EndStmt();
            }
            else if (IsKw("require"))
            {
                Next();
                if (t[p].Kind != "str") throw Err("require expects a string path");
                into.Add(At(new RequireItem { Path = Next().Text }, tok));
                EndStmt();
            }
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
            else if (top && IsKw("rule") && Peek(1).Kind == "id" && Peek(2).Kind == "op" && Peek(2).Text == "(") into.Add(Rule());
            else if (IsStructNew()) into.Add(StructNew());
            else if (IsConstDecl()) into.Add(Const());
            else
            {
                if (!top) throw Err("statements are not allowed inside a namespace");
                into.Add(At(new StmtItem { Stmt = Statement() }, tok));
            }
        }
    }

    /// <summary>A record has a body when its next line starts a member; otherwise it is a one-line declaration.</summary>
    bool RecordHasBody(int recordCol)
    {
        int k = p;
        while (t[k].Kind == "nl") k++;
        if (k == p) return true;
        var x = t[k];
        if (x.Col <= recordCol) return false;   // members must be indented under the record
        return (x.Kind == "id" && " def getter property setter include private protected abstract ".Contains(" " + x.Text + " ")) || (x.Kind == "op" && x.Text == "@[");
    }

    List<Constraint> Where()
    {
        Next();
        var list = new List<Constraint>();
        while (true)
        {
            var c = At(new Constraint { Param = Next().Text }, t[p - 1]);
            Expect(":");
            while (true)
            {
                if (t[p].Kind == "id" && " struct class unmanaged notnull new ".Contains(" " + Cur.Text + " ")) c.Keywords.Add(Next().Text);
                else c.Types.Add(Type());
                if (!IsOp(",")) break;
                if (Peek(1).Kind == "const" && Peek(2).Kind == "op" && Peek(2).Text == ":") break;
                Next();
            }
            list.Add(c);
            if (!IsOp(",")) break;
            Next();
        }
        return list;
    }

    bool IsTypeStart()
    {
        if (IsKw("abstract") && Peek(1).Kind == "id" && Peek(1).Text != "def") return true;
        return t[p].Kind == "id" && " class struct module interface enum lib record ".Contains(" " + t[p].Text + " ");
    }

    /// <summary>
    /// `rule name(x in -10..10, b : Bool) ... end`: a pure method returning Bool over a finite domain whose bounds are
    /// integer literals, so the domain is visible in the source (RULES.al, proven by `alice prove`).
    /// </summary>
    MethodDecl Rule()
    {
        var m = At(new MethodDecl(), Cur);
        Next();
        string name = Next().Text;
        m.Name = "rule_" + name;
        m.RuleName = name;
        m.IsPure = true;
        m.IsStatic = true;
        var saved = locals;
        locals = new List<HashSet<string>> { new() };
        Expect("(");
        SkipNl();
        while (!IsOp(")"))
        {
            var pt = Cur;
            var d = new RuleDomain { Name = Next().Text };
            if (IsKw("in"))
            {
                Next();
                d.Lo = SignedLiteral();
                if (!IsOp("..") && !IsOp("...")) throw Err("expected `..` or `...` in a rule domain");
                bool excl = Next().Text == "...";
                d.Hi = SignedLiteral() - (excl ? 1 : 0);
                if (d.Hi < d.Lo) throw Err($"empty domain for `{d.Name}`");
            }
            else
            {
                Expect(":");
                var ty = Type();
                if (ty.Name != "Bool") throw Err("a rule variable is `x in lo..hi` (integer literals) or `b : Bool`");
                d.IsBool = true;
            }
            bool wide = d.Lo < int.MinValue || d.Hi > int.MaxValue;
            m.Params.Add(At(new Param { Name = d.Name, Type = new TypeRef { Name = d.IsBool ? "Bool" : wide ? "Int64" : "Int32" } }, pt));
            DeclareLocal(d.Name);
            m.Domains.Add(d);
            if (IsOp(",")) { Next(); SkipNl(); }
        }
        Next();
        m.Ret = new TypeRef { Name = "Bool" };
        SkipNl();
        m.Body = Block(" end ");
        ExpectKw("end");
        locals = saved;
        m.File = file;
        return m;
    }

    long SignedLiteral()
    {
        bool neg = false;
        if (IsOp("-")) { Next(); neg = true; }
        if (t[p].Kind != "int") throw Err("rule domain bounds are integer literals");
        long v = long.Parse(Next().Text.Replace("_", ""));
        return neg ? -v : v;
    }

    bool IsDefStart() => IsKw("def") || ((IsKw("private") || IsKw("protected") || IsKw("abstract") || IsKw("async") || IsKw("pure") || IsKw("safe") || IsKw("unsafe") || IsKw("inline")) && (Peek(1).Text == "def" || Peek(2).Text == "def" || Peek(3).Text == "def"));

    bool IsStructNew() => t[p].Kind == "const" && Peek(1).Text == "=" && Peek(2).Text == "Struct" && Peek(3).Text == "." && Peek(4).Text == "new";

    /// <summary>Ruby `Point = Struct.new(:x, :y)`: a value-equality record whose fields are dynamically typed.</summary>
    TypeDecl StructNew()
    {
        Token tok = Cur;
        var d = At(new TypeDecl { Kind = "record", Name = Next().Text }, tok);
        Next(); Next(); Next(); Next();
        Expect("(");
        while (!IsOp(")"))
        {
            if (t[p].Kind != "sym") throw Err("Struct.new expects :field names");
            Token ft = Next();
            d.RecordParams.Add(At(new Param { Name = ft.Text, Type = new TypeRef { Name = "Any" } }, ft));
            if (IsOp(",")) Next();
        }
        Next();
        EndStmt();
        return d;
    }

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
        if (kw == "record" && IsKw("struct")) { Next(); kw = "record struct"; }
        if (kw == "lib") return Lib(tok);
        if (kw == "enum") return Enum(tok);
        if (t[p].Kind != "const") throw Err("expected type name");
        var d = At(new TypeDecl { Kind = kw, Name = Next().Text, IsAbstract = isAbstract }, tok);
        d.Attrs = TakeAttrs();
        if (IsOp("(") && !Cur.Space && !d.Kind.StartsWith("record"))
        {
            Next();
            d.TypeParams.Add(Next().Text);
            while (IsOp(",")) { Next(); d.TypeParams.Add(Next().Text); }
            Expect(")");
        }
        if (d.Kind.StartsWith("record"))
        {
            Expect("(");
            SkipNl();
            while (!IsOp(")"))
            {
                while (IsOp("@[")) { Attr(); SkipNl(); }
                var held = TakeAttrs();
                var rp = Param();
                rp.Attrs = held;
                d.RecordParams.Add(rp);
                if (IsOp(",")) { Next(); SkipNl(); }
            }
            Next();
        }
        if (IsOp("<"))
        {
            Next();
            d.Bases.Add(Type());
            while (IsOp(",")) { Next(); d.Bases.Add(Type()); }
        }
        if (IsKw("where")) d.Constraints = Where();
        if (d.Kind.StartsWith("record") && !RecordHasBody(tok.Col)) { EndStmt(); return d; }
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
            else if (IsKw("include") || IsKw("extend"))
            {
                bool ext = Next().Text == "extend";
                d.Mixins.Add((Type(), ext));
                while (IsOp(",")) { Next(); d.Mixins.Add((Type(), ext)); }
                EndStmt();
            }
            else if (IsKw("attr_accessor") || IsKw("attr_reader") || IsKw("attr_writer"))
            {
                // Ruby style: types are inferred from the instance variable assignments
                string kind = Next().Text switch { "attr_reader" => "getter", "attr_writer" => "setter", _ => "property" };
                while (true)
                {
                    if (t[p].Kind != "sym") throw Err("expected :name");
                    var a = At(new AccessorDecl { Kind = kind, Name = Next().Text, Untyped = true }, tok);
                    d.Members.Add(a);
                    if (!IsOp(",")) break;
                    Next();
                }
                EndStmt();
            }
            else if (t[p].Kind == "id" && Cur.Text == "define_method")
            {
                // `define_method :name do |a, b| ... end` -> an ordinary method (untyped params are specialized)
                Next();
                bool paren = IsOp("(");
                if (paren) Next();
                if (t[p].Kind != "sym") throw Err("define_method expects :name");
                var m = At(new MethodDecl { Name = Next().Text, IsStatic = d.Kind == "module" }, tok);
                if (paren) Expect(")");
                var saved = locals;
                locals = new List<HashSet<string>> { new() };
                var lam = BlockLambda();
                locals = saved;
                m.Params = lam.Params;
                m.Body = lam.Body;
                d.Members.Add(m);
                EndStmt();
            }
            else if (IsKw("alias"))
            {
                Next();
                var al = At(new AliasDecl(), tok);
                al.NewName = Next().Text;
                if (IsOp(",")) Next();
                al.OldName = Next().Text;
                d.Members.Add(al);
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
            else if (d.Kind is "class" or "module" or "struct")
            {
                // Ruby: executable class body (macros like `field "name"`, loops calling define_method) —
                // collected into a static __class_body run when the module loads, in declaration order
                var cb = d.Members.OfType<MethodDecl>().FirstOrDefault(m => m.Name == "__class_body");
                if (cb == null)
                {
                    cb = At(new MethodDecl { Name = "__class_body", IsStatic = true, Ret = new TypeRef { Name = "Nil" } }, tok);
                    d.Members.Add(cb);
                }
                var saved = locals;
                locals = cbLocals.TryGetValue(d, out var l) ? l : cbLocals[d] = new List<HashSet<string>> { new() };
                cb.Body.Add(Statement());
                locals = saved;
            }
            else throw Err("unexpected token in type body");
        }
    }

    readonly Dictionary<TypeDecl, List<HashSet<string>>> cbLocals = new();

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
            List<Param>? fields = null;
            if (IsOp("(") && !Cur.Space)
            {
                Next();
                fields = new List<Param>();
                while (!IsOp(")"))
                {
                    fields.Add(Param());
                    if (IsOp(",")) Next();
                }
                Next();
            }
            e.Fields.Add(fields);
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
            while (IsOp("@[")) { Attr(); SkipNl(); }
            Token ft = Cur;
            ExpectKw("fun");
            var f = At(new FunDecl { Name = Next().Text, Attrs = TakeAttrs() }, ft);
            f.Entry = f.Name;
            if (IsOp("=")) { Next(); f.Entry = Next().Text; }
            if (IsOp("("))
            {
                Next();
                while (!IsOp(")"))
                {
                    string mod = (IsKw("out") || IsKw("ref")) && Peek(1).Kind == "id" ? Next().Text : "";
                    var pr = new Param { Name = Next().Text, Mod = mod };
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
        bool inline = false;
        while (IsKw("private") || IsKw("protected") || IsKw("abstract") || IsKw("async") || IsKw("pure") || IsKw("safe") || IsKw("unsafe") || IsKw("inline"))
        {
            string mod = Next().Text;
            if (mod == "abstract") m.IsAbstract = true;
            else if (mod == "async") m.IsAsync = true;
            else if (mod == "pure") m.IsPure = true;
            else if (mod == "safe") m.IsSafe = true;
            else if (mod == "unsafe") m.IsUnsafe = true;
            else if (mod == "inline") inline = true;
            else m.Vis = mod;
        }
        ExpectKw("def");
        m.Attrs = TakeAttrs();
        if (inline && !m.Attrs.Contains("Inline")) m.Attrs.Add("Inline");   // `inline def` = `def @[Inline]`
        m.IsStatic = ownerKind == "module" || ownerKind == "program";
        if (IsKw("self") && Peek(1).Kind == "op" && Peek(1).Text == ".")
        {
            Next();
            Next();
            m.IsStatic = true;
            m.SelfDef = true;
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
            while (IsOp(",") && Peek(1).Kind == "const" && !(Peek(2).Kind == "op" && Peek(2).Text == ":")) { Next(); m.TypeParams.Add(Next().Text); }
        }
        if (IsKw("where")) m.Constraints = Where();
        if (m.IsAbstract || ownerKind == "interface")
        {
            m.HasBody = false;
            EndStmt();
            locals = saved;
            return m;
        }
        if (IsOp("="))
        {
            // endless def: `def sq(x : Int32) : Int32 = x * x`
            Token et = Next();
            SkipNl();
            m.Body = new List<Stmt> { At(new ExprStmt { Expr = Expression() }, et) };
            EndStmt();
            m.File = file;
            int ln = m.Line - lineBase;
            if (ln >= 0 && ln < srcLines.Length) m.SourceText = srcLines[ln];
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
        int endLine = Cur.Line;
        ExpectKw("end");
        locals = saved;
        m.File = file;
        int from = m.Line - lineBase, to = endLine - lineBase;
        if (from >= 0 && to < srcLines.Length) m.SourceText = string.Join("\n", srcLines[from..(to + 1)]);
        return m;
    }

    Param Param()
    {
        var pr = At(new Param(), Cur);
        if (IsKw("own") && Peek(1).Kind == "id") { Next(); pr.Own = true; }
        else if (IsKw("borrow") && Peek(1).Kind == "id") { Next(); pr.Borrow = true; }
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
