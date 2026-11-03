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
        if (breakValues.Count > 0) throw ErrAt(breakValues.Keys.First(), "unexpected `break` value outside a loop");
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
            else if (IsKw("require") || IsKw("require_relative"))
            {
                bool relative = Next().Text == "require_relative";
                if (t[p].Kind != "str") throw Err("require expects a string path");
                string path = Next().Text;
                // `require_relative "lib/x"` is `require "./lib/x"` (paths are relative to the requiring file)
                if (relative && !path.StartsWith("./") && !path.StartsWith("../") && !path.StartsWith('/')) path = "./" + path;
                into.Add(At(new RequireItem { Path = path }, tok));
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
            else if (IsValConst()) { Next(); into.Add(Const()); }   // `val CUT = 3`: a constant is immutable already
            else
            {
                if (!top) throw Err("statements are not allowed inside a namespace");
                var st = Statement();
                foreach (var pre in spliced) into.Add(At(new StmtItem { Stmt = pre }, tok));
                spliced.Clear();
                into.Add(At(new StmtItem { Stmt = st }, tok));
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
        if (IsKw("affine") && Peek(1).Kind == "id" && Peek(1).Text == "enum") return true;
        return t[p].Kind == "id" && (" class struct module interface enum lib record ".Contains(" " + t[p].Text + " ")
            || t[p].Text == "delegate" && Peek(1).Kind == "const");
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
                // `x : Int32` / `x : Int64`: the whole range, proven by `alice prove --smt` (too large to enumerate)
                if (ty.Name is "Int32" or "Int64") { d.Lo = ty.Name == "Int32" ? int.MinValue : long.MinValue; d.Hi = ty.Name == "Int32" ? int.MaxValue : long.MaxValue; }
                else if (ty.Name == "Bool") d.IsBool = true;
                else throw Err("a rule variable is `x in lo..hi` (integer literals), `x : Int32` / `x : Int64` (alice prove --smt), or `b : Bool`");
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
        var text = Next().Text.Replace("_", "");
        try { long v = long.Parse(text); return neg ? -v : v; }
        catch (FormatException) { throw Err($"invalid integer literal '{text}"); }
    }

    bool IsDefStart() => IsKw("def") || ((IsKw("private") || IsKw("protected") || IsKw("abstract") || IsKw("async") || IsKw("pure") || IsKw("safe") || IsKw("unsafe") || IsKw("partial") || IsKw("inline")) && (Peek(1).Text == "def" || Peek(2).Text == "def" || Peek(3).Text == "def"))
        || IsKw("stream") && Peek(1).Text == "def";   // `stream def` (ADR 0038)

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

    bool IsValConst() => IsKw("val") && Peek(1).Kind == "const" && Peek(2).Kind == "op" && (Peek(2).Text == "=" || (Peek(2).Text == ":" && Peek(2).Space));

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
        bool affine = IsKw("affine") && Peek(1).Text == "enum";
        if (affine) Next();
        string kw = Next().Text;
        if (kw == "record" && IsKw("struct")) { Next(); kw = "record struct"; }
        if (kw == "lib") return Lib(tok);
        if (kw == "delegate")
        {
            var dg = At(new DelegateDecl { Name = Next().Text, Attrs = TakeAttrs() }, tok);
            if (IsOp("("))
            {
                Next();
                while (!IsOp(")"))
                {
                    var pr = new Param { Name = Next().Text };
                    Expect(":");
                    pr.Type = Type();
                    dg.Params.Add(pr);
                    if (IsOp(",")) Next();
                }
                Next();
            }
            if (IsOp(":")) { Next(); dg.Ret = Type(); }
            EndStmt();
            return dg;
        }
        if (kw == "enum") { var en = Enum(tok); en.Affine = affine; return en; }
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
        string savedVis = sectionVis;
        sectionVis = "public";
        try { BodyMembers(d); }
        finally { sectionVis = savedVis; }
    }

    static readonly HashSet<string> AccessorKeywords = ["getter", "getter?", "getter!", "property", "property?", "property!", "setter",
        "class_getter", "class_getter?", "class_property", "class_property?", "class_setter"];

    void BodyMembers(TypeDecl d)
    {
        while (true)
        {
            SkipNl();
            if (IsKw("end") || t[p].Kind == "eof") return;
            Token tok = Cur;
            if (t[p].Kind == "id" && Cur.Text is "private" or "protected" or "public" && Peek(1).Kind is "nl" or "eof")
            {
                // Ruby section form: every later `def` of this body gets the visibility
                sectionVis = Next().Text;
                EndStmt();
            }
            else if (t[p].Kind == "id" && Cur.Text is "private" or "protected" or "public" or "private_class_method" or "public_class_method" && Peek(1).Kind == "sym")
                VisibilityOf(d);
            else if (t[p].Kind == "id" && AccessorKeywords.Contains(Cur.Text) && Cur.Text != "getter" && Cur.Text != "property" && Cur.Text != "setter"
                || (IsKw("getter") || IsKw("property") || IsKw("setter")) && Peek(1).Kind == "id" && Peek(2).Kind == "op" && Peek(2).Text == ",")
                CrystalAccessors(d);
            else if (IsOp("@[")) Attr();
            else if (IsKw("refine") && d.Kind == "module")
            {
                // Ruby refinement: a monkey patch of a type that only files with `using <module>` see
                Next();
                var r = At(new TypeDecl { Kind = "class", Name = Type().Name, RefineScope = d.Name }, tok);
                if (IsKw("do")) Next();
                Body(r);
                ExpectKw("end");
                d.Members.Add(r);
                EndStmt();
            }
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
            else if (IsKw("getter") || IsKw("property") || IsKw("setter") || IsKw("event") && Peek(1).Kind is "id" or "const")   // `event PropertyChanged : ...` implements a .NET interface event
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
            else if (IsValConst()) { Next(); d.Members.Add(Const()); }
            else if (IsTypeStart()) d.Members.Add(TypeItem());
            else if (IsDefStart()) d.Members.Add(Def(d.Kind));
            else if (d.Kind is "class" or "module" or "struct")
            {
                // Ruby: executable class body (macros like `field "name"`, loops calling define_method) —
                // collected into a static __class_body run when the module loads, in declaration order
                var cb = ClassBody(d, tok);
                var saved = locals;
                locals = cbLocals.TryGetValue(d, out var l) ? l : cbLocals[d] = new List<HashSet<string>> { new() };
                var st = Statement();
                cb.Body.AddRange(spliced);
                spliced.Clear();
                cb.Body.Add(st);
                locals = saved;
            }
            else throw Err("unexpected token in type body");
        }
    }

    readonly Dictionary<TypeDecl, List<HashSet<string>>> cbLocals = new();

    /// <summary>The static `__class_body` of `d` (its executable class-body statements), created on first use.</summary>
    MethodDecl ClassBody(TypeDecl d, Token tok)
    {
        var cb = d.Members.OfType<MethodDecl>().FirstOrDefault(m => m.Name == "__class_body");
        if (cb == null)
        {
            cb = At(new MethodDecl { Name = "__class_body", IsStatic = true, Ret = new TypeRef { Name = "Nil" } }, tok);
            d.Members.Add(cb);
        }
        return cb;
    }

    /// <summary>`private :a, :b` / `private_class_method :s`: sets the visibility of methods defined above.</summary>
    void VisibilityOf(TypeDecl d)
    {
        string kw = Next().Text;
        bool cls = kw.EndsWith("_class_method");
        string vis = cls ? kw[..kw.IndexOf('_')] : kw;
        while (true)
        {
            if (t[p].Kind != "sym") throw Err($"expected :name after {kw}");
            Token st = Next();
            var ms = d.Members.OfType<MethodDecl>().Where(m => m.Name == st.Text && (cls ? m.IsStatic : !m.SelfDef)).ToList();
            if (ms.Count == 0) throw ErrAt(At(new NameExpr(), st), $"unexpected `{kw} :{st.Text}`: no {(cls ? "class " : "")}method `{st.Text}` is defined above it in {d.Name}");
            foreach (var m in ms) m.Vis = vis;
            if (!IsOp(",")) break;
            Next();
        }
        EndStmt();
    }

    /// <summary>
    /// Crystal accessor macros beyond `getter x : T`: `getter a, b : T` (several names), `getter? ok : Bool` (reader `ok?`),
    /// `getter! v : T` (`v` raises when nil, `v?` may be nil), `property?` / `property!` (plus a writer),
    /// `class_getter` / `class_property` / `class_setter` (a class variable `@@x` and class methods).
    /// Lowered to fields, accessors and methods the binder already knows.
    /// </summary>
    void CrystalAccessors(TypeDecl d)
    {
        Token tok = Next();
        string kw = tok.Text;
        var attrsHere = TakeAttrs();
        var names = new List<Token> { Next() };
        while (IsOp(",")) { Next(); names.Add(Next()); }
        TypeRef? type = null;
        Expr? init = null;
        if (IsOp(":")) { Next(); type = Type(); }
        if (IsOp("=")) { Next(); init = FreshExpr(); }
        EndStmt();
        bool cls = kw.StartsWith("class_");
        string basic = cls ? kw[6..] : kw;
        char suffix = basic[^1] is '?' or '!' ? basic[^1] : ' ';
        string kind = suffix == ' ' ? basic : basic[..^1];
        foreach (var nt in names)
        {
            string n = nt.Text;
            if (nt.Kind != "id") throw ErrAt(At(new NameExpr(), nt), $"expected a name after {kw}");
            IvarExpr Field() => At(new IvarExpr { Name = cls ? "@" + n : n }, nt);
            if (cls)
            {
                // class_getter x : T = v -> `@@x = v` in the class body; def self.x : T; @@x; end (+ def self.x=(v))
                if (init == null) throw ErrAt(At(new NameExpr(), nt), $"expected a value: {kw} {n}{(type != null ? " : T" : "")} = value (the class variable is typed from it)");
                ClassBody(d, nt).Body.Add(At(new Assign { Target = Field(), Value = init }, nt));
                if (kind != "setter") d.Members.Add(Synth(nt, n + (suffix == '?' ? "?" : ""), [], type, Field(), isStatic: true));
                if (kind is "property" or "setter")
                {
                    if (type == null) throw ErrAt(At(new NameExpr(), nt), $"type required for {kw} {n}");
                    d.Members.Add(SetterMethod(nt, n, type, Field(), isStatic: true));
                }
                continue;
            }
            if (suffix == '!')
            {
                // getter! v : T -> @v : T? ; def v : T = @v.not_nil! ; def v? : T? = @v
                if (type == null) throw ErrAt(At(new NameExpr(), nt), $"type required for {kw} {n}");
                var nonNil = new TypeRef { Name = type.Name, Args = type.Args, Special = type.Special, TupleNames = type.TupleNames, Line = type.Line, Col = type.Col };
                var nil = new TypeRef { Name = type.Name, Args = type.Args, Special = type.Special, TupleNames = type.TupleNames, Nullable = true, Line = type.Line, Col = type.Col };
                d.Members.Add(At(new FieldDecl { Name = n, Type = nil, Init = init, Attrs = attrsHere }, nt));
                // `.as(T)` after `not_nil!`: a nilable value type (Int32?) unwraps to T
                d.Members.Add(Synth(nt, n, [], nonNil, At(new TypeTestExpr { Kind = "as", Ref = nonNil, E = At(new TypeTestExpr { Kind = "not_nil!", E = Field() }, nt) }, nt)));
                d.Members.Add(Synth(nt, n + "?", [], nil, Field()));
                if (kind == "property") d.Members.Add(SetterMethod(nt, n, nonNil, Field(), isStatic: false));
                continue;
            }
            if (suffix == '?')
            {
                // getter? ok : Bool -> @ok : Bool ; def ok? : Bool = @ok   (property? also gets a writer `ok=`)
                if (kind == "property") d.Members.Add(At(new AccessorDecl { Kind = "setter", Name = n, Type = type, Init = init, Untyped = type == null && init == null, Attrs = attrsHere }, nt));
                else if (type != null) d.Members.Add(At(new FieldDecl { Name = n, Type = type, Init = init, Attrs = attrsHere }, nt));
                else d.Members.Add(At(new AccessorDecl { Kind = "getter", Name = n, Init = init, Untyped = init == null, Attrs = attrsHere }, nt));
                d.Members.Add(Synth(nt, n + "?", [], type, Field()));
                continue;
            }
            // getter a, b : T -> getter a : T; getter b : T (untyped: inferred from @a / @b, like attr_reader)
            d.Members.Add(At(new AccessorDecl { Kind = kind, Name = n, Type = type, Init = init, Untyped = type == null && init == null, Attrs = attrsHere }, nt));
        }
    }

    /// <summary>A parser-made method `def name(params) : ret; value; end`.</summary>
    MethodDecl Synth(Token at, string name, List<Param> ps, TypeRef? ret, Expr value, bool isStatic = false)
    {
        var m = At(new MethodDecl { Name = name, Params = ps, Ret = ret, IsStatic = isStatic, SelfDef = isStatic, File = file }, at);
        m.Body = [At(new ExprStmt { Expr = value }, at)];
        m.SourceText = $"def {(isStatic ? "self." : "")}{name}\n{Show(value)}\nend";
        return m;
    }

    /// <summary>`def name=(v : T)`: a writer method (see <see cref="SetterName"/>) assigning `field`.</summary>
    MethodDecl SetterMethod(Token at, string name, TypeRef type, IvarExpr field, bool isStatic)
    {
        var m = At(new MethodDecl { Name = SetterName(name), Params = [At(new Param { Name = "value", Type = type }, at)], IsStatic = isStatic, SelfDef = isStatic, File = file }, at);
        m.Body = [At(new Assign { Target = field, Value = At(new NameExpr { Name = "value" }, at) }, at)];
        m.SourceText = $"def {(isStatic ? "self." : "")}{name}=(value{(TypeText(type) is { } tt ? " : " + tt : "")})\n@{field.Name} = value\nend";
        return m;
    }

    /// <summary>Source spelling of a plain type reference (`Int32`, `Array(String)`, `T?`), null for tuple/proc types.</summary>
    static string? TypeText(TypeRef r)
    {
        if (r.Special != "" || r.Pointer) return null;
        var args = r.Args.Select(TypeText).ToList();
        if (args.Any(a => a == null)) return null;
        return r.Name + (args.Count > 0 ? "(" + string.Join(", ", args) + ")" : "") + (r.Nullable ? "?" : "");
    }

    static string Show(Expr e) => e switch
    {
        IvarExpr iv => "@" + iv.Name,
        TypeTestExpr { Kind: "as", Ref: { } r, E: TypeTestExpr { Kind: "not_nil!", E: IvarExpr iv } } => "@" + iv.Name + ".not_nil!" + (TypeText(r) is { } tt ? ".as(" + tt + ")" : ""),
        _ => "nil",
    };

    /// <summary>
    /// `def name=(v)` is the method `__set_name`; `recv.name = v` calls it when the receiver's type has one (the binder
    /// rewrites the assignment). A C# property cannot be used: `def name` stays a method, and C# forbids a method and a
    /// property of the same name.
    /// </summary>
    public static string SetterName(string name) => "__set_" + name;

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
        bool explicitVis = false;
        while (IsKw("private") || IsKw("protected") || IsKw("abstract") || IsKw("async") || IsKw("pure") || IsKw("safe") || IsKw("unsafe") || IsKw("partial") || IsKw("inline")
            || IsKw("stream") && Peek(1).Text == "def")
        {
            string mod = Next().Text;
            if (mod == "abstract") m.IsAbstract = true;
            else if (mod == "stream") m.IsStream = true;
            else if (mod == "async") m.IsAsync = true;
            else if (mod == "pure") m.IsPure = true;
            else if (mod == "safe") m.IsSafe = true;
            else if (mod == "unsafe") m.IsUnsafe = true;
            else if (mod == "partial") m.IsPartial = true;
            else if (mod == "inline") inline = true;
            else { m.Vis = mod; explicitVis = true; }
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
        if (!explicitVis && !m.SelfDef && ownerKind != "program") m.Vis = sectionVis;
        if (IsOp("[") && Peek(1).Text == "]") { Next(); Next(); m.Name = "[]"; }
        else m.Name = Next().Text;
        // `def -@` / `def +@` are the unary operators (`def -` with no parameters is too)
        if (m.Name is "-@" or "+@" or "~@" or "!@") m.Name = m.Name[..1];
        // `def name=(v)`: a writer (a `=` glued to the name, then the parameter list)
        bool setter = t[p - 1].Kind == "id" && IsOp("=") && !Cur.Space && Peek(1).Kind == "op" && Peek(1).Text == "(";
        if (setter) { Next(); m.Name = SetterName(m.Name); }
        string? savedDef = curDef;
        curDef = setter ? m.Name[6..] + "=" : m.Name;
        try { return DefRest(m, ownerKind, setter); }
        finally { curDef = savedDef; }
    }

    MethodDecl DefRest(MethodDecl m, string ownerKind, bool setter)
    {
        if (IsOp("<") && !Cur.Space)
        {
            Next();
            m.TypeParams.Add(Next().Text);
            while (IsOp(",")) { Next(); m.TypeParams.Add(Next().Text); }
            ExpectCloseAngle();
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
        if (m.IsAbstract || m.IsPartial || ownerKind == "interface" && !InterfaceBodyAhead())
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
        Token bodyTok = Cur;
        m.Body = Block(RescueStops + "end ");
        // def-level `rescue` / `else` / `ensure` (no `begin`): the whole body is the begin block
        if (IsKw("rescue") || IsKw("else") || IsKw("ensure")) m.Body = [RescueTail(m.Body, bodyTok, false)];
        // `def name=(@name)`: Crystal's writer shorthand assigns the field
        if (setter)
            foreach (var pr in m.Params.Where(x => x.Auto).Reverse())
            {
                pr.Auto = false;
                m.Body.Insert(0, Mk(new Assign { Target = Mk(new IvarExpr { Name = pr.Name }, pr), Value = Mk(new NameExpr { Name = pr.Name }, pr) }, pr));
            }
        int endLine = Cur.Line;
        ExpectKw("end");
        locals = saved;
        m.File = file;
        int from = m.Line - lineBase, to = endLine - lineBase;
        if (from >= 0 && to < srcLines.Length) m.SourceText = string.Join("\n", srcLines[from..(to + 1)]);
        return m;
    }

    /// <summary>An interface method with statements on the next line is a default implementation (C# 8 default
    /// interface method); another member, an attribute or `end` means it is abstract.</summary>
    bool InterfaceBodyAhead()
    {
        int k = p;
        while (t[k].Kind == "nl") k++;
        if (k == p) return false;   // `def f : T` followed by something on the same line
        var x = t[k];
        if (x.Kind == "eof" || x.Kind == "op" && x.Text == "@[") return false;
        return !(x.Kind == "id" && x.Text is "def" or "end" or "getter" or "property" or "setter" or "event" or "abstract" or "private" or "protected" or "async" or "pure" or "safe" or "unsafe" or "inline" or "stream");
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
