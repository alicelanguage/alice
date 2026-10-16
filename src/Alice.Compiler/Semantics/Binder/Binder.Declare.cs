using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>Phase 1: register every Alice type so later phases can resolve names in any order.</summary>
    void Collect()
    {
        Types["Program"] = Program;
        foreach (var f in Files) CollectUsings(f.Items);
        foreach (var f in Files)
        {
            file = f.Path;
            CollectItems(f.Items, "");
        }
    }

    void CollectUsings(List<Item> items)
    {
        foreach (var it in items)
        {
            if (it is UsingItem u) usings.Add(u.Name);
            else if (it is NamespaceItem n) CollectUsings(n.Items);
        }
    }

    /// <summary>`class String ... end` with only methods reopens a .NET type: Ruby monkey patching as extension methods.</summary>
    bool IsMonkeyPatch(TypeDecl d) =>
        d.Kind is "class" or "struct" && !Types.ContainsKey(d.Name) && d.Bases.Count == 0 && d.TypeParams.Count == 0
        && d.Members.Count > 0 && d.Members.All(m => m is MethodDecl md && md.Name != "initialize")
        && ((PrimNames.TryGetValue(d.Name, out var pt) && pt.Kind is "prim" or "class") || Clr.Find(d.Name, 0, usings) != null);

    void CollectItems(List<Item> items, string ns)
    {
        foreach (var it in items)
        {
            switch (it)
            {
                case UsingItem u:
                    usings.Add(u.Name);
                    break;
                case NamespaceItem n:
                    CollectItems(n.Items, ns == "" ? n.Name : ns + "." + n.Name);
                    break;
                case TypeDecl d when IsMonkeyPatch(d):
                    {
                        var x = new TypeSym { Name = d.Name.Replace(".", "_") + "_AliceExt", Kind = "extension", Namespace = ns, Decl = d };
                        d.Sym = x;
                        Extensions.Add(x);
                        break;
                    }
                case TypeDecl d:
                    RegisterType(d.Name, d.Kind, ns, d);
                    CollectItems(d.Members, ns == "" ? d.Name : ns + "." + d.Name);
                    break;
                case EnumDecl e:
                    {
                        RegisterType(e.Name, e.IsAdt ? "adt" : "enum", ns, e);
                        var es = (TypeSym)e.Sym!;
                        if (!e.IsAdt) break;
                        es.Type.Kind = "class";
                        for (int i = 0; i < e.Names.Count; i++)
                        {
                            string vn = e.Names[i];
                            if (es.Variants.Any(v => v.Name == vn)) continue;
                            var vs = new TypeSym { Name = vn, Kind = "variant", Parent = es, Namespace = ns };
                            vs.Type = new AType("class", es.Type.Cs + "." + vn) { Sym = vs };
                            es.Variants.Add(vs);
                            Types[e.Name + "." + vn] = vs;
                            Types.TryAdd(vn, vs);
                        }
                        break;
                    }
                case LibDecl l:
                    RegisterType(l.Name, "lib", ns, l);
                    break;
            }
        }
    }

    void RegisterType(string name, string kind, string ns, Item decl)
    {
        if (!Types.TryGetValue(name, out var s))
        {
            s = new TypeSym { Name = name, Kind = kind, Namespace = ns };
            string tk = kind switch { "struct" or "record struct" => "struct", "interface" => "iface", "enum" => "enum", _ => "class" };
            s.Type = new AType(tk, ns == "" ? name : "global::" + ns + "." + name) { Sym = s };
            Types[name] = s;
            if (ns != "") Types[ns + "." + name] = s;
        }
        decl.Sym = s;
        if (decl is TypeDecl td)
        {
            s.Decl ??= td;
            if (td.TypeParams.Count > 0) s.TypeParams = td.TypeParams;
            if (td.IsAbstract) s.IsAbstract = true;
            if (td.Attrs.Contains("Stack")) s.IsStack = true;
        }
    }

    /// <summary>Phase 2: bases, fields, accessors, method signatures.</summary>
    void Declare()
    {
        foreach (var f in Files)
        {
            file = f.Path;
            DeclareItems(f.Items, Program);
        }
        foreach (var s in Types.Values.Distinct())
            if (s.BaseClass != null) s.BaseClass.HasSubclass = true;
        foreach (var s in Types.Values.Distinct().ToList())
            if (s.Decl != null && s.Decl.Attrs.Contains("SoA") && s.SoaStore == null) CreateSoaStore(s);
    }

    void DeclareItems(List<Item> items, TypeSym owner)
    {
        foreach (var it in items)
        {
            switch (it)
            {
                case NamespaceItem n:
                    DeclareItems(n.Items, Program);
                    break;
                case TypeDecl d:
                    DeclareType(d);
                    break;
                case EnumDecl e:
                    DeclareEnum(e);
                    break;
                case LibDecl l:
                    DeclareLib(l);
                    break;
                case MethodDecl m:
                    DeclareMethod(m, owner);
                    break;
                case ConstDecl c:
                    DeclareConst(c, owner);
                    break;
            }
        }
    }

    void DeclareType(TypeDecl d)
    {
        var s = (TypeSym)d.Sym!;
        if (s.Kind == "extension")
        {
            s.ExtTarget ??= ResolveType(new TypeRef { Name = d.Name });
            s.Type = s.ExtTarget;
            foreach (var m in d.Members.OfType<MethodDecl>())
            {
                m.IsStatic = false;
                DeclareMethod(m, s);
            }
            return;
        }
        typeParams = s.TypeParams;
        if (s.FieldOrder.Count == 0)
            foreach (var p in d.RecordParams)
            {
                s.Fields[p.Name] = new FieldSym { Name = p.Name, CsName = Mangle(p.Name), Type = ResolveType(p.Type!), Kind = "getter", Owner = s };
                s.FieldOrder.Add(p.Name);
            }
        foreach (var b in d.Bases)
        {
            var bt = ResolveType(b);
            s.Bases.Add(bt);
            if (d.Kind == "class" && bt.Sym != null && bt.Sym.Kind == "class" && s.BaseClass == null) s.BaseClass = bt.Sym;
        }
        foreach (var it in d.Members)
        {
            switch (it)
            {
                case AccessorDecl a:
                    var at = a.Type != null ? ResolveType(a.Type) : LiteralType(a.Init);
                    if (!at.IsKnown) throw Err(a, $"type required for {a.Kind} {a.Name}");
                    s.Fields[a.Name] = new FieldSym { Name = a.Name, CsName = Mangle(a.Name), Type = at, Kind = a.Kind, IsStatic = s.Kind == "module", Owner = s };
                    a.Sym = s.Fields[a.Name];
                    break;
                case FieldDecl fd:
                    s.Fields[fd.Name] = new FieldSym { Name = fd.Name, CsName = Mangle(fd.Name), Type = ResolveType(fd.Type), Kind = "field", IsStatic = s.Kind == "module", Owner = s };
                    fd.Sym = s.Fields[fd.Name];
                    break;
                case MethodDecl m:
                    DeclareMethod(m, s);
                    break;
                case ConstDecl c:
                    DeclareConst(c, s);
                    break;
                case TypeDecl nested:
                    DeclareType(nested);
                    typeParams = s.TypeParams;
                    break;
                case EnumDecl e:
                    DeclareEnum(e);
                    break;
            }
        }
        typeParams = new List<string>();
    }

    void DeclareEnum(EnumDecl e)
    {
        var es = (TypeSym)e.Sym!;
        if (!e.IsAdt)
        {
            foreach (var n in e.Names) es.Fields[n] = new FieldSym { Name = n, CsName = n, Type = es.Type, Kind = "const", IsStatic = true, Owner = es };
            return;
        }
        for (int i = 0; i < e.Names.Count; i++)
        {
            var vs = es.Variants.First(v => v.Name == e.Names[i]);
            if (vs.FieldOrder.Count > 0) continue;
            foreach (var p in e.Fields[i] ?? new List<Param>())
            {
                vs.Fields[p.Name] = new FieldSym { Name = p.Name, CsName = Mangle(p.Name), Type = ResolveType(p.Type!), Kind = "getter", Owner = vs };
                vs.FieldOrder.Add(p.Name);
            }
        }
    }

    /// <summary>`@[SoA] record struct P(...)` gets `P.Store`: one array column per field + add/[]/remove_at/size/clear.</summary>
    /// <summary>Creates the store on first use (signatures may mention SoA(T) before T is declared).</summary>
    void EnsureSoaStore(TypeSym s)
    {
        if (s.FieldOrder.Count == 0 && s.Decl != null)
        {
            var saved = typeParams;
            typeParams = s.TypeParams;
            foreach (var p in s.Decl.RecordParams)
            {
                s.Fields[p.Name] = new FieldSym { Name = p.Name, CsName = Mangle(p.Name), Type = ResolveType(p.Type!), Kind = "getter", Owner = s };
                s.FieldOrder.Add(p.Name);
            }
            typeParams = saved;
        }
        CreateSoaStore(s);
    }

    void CreateSoaStore(TypeSym s)
    {
        if (s.FieldOrder.Count == 0) throw Err(s.Decl!, "@[SoA] needs positional fields: record struct Name(a : T, ...)");
        var st = new TypeSym { Name = "Store", Kind = "soa", Namespace = s.Namespace, SoaOf = s };
        st.Type = new AType("class", s.Type.Cs + ".Store") { Sym = st };
        s.SoaStore = st;
        foreach (var f in s.FieldOrder)
            st.Fields[f] = new FieldSym { Name = f, CsName = s.Fields[f].CsName, Type = AType.Array(s.Fields[f].Type), Kind = "column", Owner = st };
        MethodSym M(string name, AType ret, params (string, AType)[] ps)
        {
            var m = new MethodSym { Name = name, CsName = name == "[]" ? "[]" : name, Owner = st, Ret = ret, RetDeclared = true };
            foreach (var (pn, pt) in ps) m.Params.Add(new ParamSym { Name = pn, CsName = pn, Type = pt });
            st.AddMethod(m);
            return m;
        }
        M("add", AType.Int, ("item", s.Type));
        M("size", AType.Int);
        M("clear", AType.Void);
        M("remove_at", AType.Void, ("index", AType.Int));
        M("[]", s.Type, ("index", AType.Int));
    }

    void DeclareConst(ConstDecl c, TypeSym owner)
    {
        var t = c.Type != null ? ResolveType(c.Type) : LiteralType(c.Value);
        if (!t.IsKnown) throw Err(c, $"constant {c.Name} needs a type: {c.Name} : Type = value");
        owner.Fields[c.Name] = new FieldSym { Name = c.Name, CsName = c.Name, Type = t, Kind = "const", IsStatic = true, Owner = owner };
        c.Sym = owner.Fields[c.Name];
    }

    void DeclareLib(LibDecl l)
    {
        var s = (TypeSym)l.Sym!;
        foreach (var f in l.Funs)
        {
            var m = new MethodSym { Name = f.Name, CsName = Mangle(f.Name), Owner = s, IsStatic = true, RetDeclared = true, Ret = f.Ret != null ? ResolveType(f.Ret) : AType.Void };
            foreach (var p in f.Params) m.Params.Add(new ParamSym { Name = p.Name, CsName = Mangle(p.Name), Type = ResolveType(p.Type!) });
            s.AddMethod(m);
        }
    }

    void DeclareMethod(MethodDecl d, TypeSym owner)
    {
        var saved = typeParams;
        typeParams = new List<string>(owner.TypeParams);
        typeParams.AddRange(d.TypeParams);
        var m = new MethodSym
        {
            Name = d.Name,
            CsName = Mangle(d.Name),
            Owner = owner,
            Decl = d,
            IsStatic = d.IsStatic,
            IsCtor = d.Name == "initialize",
            IsAsync = d.IsAsync,
            TypeParams = new List<string>(d.TypeParams),
        };
        foreach (var p in d.Params)
        {
            var ps = new ParamSym { Name = p.Name, CsName = Mangle(p.Name), HasDefault = p.Default != null, Splat = p.Splat, Block = p.Block, Auto = p.Auto };
            if (p.Type != null) ps.Type = ResolveType(p.Type);
            else
            {
                string tp = "T" + char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1);
                m.TypeParams.Add(tp);
                ps.Type = new AType("param", tp);
                ps.Inferred = true;
                m.HasInferredGenerics = true;
            }
            m.Params.Add(ps);
            if (p.Auto && owner.FindField(p.Name) == null)
                owner.Fields[p.Name] = new FieldSym { Name = p.Name, CsName = Mangle(p.Name), Type = ps.Type, Kind = "auto", Owner = owner };
        }
        if (d.Ret != null)
        {
            m.Ret = ResolveType(d.Ret);
            m.RetDeclared = true;
        }
        else if (!d.HasBody || m.IsCtor) m.RetDeclared = true;
        else m.Ret = AType.Unknown;
        d.Sym = m;
        owner.AddMethod(m);
        typeParams = saved;
    }

    static AType LiteralType(Expr? e) => e switch
    {
        LitExpr { Kind: "int" } l => l.Text.EndsWith("L") ? AType.Long : AType.Int,
        LitExpr { Kind: "float" } l => l.Text.EndsWith("f") ? AType.Float : AType.Double,
        LitExpr { Kind: "str" } => AType.String,
        InterpExpr => AType.String,
        LitExpr { Kind: "char" } => AType.Char,
        LitExpr { Kind: "bool" } => AType.Bool,
        _ => AType.Unknown,
    };
}
