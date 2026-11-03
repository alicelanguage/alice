using System.Reflection;
using System.Reflection.Metadata;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Binder = Alice.Compiler.Semantics.Binder;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Il;

/// <summary>The result of an IL build: the PE image and portable PDB (null when any method was unsupported), the
/// statistics, and every unsupported node with its location.</summary>
public sealed record IlCompileResult(byte[]? Pe, byte[]? Pdb, IlStats Stats, IReadOnlyList<IlUnsupportedException> Errors)
{
    public bool Ok => Pe != null;
    /// <summary>The module (for <see cref="IlDisassembler"/>).</summary>
    public IlModule? Module { get; init; }
}

/// <summary>
/// D1.4: the direct IL backend (ADR 0033). Binds and lowers the program exactly as the C# backend does
/// (<see cref="Lowerer"/> + <see cref="Optimizer.Pipeline"/>), mirrors the declaration structure of Backend/Emitter.cs
/// (static Program class, Alice classes/structs/records as plain types, auto properties, constructors, sum-type
/// variants as sealed nested classes) and emits each post-optimizer MIR body through <see cref="IlBody"/>.
/// Forks lower to serial code (<c>Lowerer.Sequential</c>, ADR 0024): their helpers are C# text today.
/// </summary>
public sealed partial class AirToIl
{
    internal IlModule Module = null!;
    internal IlTypes Types = null!;
    internal readonly IlStats Stats = new();
    internal AliceT ProgramT = null!;
    internal string File = "";
    /// <summary>Source lines per file path: sequence point columns and lengths (review-r1 F19).</summary>
    internal readonly Dictionary<string, string[]> SourceLines = [];
    internal readonly List<AMethod> Extensions = [];
    readonly List<IlUnsupportedException> errors = [];
    Binder binder = null!;
    Lowerer lowerer = null!;
    HotPolicy hot = null!;
    readonly List<(AliceT Owner, List<(MExpr? Value, AField F, string File)> Inits)> staticInits = [];
    readonly Dictionary<AliceT, List<(Expr Init, AField F, string File)>> instanceInits = new();

    AirToIl() { }

    /// <summary>Whether the C# backend's [AggressiveOptimization] policy was found (reflection on Emitter; a test guards it).</summary>
    public static bool HotPolicyAvailable => HotPolicy.Available;

    /// <summary>Compiles Alice sources to an IL module (an executable when it has top-level statements and <paramref name="exe"/>).</summary>
    public static IlCompileResult Compile(IReadOnlyList<SourceFile> files, string name, bool exe = true, IEnumerable<string>? refs = null)
    {
        var c = new AirToIl();
        return c.Run(files, name, exe, refs);
    }

    IlCompileResult Run(IReadOnlyList<SourceFile> files, string name, bool exe, IEnumerable<string>? refs)
    {
        foreach (var f in files) SourceLines[f.Path] = f.Text.Split('\n');
        var asts = files.Select(f =>
        {
            var ast = Parser.ParseFile(f.Text, f.Path);
            if (f.LibraryOnly) ast.Items.RemoveAll(i => i is StmtItem);
            return ast;
        }).ToList();
        binder = new Binder(asts, new ClrTypes(refs));
        binder.Bind();
        lowerer = new Lowerer(binder) { Sequential = true };
        Module = new IlModule(name);
        Types = new IlTypes(binder, Module);
        hot = new HotPolicy(binder);
        foreach (var f in files) Module.AddDocument(f.Path, f.Text);

        IlMethodDef? main = null;
        try
        {
            DeclareTypes(asts);
            if (SynthOn) SynthDeclareMembers(asts); else DeclareMembers(asts); // S2 il-emit2
            main = EmitBodies(asts, exe);
            EmitDynSites(); // H4b il-emit2
        }
        catch (IlUnsupportedException e) { Fail(e); }
        if (errors.Count > 0) return new IlCompileResult(null, null, Stats, errors);
        Module.EntryPoint = main;
        try
        {
            var (pe, pdb) = Module.Emit();
            // every body must decode cleanly (the old short-branch corruption is fixed in IlBuilder.Encode; a hit here is a bug)
            if (CorruptBodies(pe) is { Count: > 0 } bad)
                throw new InvalidOperationException("corrupt IL in MethodDef rows " + string.Join(", ", bad));
            return new IlCompileResult(pe, pdb, Stats, errors) { Module = Module };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or BadImageFormatException)
        {
            Fail(new IlUnsupportedException("Encode:" + e.GetType().Name, null, e.Message));
            return new IlCompileResult(null, null, Stats, errors);
        }
    }

    static readonly Dictionary<short, System.Reflection.Emit.OpCode> Ops =
        typeof(System.Reflection.Emit.OpCodes).GetFields().Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>MethodDef rows (1-based) whose serialized IL does not decode cleanly: an unknown opcode, a branch target
    /// off an instruction boundary, or the last instruction not ending at the body end.</summary>
    internal static List<int> CorruptBodies(byte[] image)
    {
        var bad = new List<int>();
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image));
        var md = pe.GetMetadataReader();
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            if (m.RelativeVirtualAddress == 0) continue;
            var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
            var starts = new HashSet<int>();
            var targets = new List<int>();
            int i = 0;
            bool ok = true;
            while (i < il.Length && ok)
            {
                starts.Add(i);
                short v = il[i] == 0xFE && i + 1 < il.Length ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
                i += il[i] == 0xFE ? 2 : 1;
                if (!Ops.TryGetValue(v, out var op)) { ok = false; break; }
                switch (op.OperandType)
                {
                    case System.Reflection.Emit.OperandType.InlineNone: break;
                    case System.Reflection.Emit.OperandType.ShortInlineBrTarget: if (i >= il.Length) { ok = false; break; } targets.Add(i + 1 + (sbyte)il[i]); i += 1; break;
                    case System.Reflection.Emit.OperandType.InlineBrTarget: if (i + 4 > il.Length) { ok = false; break; } targets.Add(i + 4 + BitConverter.ToInt32(il, i)); i += 4; break;
                    case System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar: i += 1; break;
                    case System.Reflection.Emit.OperandType.InlineVar: i += 2; break;
                    case System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR: i += 8; break;
                    case System.Reflection.Emit.OperandType.InlineSwitch:
                        {
                            if (i + 4 > il.Length) { ok = false; break; }
                            int n = BitConverter.ToInt32(il, i);
                            int at = i + 4 + 4 * n;
                            for (int k = 0; k < n && i + 8 + 4 * k <= il.Length; k++) targets.Add(at + BitConverter.ToInt32(il, i + 4 + 4 * k));
                            i = at;
                            break;
                        }
                    default: i += 4; break;
                }
            }
            if (!ok || i != il.Length || targets.Any(t => !starts.Contains(t))) bad.Add(System.Reflection.Metadata.Ecma335.MetadataTokens.GetRowNumber(h));
        }
        return bad;
    }

    void Fail(IlUnsupportedException e)
    {
        errors.Add(e);
        Stats.Blocked(e.NodeKind);
    }

    // ------------------------------------------------------------------ types

    const TypeAttributes StaticClass = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;

    readonly List<(Item Decl, AliceT T)> decls = [];

    static IEnumerable<(Item Item, string Ns, Item? Outer)> AllTypeItems(IEnumerable<Item> items, string ns, Item? outer)
    {
        foreach (var it in items)
        {
            switch (it)
            {
                case NamespaceItem n:
                    foreach (var x in AllTypeItems(n.Items, ns.Length > 0 ? ns + "." + n.Name : n.Name, null)) yield return x;
                    break;
                case TypeDecl td:
                    yield return (td, ns, outer);
                    foreach (var x in AllTypeItems(td.Members, ns, td)) yield return x;
                    break;
                case EnumDecl or LibDecl or DelegateDecl:
                    yield return (it, ns, outer);
                    break;
            }
        }
    }

    void DeclareTypes(List<FileNode> asts)
    {
        ProgramT = new AliceT { Sym = binder.Program, IsStatic = true };
        ProgramT.Def = Module.DefineType("", "Program", StaticClass);
        ProgramT.Proxy = Types.MakeProxy(ProgramT, null, null);
        Types.BySym[binder.Program] = ProgramT;
        Types.ByCs["Program"] = ProgramT;

        var all = asts.SelectMany(a => AllTypeItems(a.Items, "", null)).ToList();
        foreach (var u in asts.SelectMany(a => a.Items.OfType<UsingItem>()))
            if (!binder.IsTypeName(u.Name) && !Types.Usings.Contains(u.Name)) Types.Usings.Add(u.Name);
        var byItem = new Dictionary<Item, AliceT>();
        // one AliceT per symbol (a reopened class merges its declarations)
        foreach (var (it, ns, outer) in all)
        {
            switch (it)
            {
                case LibDecl: continue; // H5 il-emit2: DeclareLibs below
                case DelegateDecl: throw new IlUnsupportedException("Decl:delegate", Sp(it));
                case TypeDecl { RefineScope: not null }: throw new IlUnsupportedException("Decl:refinement", Sp(it));
            }
            var sym = (TypeSym)it.Sym!;
            if (Types.BySym.TryGetValue(sym, out var existing)) { byItem[it] = existing; decls.Add((it, existing)); continue; }
            var a = new AliceT { Sym = sym };
            if (it is TypeDecl td)
            {
                if (sym.TypeParams.Count > 0) throw new IlUnsupportedException("Decl:generic-type", Sp(it), sym.Name);
                if (sym.SoaStore != null || sym.SoaOf != null) throw new IlUnsupportedException("Decl:soa", Sp(it), sym.Name);
                a.IsValueType = td.Kind is "struct" or "record struct";
                a.IsInterface = td.Kind == "interface";
                a.IsStatic = td.Kind is "module" || sym.Kind == "extension";
                a.IsRecord = td.Kind.StartsWith("record");
                if (sym.IsStack) throw new IlUnsupportedException("Decl:ref-struct", Sp(it), sym.Name);
            }
            else if (it is EnumDecl ed)
            {
                if (ed.IsAdt) a.IsAdt = true;
                else
                {
                    a.IsEnum = true;
                    a.EnumUnderlying = ed.Underlying != null ? Types.FromAType(binder.ResolveType(ed.Underlying)) : typeof(int);
                }
            }
            Types.BySym[sym] = a;
            Types.ByCs[sym.Type.Cs] = a;
            byItem[it] = a;
            decls.Add((it, a));
            if (it is EnumDecl { IsAdt: true } adt)
                foreach (var vs in sym.Variants)
                {
                    var va = new AliceT { Sym = vs, IsVariant = true, IsRecord = true, Base = a };
                    Types.BySym[vs] = va;
                    Types.ByCs[vs.Type.Cs] = va;
                }
        }
        // proxies (bases first) and TypeDefs
        var made = new HashSet<AliceT>();
        var outers = all.ToDictionary(x => x.Item, x => (x.Ns, x.Outer));
        var first = new Dictionary<AliceT, Item>();
        foreach (var (it, a) in decls) first.TryAdd(a, it);
        void MakeOf(AliceT a)
        {
            var it = first[a];
            var (ns, outer) = outers[it];
            Make(a, it, ns, outer != null && byItem.TryGetValue(outer, out var oa) ? oa : null);
        }
        void Make(AliceT a, Item? it, string ns, AliceT? outer)
        {
            if (made.Contains(a)) return;
            if (outer != null) MakeOf(outer);
            if (a.Sym.BaseClass is { } bc && Types.BySym.TryGetValue(bc, out var ba)) { MakeOf(ba); a.Base = ba; }
            if (!made.Add(a)) return;
            foreach (var b in a.Sym.Bases)
                if (b.Sym == null && b.Clr is { IsClass: true } clr && clr != typeof(object)) a.ClrBase = clr;
            List<(string, object)>? enumValues = null;
            if (it is EnumDecl { IsAdt: false } ed) enumValues = EnumValues(ed, a.EnumUnderlying!);
            a.Proxy = Types.MakeProxy(a, a.Base?.Proxy ?? a.ClrBase, enumValues);
            var attrs = TypeAttrs(a, it, outer != null);
            string nm = a.IsVariant ? a.Sym.Name : a.Sym.Name;
            a.Def = outer != null ? outer.Def.DefineNestedType(nm, attrs) : Module.DefineType(ns, nm, attrs);
            if (it is EnumDecl { IsAdt: true })
                foreach (var vs in a.Sym.Variants)
                {
                    var va = Types.BySym[vs];
                    made.Add(va);
                    va.Proxy = Types.MakeProxy(va, a.Proxy, null);
                    va.Def = a.Def.DefineNestedType(vs.Name, TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
                }
            if (enumValues != null)
            {
                a.Def.BaseType = Module.CoreType("Enum");
                a.Def.DefineField("value__", Types.Il(a.EnumUnderlying!), FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName);
                foreach (var (n, v) in enumValues)
                {
                    var f = a.Def.DefineField(n, a.Def.Type, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal, v);
                    a.Fields[n] = new AField { Name = n, Type = a.Proxy, IsStatic = true, Owner = a, Literal = v };
                    _ = f;
                }
            }
        }
        foreach (var (_, a) in decls) MakeOf(a);
        foreach (var a in made)
        {
            if (a.IsEnum || a.IsInterface) continue;
            if (a.IsValueType) a.Def.BaseType = Module.CoreType("ValueType");
            else if (a.Base != null) a.Def.BaseType = a.Base.Il;
            else if (a.ClrBase != null) a.Def.BaseType = Types.Il(a.ClrBase);
            foreach (var b in a.Sym.Bases)
            {
                if (b.Sym != null && Types.BySym.TryGetValue(b.Sym, out var bi) && bi.IsInterface) a.Def.Interfaces.Add(bi.Il);
                else if (b.Sym == null && b.Clr is { IsInterface: true }) a.Def.Interfaces.Add(Types.Il(Types.FromAType(b)));
            }
            if (!a.IsStatic && !a.IsVariant && !a.IsAdt) a.Def.AddCustomAttribute(Module.Import(typeof(Alice.Runtime.AliceTypeAttribute).GetConstructor(Type.EmptyTypes)!));
        }
        DeclareLibs(all); // H5 il-emit2
    }

    static Span? Sp(Node n) => n.Line > 0 ? new Span("", n.Line, Math.Max(1, n.Col), 0) : null;

    TypeAttributes TypeAttrs(AliceT a, Item? it, bool nested)
    {
        var vis = nested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
        if (a.IsEnum) return vis | TypeAttributes.Sealed;
        if (a.IsInterface) return vis | TypeAttributes.Interface | TypeAttributes.Abstract;
        if (a.IsStatic) return vis | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
        if (a.IsValueType) return vis | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit;
        if (a.IsAdt) return vis | TypeAttributes.Abstract | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
        var td = it as TypeDecl;
        bool open = a.Sym.HasSubclass || td?.IsAbstract == true || td?.Attrs.Contains("Open") == true;
        return vis | TypeAttributes.Class | TypeAttributes.BeforeFieldInit | (td?.IsAbstract == true ? TypeAttributes.Abstract : 0) | (open ? 0 : TypeAttributes.Sealed);
    }

    List<(string, object)> EnumValues(EnumDecl e, Type under)
    {
        var r = new List<(string, object)>();
        long next = 0;
        for (int i = 0; i < e.Names.Count; i++)
        {
            long v = next;
            if (e.Values[i] is { } ex)
            {
                var me = Init(ex, binder.Program);
                if (me is AirConst { Value: int iv }) v = iv;
                else if (me is AirConst { Value: long lv }) v = lv;
                else if (me is MCode mc && IlTypes.TryLiteral(mc.Text, out _, out var lit) && lit is int or long or uint or ulong) v = System.Convert.ToInt64(lit);
                else throw new IlUnsupportedException("Decl:enum-value", Sp(e), e.Names[i]);
            }
            r.Add((e.Names[i], System.Convert.ChangeType(v, under)));
            next = v + 1;
        }
        return r;
    }

    // ------------------------------------------------------------------ members

    void DeclareMembers(List<FileNode> asts)
    {
        foreach (var (it, a) in decls)
        {
            File = FileOf(asts, it);
            switch (it)
            {
                case TypeDecl td: TypeMembers(td, a); break;
                case EnumDecl { IsAdt: true } ed: Variants(ed, a); break;
            }
        }
        foreach (var ast in asts)
        {
            File = ast.Path;
            foreach (var it in ast.Items.Concat(ast.Items.OfType<NamespaceItem>().SelectMany(n => n.Items)))
                switch (it)
                {
                    case MethodDecl m: DeclareMethod(m, ProgramT); break;
                    case ConstDecl k: Const(k, ProgramT); break;
                }
        }
        foreach (var a in decls.Select(d => d.T).Distinct()) AutoMembers(a);
    }

    static string FileOf(List<FileNode> asts, Item it)
    {
        foreach (var a in asts) if (Contains(a.Items, it)) return a.Path;
        return asts[0].Path;
    }

    static bool Contains(List<Item> items, Item it) => items.Any(x => x == it || x is NamespaceItem n && Contains(n.Items, it) || x is TypeDecl t && Contains(t.Members, it));

    void TypeMembers(TypeDecl d, AliceT a)
    {
        var s = a.Sym;
        if (d.Members.Any(m => m is MethodDecl { Name: "__class_body" })) throw new IlUnsupportedException("Decl:class-body", Sp(d), s.Name);
        if (s.EnumerableElem != null) throw new IlUnsupportedException("Decl:enumerable-bridge", Sp(d), s.Name);
        // positional record parameters: properties + the primary constructor
        if (d.RecordParams.Count > 0)
        {
            foreach (var fn in s.FieldOrder) Property(a, s.Fields[fn], getterOnly: true);
            var ps = s.FieldOrder.Select(fn => a.Fields[Bare(s.Fields[fn].CsName)]).ToList();
            var ctor = a.Def.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, [.. ps.Select(f => Types.Il(f.Type))]);
            for (int i = 0; i < ps.Count; i++) ctor.DefineParameter(i + 1, ps[i].Name);
            var am = new AMethod { Name = ".ctor", Owner = a, IsCtor = true, Params = [.. ps.Select(f => f.Type)], ParamNames = [.. ps.Select(f => f.Name)], Def = ctor };
            a.Ctors.Add(am);
            recordCtors[am] = ps;
        }
        foreach (var it in d.Members)
        {
            switch (it)
            {
                case AccessorDecl acc:
                    {
                        var f = (FieldSym)acc.Sym!;
                        if (acc.Kind == "event") throw new IlUnsupportedException("Decl:event", Sp(acc), f.Name);
                        if (a.IsInterface) throw new IlUnsupportedException("Decl:interface-property", Sp(acc), f.Name);
                        var af = Property(a, f, getterOnly: false);
                        if (acc.Init != null) AddInit(a, af, acc.Init);
                        break;
                    }
                case FieldDecl fd:
                    {
                        var fs = (FieldSym)fd.Sym!;
                        if (fd.Attrs.Count > 0) throw new IlUnsupportedException("Decl:field-attribute", Sp(fd), string.Join(",", fd.Attrs));
                        var af = Field(a, fs, FieldAttributes.Private);
                        if (fd.Init != null) AddInit(a, af, fd.Init);
                        break;
                    }
                case ConstDecl k: Const(k, a); break;
                case MethodDecl { IsTemplate: true }: break;
                case MethodDecl m: DeclareMethod(m, a); break;
            }
        }
    }

    readonly Dictionary<AMethod, List<AField>> recordCtors = new();

    /// <summary>Ruby-style inferred fields, `initialize(@x)` auto fields, default constructors, derived operators, records' ToString.</summary>
    void AutoMembers(AliceT a)
    {
        if (a == ProgramT || a.IsEnum || a.IsInterface || a.IsAdt && !a.IsVariant) { if (a.IsAdt) AdtCtor(a); return; }
        foreach (var f in a.Sym.Fields.Values)
            if (f.Kind is "inferred" or "inferred-emitted" or "auto" or "auto-emitted" && !a.Fields.ContainsKey(Bare(f.CsName)))
                Field(a, f, FieldAttributes.Private);
        if (a.IsStatic) return;
        bool hasInit = instanceInits.ContainsKey(a);
        if (a.Ctors.Count == 0 && (!a.IsValueType || hasInit))
        {
            var ctor = a.Def.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig);
            a.Ctors.Add(new AMethod { Name = ".ctor", Owner = a, IsCtor = true, Def = ctor, Params = [], ParamNames = [] });
        }
        if (a.IsRecord && !a.Methods.Any(m => m.Name == "ToString" && m.Params.Length == 0)) RecordToString(a);
        // `==` without `!=` (and `<` without `>`...): the derived operator, as Emitter.Type does
        var ops = a.Methods.Where(m => m.IsStatic).Select(m => m.Name).ToHashSet();
        void Derive(string have, string want, bool swap, bool negate)
        {
            if (!ops.Contains(have) || ops.Contains(want)) return;
            var src = a.Methods.First(m => m.Name == have && m.IsStatic);
            var md = a.Def.DefineMethod(want, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName, IlType.Bool, [Types.Il(a.Proxy), Types.Il(a.Proxy)]);
            var il = Body(md);
            il.EmitLoadArg(swap ? 1 : 0);
            il.EmitLoadArg(swap ? 0 : 1);
            il.EmitCall(src.Def);
            if (negate) { il.EmitLoadConst(0); il.Emit(ILOpCode.Ceq); }
            il.EmitReturn();
            a.Methods.Add(new AMethod { Name = want, Owner = a, IsStatic = true, Ret = typeof(bool), Params = [a.Proxy, a.Proxy], ParamNames = ["a", "b"], Def = md });
        }
        Derive("op_Equality", "op_Inequality", false, true);
        Derive("op_LessThan", "op_GreaterThan", true, false);
        Derive("op_LessThanOrEqual", "op_GreaterThanOrEqual", true, false);
    }

    void AdtCtor(AliceT a)
    {
        var ctor = a.Def.DefineConstructor(MethodAttributes.Private | MethodAttributes.HideBySig);
        var am = new AMethod { Name = ".ctor", Owner = a, IsCtor = true, Def = ctor };
        var il = Body(ctor);
        il.EmitLoadArg(0);
        il.EmitCall(Module.Import(typeof(object).GetConstructor(Type.EmptyTypes)!));
        il.EmitReturn();
        a.Ctors.Add(am);
    }

    /// <summary>C# record ToString: `Name { a = 1, b = x }` (members through Prelude-free string conversion).</summary>
    void RecordToString(AliceT a)
    {
        var md = a.Def.DefineMethod("ToString", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, IlType.String);
        var il = Body(md);
        var sb = typeof(System.Text.StringBuilder);
        var append = sb.GetMethod("Append", [typeof(string)])!;
        var appendObj = sb.GetMethod("Append", [typeof(object)])!;
        il.EmitNewObj(Module.Import(sb.GetConstructor(Type.EmptyTypes)!));
        var fields = a.Sym.FieldOrder.Select(fn => a.Fields[Bare(a.Sym.Fields[fn].CsName)]).ToList();
        string head = a.IsVariant ? a.Sym.Name : a.Sym.Name;
        il.EmitLoadConst(head + (fields.Count > 0 ? " { " : " { }"));
        il.EmitCall(Module.Import(append));
        for (int i = 0; i < fields.Count; i++)
        {
            il.EmitLoadConst((i > 0 ? ", " : "") + fields[i].Name + " = ");
            il.EmitCall(Module.Import(append));
            il.EmitLoadArg(0);
            if (a.IsValueType) { }
            il.EmitLoadField(fields[i].Field!);
            var ft = fields[i].Type;
            if (ft == typeof(string)) il.EmitCall(Module.Import(append));
            else
            {
                if (ft.IsValueType) il.EmitBox(Types.Il(ft));
                il.EmitCall(Module.Import(appendObj));
            }
        }
        if (fields.Count > 0) { il.EmitLoadConst(" }"); il.EmitCall(Module.Import(append)); }
        il.EmitCall(Module.Import(typeof(object).GetMethod("ToString")!), virtualCall: true);
        il.EmitReturn();
        a.Methods.Add(new AMethod { Name = "ToString", Owner = a, Ret = typeof(string), Def = md, IsVirtual = true });
    }

    void Variants(EnumDecl e, AliceT adt)
    {
        foreach (var vs in adt.Sym.Variants)
        {
            var va = Types.BySym[vs];
            va.Def.BaseType = adt.Il;
            var ps = new List<AField>();
            foreach (var fn in vs.FieldOrder) ps.Add(Property(va, vs.Fields[fn], getterOnly: true));
            var ctor = va.Def.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, [.. ps.Select(f => Types.Il(f.Type))]);
            for (int i = 0; i < ps.Count; i++) ctor.DefineParameter(i + 1, ps[i].Name);
            var am = new AMethod { Name = ".ctor", Owner = va, IsCtor = true, Params = [.. ps.Select(f => f.Type)], ParamNames = [.. ps.Select(f => f.Name)], Def = ctor };
            va.Ctors.Add(am);
            recordCtors[am] = ps;
            if (ps.Count == 0)
            {
                // unit variant: `public static readonly V Value = new();`
                var fd = va.Def.DefineField("Value", va.Il, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
                var vf = new AField { Name = "Value", Type = va.Proxy, IsStatic = true, Owner = va, Field = fd, ReadOnly = true };
                va.Fields["Value"] = vf;
                AddStaticInit(va, vf, new MNew { TypeCs = vs.Type.Cs });
            }
            RecordToString(va);
        }
    }

    static string Bare(string n) => n.StartsWith('@') ? n[1..] : n;

    AField Field(AliceT a, FieldSym fs, FieldAttributes vis)
    {
        string n = Bare(fs.CsName);
        var t = Types.FromAType(fs.Type);
        var attrs = vis | (fs.IsStatic || a.IsStatic ? FieldAttributes.Static : 0);
        var fd = a.Def.DefineField(n, Types.Il(t), attrs);
        return a.Fields[n] = new AField { Name = n, Type = t, IsStatic = fs.IsStatic || a.IsStatic, Owner = a, Field = fd };
    }

    /// <summary>An auto property: private backing field plus get_/set_ accessors.</summary>
    AField Property(AliceT a, FieldSym fs, bool getterOnly)
    {
        string n = Bare(fs.CsName);
        var t = Types.FromAType(fs.Type);
        bool st = fs.IsStatic || a.IsStatic;
        var ft = Types.Il(t);
        var backing = a.Def.DefineField("<" + n + ">k__BackingField", ft, FieldAttributes.Private | (st ? FieldAttributes.Static : 0) | (getterOnly && a.IsValueType && a.IsRecord ? FieldAttributes.InitOnly : 0));
        var acc = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | (st ? MethodAttributes.Static : 0);
        var get = a.Def.DefineMethod("get_" + n, acc, ft);
        get.ImplAttributes |= MethodImplAttributes.AggressiveInlining;
        var g = Body(get);
        if (!st) g.EmitLoadArg(0);
        g.EmitLoadField(backing);
        g.EmitReturn();
        IlMethodDef? set = null;
        if (!getterOnly)
        {
            set = a.Def.DefineMethod("set_" + n, acc, IlType.Void, [ft]);
            set.DefineParameter(1, "value");
            var sIl = Body(set);
            if (!st) { sIl.EmitLoadArg(0); sIl.EmitLoadArg(1); } else sIl.EmitLoadArg(0);
            sIl.EmitStoreField(backing);
            sIl.EmitReturn();
        }
        a.Def.DefineProperty(n, ft, get, set);
        var af = new AField { Name = n, Type = t, IsStatic = st, Owner = a, Field = backing, Getter = get, Setter = set };
        // inside the type (and its constructors) the backing field is written directly
        return a.Fields[n] = af;
    }

    IlBuilder Body(IlMethodDef m)
    {
        var il = m.Il;
        il.InitLocals = false;
        il.Peephole = PeepholeOn ? IlPeephole.All : IlPeephole.None;
        return il;
    }

    void Const(ConstDecl k, AliceT a)
    {
        var f = (FieldSym)k.Sym!;
        var t = Types.FromAType(f.Type);
        var fd = a.Def.DefineField(k.Name, Types.Il(t), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
        var af = new AField { Name = k.Name, Type = t, IsStatic = true, Owner = a, Field = fd, ReadOnly = true };
        a.Fields[k.Name] = af;
        AddStaticInit(a, af, Init(k.Value, a.Sym), file: File);
    }

    void AddInit(AliceT a, AField f, Expr init)
    {
        if (f.IsStatic) { AddStaticInit(a, f, Init(init, a.Sym)); return; }
        if (!instanceInits.TryGetValue(a, out var l)) instanceInits[a] = l = [];
        l.Add((init, f, File));
    }

    void AddStaticInit(AliceT a, AField f, MExpr? value, string? file = null)
    {
        var e = staticInits.FirstOrDefault(x => x.Owner == a);
        if (e.Owner == null) { e = (a, []); staticInits.Add(e); }
        e.Inits.Add((value, f, file ?? File));
    }

    /// <summary>A field initializer or default value lowered as the C# backend does (Emitter.Init).</summary>
    MExpr Init(Expr e, TypeSym owner)
    {
        lowerer.Type = owner;
        lowerer.Method = null;
        lowerer.File = File;
        var tmp = lowerer.LowerBody([new ExprStmt { Expr = e, Line = e.Line }], Tail.Return, [], e.Line);
        Optimizer.Pipeline.Run(tmp);
        DynLower(tmp); // H4a il-emit2
        if (tmp.Count != 1) throw new IlUnsupportedException("Init:statements", Sp(e));
        CheckLowerer(Sp(e));
        return ((MReturn)tmp[^1]).Value!;
    }

    /// <summary>`ALICE_IL_PEEPHOLE=0`: no peephole passes (A/B of IlBuilder's rewrites).</summary>
    static readonly bool PeepholeOn = Environment.GetEnvironmentVariable("ALICE_IL_PEEPHOLE") != "0";

    static readonly bool IlLoweringOn = Environment.GetEnvironmentVariable("ALICE_IL_LOWER") != "0";

    /// <summary>The IL-only lowering passes (Air/Passes/IlLowering, D1.7) after the MIR pipeline. What they leave
    /// (ctx.Unsupported) is emitted natively here or reported by the emitter itself.</summary>
    void IlLower(List<MStmt> body)
    {
        if (!IlLoweringOn) return;
        var ctx = Air.Passes.IlLowering.Run(body);
        foreach (var u in ctx.Unsupported) Stats.Seen("IlLowering.left:" + u.Split(':')[0]);
    }

    void CheckLowerer(Span? sp)
    {
        if (lowerer.Extra.Count > 0)
        {
            lowerer.Extra.Clear();
            throw new IlUnsupportedException("Lowerer.Extra (C# helper text)", sp);
        }
        if (lowerer.FlushSpecializations().Length > 0) throw new IlUnsupportedException("Lowerer.Specialization (C# text)", sp);
        if (lowerer.UsesGpu) throw new IlUnsupportedException("Lowerer.Gpu", sp);
    }

    // ------------------------------------------------------------------ methods

    static readonly Dictionary<string, string> AliceOps = new()
    {
        ["+"] = "op_Add", ["-"] = "op_Sub", ["*"] = "op_Mul", ["/"] = "op_Div", ["%"] = "op_Mod", ["=="] = "op_Eq", ["<=>"] = "op_Cmp",
        ["!="] = "op_Ne", ["<"] = "op_Lt", [">"] = "op_Gt", ["<="] = "op_Le", [">="] = "op_Ge", ["<<"] = "op_Shl", [">>"] = "op_Shr",
        ["&"] = "op_BitAnd", ["|"] = "op_BitOr", ["^"] = "op_Xor", ["~"] = "op_Inv", ["==="] = "op_CaseEq", ["[]="] = "op_IndexSet",
    };

    static readonly Dictionary<string, string> ClrOps = new()
    {
        ["+"] = "op_Addition", ["-"] = "op_Subtraction", ["*"] = "op_Multiply", ["/"] = "op_Division", ["%"] = "op_Modulus",
        ["=="] = "op_Equality", ["!="] = "op_Inequality", ["<"] = "op_LessThan", [">"] = "op_GreaterThan", ["<="] = "op_LessThanOrEqual",
        [">="] = "op_GreaterThanOrEqual", ["<<"] = "op_LeftShift", [">>"] = "op_RightShift", ["&"] = "op_BitwiseAnd", ["|"] = "op_BitwiseOr",
        ["^"] = "op_ExclusiveOr", ["~"] = "op_OnesComplement",
    };

    static readonly Dictionary<string, string> UnaryOps = new() { ["-"] = "op_UnaryNegation", ["+"] = "op_UnaryPlus", ["~"] = "op_OnesComplement" };
    static readonly Dictionary<string, string> AliceUnary = new() { ["-"] = "op_Neg", ["+"] = "op_Plus", ["~"] = "op_Inv" };

    readonly List<AMethod> toEmit = [];

    void DeclareMethod(MethodDecl d, AliceT a)
    {
        var m = (MethodSym)d.Sym!;
        if (d.IsPartial) throw new IlUnsupportedException("Decl:partial-method (source generator)", Sp(d), m.Name);
        if (m.TypeParams.Count > 0 && !m.HasInferredGenerics) throw new IlUnsupportedException("Decl:generic-method", Sp(d), m.Name);
        if (d.Attrs.Any(x => x is not ("Inline" or "NoInline" or "Hot" or "StrictMath" or "Native" or "Open" or "Stack") && !x.StartsWith("Native(") && !x.StartsWith("__enumerable:")))
            throw new IlUnsupportedException("Decl:attribute", Sp(d), string.Join(",", d.Attrs));
        if (m.Params.Any(p => p.Block || p.Mod != "")) throw new IlUnsupportedException("Decl:param-kind", Sp(d), m.Name);
        if (!m.HasInferredGenerics || !d.HasBody) { DeclareVariant(d, a, m, null); return; }
        int n = 0;
        foreach (var inst in m.Instances.Values.Where(i => i.Seen)) { DeclareVariant(d, a, m, inst); n++; }
        if (m.NeedsGeneric || n == 0) throw new IlUnsupportedException("Decl:generic-method", Sp(d), m.Name);
    }

    void DeclareVariant(MethodDecl d, AliceT a, MethodSym m, Instance? inst)
    {
        var sp = Sp(d);
        var retA = inst?.Ret ?? m.Ret;
        if (inst != null && retA.IsUnknown) throw new IlUnsupportedException("Decl:dynamic-return", sp, m.Name);
        var retT = m.IsCtor ? typeof(void) : SmReturn(m, inst) ?? Types.FromAType(retA, sp); // H3a il-emit2
        var ps = new List<Type>();
        var names = new List<string>();
        bool ext = a.Sym.Kind == "extension";
        if (ext)
        {
            ps.Add(Types.FromAType(a.Sym.ExtTarget!, sp));
            names.Add("self");
        }
        for (int i = 0; i < m.Params.Count; i++)
        {
            var pt = Types.FromAType(inst != null ? inst.Args[i] : m.Params[i].Type, sp);
            ps.Add(m.Params[i].Splat ? pt.MakeArrayType() : pt);
            names.Add(Bare(m.Params[i].CsName));
        }
        bool isStatic = m.IsStatic || a.IsStatic || ext;
        string name = m.CsName;
        bool special = false;
        bool isToString = d.Name == "to_s" && m.Params.Count == 0;
        if (m.IsCtor) name = ".ctor";
        else if (isToString) name = "ToString";
        else if (d.Name == "[]") { name = "get_Item"; special = true; }
        else if (d.Name == "[]=") { name = "set_Item"; special = true; } // C#: the indexer setter (review-r1 F7 follow-up)
        else if (AliceOps.TryGetValue(d.Name, out var on)) name = m.Params.Count == 0 && AliceUnary.TryGetValue(d.Name, out var un) ? un : on;
        var (vis, virt) = Virtual(m, a, d.Vis);
        var attrs = (vis == "private" ? MethodAttributes.Private : vis == "protected" ? MethodAttributes.Family : MethodAttributes.Public) | MethodAttributes.HideBySig;
        if (isStatic) attrs |= MethodAttributes.Static;
        if (isToString) attrs = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual;
        else if (virt == " override") attrs |= MethodAttributes.Virtual;
        else if (virt == " virtual") attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        if (special) attrs |= MethodAttributes.SpecialName;
        bool abstractM = d.IsAbstract || a.IsInterface && !d.HasBody;
        if (abstractM) attrs |= MethodAttributes.Abstract | MethodAttributes.Virtual | (virt == " override" ? 0 : MethodAttributes.NewSlot);
        if (!isStatic && !m.IsCtor && ImplementsInterface(a, name, ps.Count)) attrs |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
        if (a.IsInterface && d.HasBody) throw new IlUnsupportedException("Decl:default-interface-method", sp, m.Name);
        var ilps = ps.Select(Types.Il).ToArray();
        var md = m.IsCtor
            ? a.Def.DefineConstructor((vis == "private" ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.HideBySig, ilps)
            : a.Def.DefineMethod(name, attrs, Types.Il(retT), ilps);
        for (int i = 0; i < names.Count; i++)
        {
            var p = md.DefineParameter(i + 1, names[i]);
            if (ext ? i > 0 && m.Params[i - 1].Splat : m.Params[i].Splat)
                p.CustomAttributes.Add(new IlCustomAttribute(Module.Import(typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes)!), []));
        }
        if (d.Attrs.Contains("Inline")) md.ImplAttributes |= MethodImplAttributes.AggressiveInlining;
        if (d.Attrs.Contains("NoInline")) md.ImplAttributes |= MethodImplAttributes.NoInlining;
        if (d.Attrs.Contains("Hot")) md.ImplAttributes |= MethodImplAttributes.AggressiveOptimization;
        int k0 = ext ? 1 : 0;
        var defaults = new object?[ps.Count];
        var hasDefault = new bool[ps.Count];
        for (int i = 0; i < m.Params.Count; i++)
            if (d.Params[i].Default is { } dv) { hasDefault[i + k0] = true; defaults[i + k0] = Init(dv, a.Sym); }
        var am = new AMethod
        {
            Name = name, Owner = a, Ret = retT, Params = [.. ps], ParamNames = [.. names], IsStatic = isStatic, IsCtor = m.IsCtor,
            IsVirtual = (attrs & MethodAttributes.Virtual) != 0, IsAbstract = abstractM, IsExtension = ext, Def = md, Sym = m, Inst = inst, Decl = d,
            Defaults = defaults, HasDefault = hasDefault, ParamsArray = m.Params.Count > 0 && m.Params[^1].Splat,
        };
        if (m.IsCtor) a.Ctors.Add(am); else a.Methods.Add(am);
        if (ext) Extensions.Add(am);
        if (!abstractM && d.HasBody) toEmit.Add(am);
        // operators: `public static R operator +(Self self, P p) => self.op_Add(p);`
        if (AliceOps.ContainsKey(d.Name) && d.Name is not ("<=>" or "===" or "[]="))
        {
            bool unary = m.Params.Count == 0 && UnaryOps.ContainsKey(d.Name);
            string opn = unary ? UnaryOps[d.Name] : ClrOps.GetValueOrDefault(d.Name) ?? "";
            if (opn.Length > 0 && (unary || m.Params.Count == 1))
            {
                var opPs = ps.Prepend(a.Proxy).ToArray();
                var od = a.Def.DefineMethod(opn, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName, Types.Il(retT), [.. opPs.Select(Types.Il)]);
                var oil = Body(od);
                if (a.IsValueType) oil.EmitLoadArgAddress(0); else oil.EmitLoadArg(0);
                for (int i = 0; i < ps.Count; i++) oil.EmitLoadArg(i + 1);
                oil.EmitCall(md, virtualCall: !a.IsValueType);
                oil.EmitReturn();
                a.Methods.Add(new AMethod { Name = opn, Owner = a, Ret = retT, Params = opPs, ParamNames = [.. names.Prepend("self")], IsStatic = true, Def = od });
            }
        }
        if (special)
        {
            a.Def.DefineProperty("Item", Types.Il(retT), md, null);
            a.Def.CustomAttributes.Add(new IlCustomAttribute(Module.Import(typeof(DefaultMemberAttribute).GetConstructor([typeof(string)])!), ["Item"]));
        }
    }

    bool ImplementsInterface(AliceT a, string name, int argc)
    {
        foreach (var b in a.Sym.Bases)
        {
            if (b.Sym != null && Types.BySym.TryGetValue(b.Sym, out var bi) && bi.IsInterface && bi.Sym.Methods.Values.SelectMany(x => x).Any(x => x.CsName == name && x.Params.Count == argc)) return true;
            if (b.Sym == null && b.Clr is { IsInterface: true } ci && ci.GetMethods().Concat(ci.GetInterfaces().SelectMany(i => i.GetMethods())).Any(x => x.Name == name && x.GetParameters().Length == argc)) return true;
        }
        return false;
    }

    /// <summary>Alice dispatch is virtual only where the program needs it (Emitter.Virtual).</summary>
    static (string Vis, string Mod) Virtual(MethodSym m, AliceT a, string vis)
    {
        var owner = a.Sym;
        if (m.IsStatic || owner.Kind != "class" || vis == "private") return (vis, "");
        for (var b = owner.BaseClass; b != null; b = b.BaseClass)
            if (b.Methods.ContainsKey(m.Name)) return (vis, " override");
        for (var s = owner; s != null; s = s.BaseClass)
            foreach (var b in s.Bases)
            {
                if (b.Clr == null || b.Clr.IsInterface) continue;
                foreach (var bm in b.Clr.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (bm.Name == m.CsName && bm.IsVirtual && !bm.IsFinal && bm.GetParameters().Length == m.Params.Count && !bm.IsPrivate && !bm.IsAssembly)
                        return (bm.IsPublic ? "public" : "protected", " override");
            }
        return (vis, owner.HasSubclass ? " virtual" : "");
    }

    // ------------------------------------------------------------------ bodies

    IlMethodDef? EmitBodies(List<FileNode> asts, bool exe)
    {
        foreach (var am in toEmit) Guard(am.ToString(), () => MethodBody(am, asts));
        if (SynthOn) SynthBodies(); // S3 il-emit2
        else
        {
            foreach (var a in Types.BySym.Values.Distinct())
                foreach (var ctor in a.Ctors.Where(x => x.Decl == null && x.Owner == a).ToList())
                    if (!ctor.Def.Attributes.HasFlag(MethodAttributes.Private) || !a.IsAdt) Guard(a + "::.ctor", () => SyntheticCtor(ctor));
            foreach (var (owner, inits) in staticInits) Guard(owner + "::.cctor", () => StaticCtor(owner, inits));
        }
        IlMethodDef? main = null;
        var mains = asts.Where(f => f.Items.OfType<StmtItem>().Any()).ToList();
        if (mains.Count > 1) Fail(new IlUnsupportedException("Main:several-files"));
        if (mains.Count == 1)
        {
            var f = mains[0];
            var md = ProgramT.Def.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, IlType.Void, [IlType.String.SzArray()]);
            md.DefineParameter(1, "ARGV");
            Guard("Main", () =>
            {
                File = f.Path;
                lowerer.File = f.Path;
                var stmts = f.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList();
                lowerer.Type = binder.Program;
                lowerer.Method = null;
                var body = lowerer.LowerBody(stmts, Tail.None, f.HoistedLocals, stmts[0].Line);
                Optimizer.Pipeline.Run(body);
                DynLower(body); // H4a il-emit2
                CheckLowerer(Sp(stmts[0]));
                IlLower(body);
                var self = new AMethod { Name = "Main", Owner = ProgramT, IsStatic = true, Def = md, Params = [typeof(string[])], ParamNames = ["ARGV"] };
                var b = new IlBody(this, ProgramT, self, Body(md), typeof(void), true, [("ARGV", typeof(string[]))]);
                b.Body(body, () =>
                {
                    var il = md.Il;
                    il.EmitCall(Module.Import(typeof(Alice.Runtime.Prelude).GetMethod("init", Type.EmptyTypes)!));
                    il.EmitLoadArg(0);
                    il.EmitCall(Module.Import(typeof(Alice.Runtime.Prelude).GetProperty("ARGV")!.GetSetMethod()!));
                });
            });
            if (exe) main = md;
        }
        return main;
    }

    void Guard(string where, Action a)
    {
        Stats.Methods++;
        try
        {
            a();
            Stats.MethodsOk++;
        }
        catch (IlUnsupportedException e) { Fail(e); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or NullReferenceException or KeyNotFoundException or IndexOutOfRangeException or InvalidCastException or CompileException or TypeLoadException)
        {
            Fail(new IlUnsupportedException("Internal:" + e.GetType().Name, null, where + ": " + e.Message));
        }
    }

    void MethodBody(AMethod am, List<FileNode> asts)
    {
        var d = am.Decl!;
        var m = am.Sym!;
        File = d.File.Length > 0 ? d.File : FileOf(asts, d);
        lowerer.File = File;
        if (am.Inst != null || m.HasInferredGenerics) binder.Rebind(d, am.Owner.Sym, am.Inst);
        lowerer.Type = am.Owner.Sym;
        lowerer.Method = m;
        lowerer.Forked = false;
        var retA = am.Inst?.Ret ?? m.Ret;
        bool returns = !retA.IsVoid && !m.IsCtor && !m.IsIterator;
        bool plain = !m.IsCtor && !(d.Name == "to_s" && m.Params.Count == 0) && d.Name != "[]" && !AliceOps.ContainsKey(d.Name);
        lowerer.CloneOk = plain && Virtual(m, am.Owner, d.Vis).Mod == "";
        List<MStmt> body;
        try { body = lowerer.LowerBody(d.Body, returns ? Tail.Return : Tail.None, d.HoistedLocals, d.Line); }
        finally { lowerer.CloneOk = false; }
        Optimizer.Pipeline.Run(body);
        DynLower(body); // H4a il-emit2
        CheckLowerer(Sp(d));
        IlLower(body);
        body = SmLower(am, body); // H3b il-emit2
        if (lowerer.Forked) throw new IlUnsupportedException("Lowerer.Forked", Sp(d), m.Name);
        if (!m.IsCtor && hot.IsHot(d, m, am.Inst, body)) am.Def.ImplAttributes |= MethodImplAttributes.AggressiveOptimization;
        var ps = am.ParamNames.Select((n, i) => (n, am.Params[i])).ToList();
        var il = Body(am.Def);
        var b = new IlBody(this, am.Owner, am, il, am.Ret, am.IsStatic, ps);
        b.Body(body, m.IsCtor ? (Action)(() => { if (SynthOn) SynthCtorPrologue(b, am, d); else CtorPrologue(b, am, d); }) : null); // S4 il-emit2
    }

    /// <summary>Field initializers, the base constructor call (`super` arguments), then `initialize(@x)` auto assignments.</summary>
    void CtorPrologue(IlBody b, AMethod ctor, MethodDecl? d)
    {
        var a = ctor.Owner;
        var il = ctor.Def.Il;
        if (instanceInits.TryGetValue(a, out var inits))
            foreach (var (init, f, file) in inits)
            {
                File = file;
                var v = Init(init, a.Sym);
                il.EmitLoadArg(0);
                b.Emit(v, f.Type);
                il.EmitStoreField(f.Field!);
            }
        if (!a.IsValueType)
        {
            il.EmitLoadArg(0);
            if (d?.SuperInit is { } sup)
            {
                var args = sup.Select(x => Init(x.Value!, a.Sym)).ToList();
                if (a.Base != null)
                {
                    var infos = args.Select(x => new ArgInfo(b.Infer(x), null, false, "")).ToList();
                    var cand = Types.Pick(a.Base.Ctors.Select(IlTypes.FromAlice), infos) ?? throw new IlUnsupportedException("Ctor:super", Sp(d), a.ToString());
                    for (int i = 0; i < args.Count; i++) b.Emit(args[i], cand.Params[i]);
                    il.EmitCall(cand.Alice!.Def);
                }
                else
                {
                    var bt = a.ClrBase ?? typeof(object);
                    var infos = args.Select(x => new ArgInfo(b.Infer(x), null, false, "")).ToList();
                    var cand = Types.Pick(bt.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(x => !x.IsPrivate).Select(x => IlTypes.FromClr(x)), infos)
                        ?? throw new IlUnsupportedException("Ctor:super", Sp(d), bt.Name);
                    for (int i = 0; i < args.Count; i++) b.Emit(args[i], cand.Params[i]);
                    il.EmitCall(Types.Method(cand.Clr!));
                }
            }
            else BaseDefaultCtor(a, il);
        }
        if (d != null && ctor.Sym != null)
            for (int i = 0; i < ctor.Sym.Params.Count; i++)
            {
                var p = ctor.Sym.Params[i];
                if (!p.Auto) continue;
                var f = a.FindField(Bare(Binder.Mangle(p.Name))) ?? throw new IlUnsupportedException("Ctor:auto-field", Sp(d), p.Name);
                il.EmitLoadArg(0);
                il.EmitLoadArg(i + 1);
                il.EmitStoreField(f.Field!);
            }
    }

    void BaseDefaultCtor(AliceT a, IlBuilder il)
    {
        if (a.Base != null)
        {
            var bc = a.Base.Ctors.FirstOrDefault(x => x.Params.Length == 0 || x.HasDefault?.All(h => h) == true)
                ?? throw new IlUnsupportedException("Ctor:base-default", null, a.Base.ToString());
            if (bc.Params.Length > 0) throw new IlUnsupportedException("Ctor:base-defaults", null, a.Base.ToString());
            il.EmitCall(bc.Def);
            return;
        }
        var bt = a.ClrBase ?? typeof(object);
        var ci = bt.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes) ?? throw new IlUnsupportedException("Ctor:base-default", null, bt.Name);
        il.EmitCall(Types.Method(ci));
    }

    void SyntheticCtor(AMethod ctor)
    {
        var a = ctor.Owner;
        var il = Body(ctor.Def);
        var b = new IlBody(this, a, ctor, il, typeof(void), false, ctor.ParamNames.Select((n, i) => (n, ctor.Params[i])).ToList());
        if (recordCtors.TryGetValue(ctor, out var fields))
        {
            b.Body([], () =>
            {
                if (!a.IsValueType) { il.EmitLoadArg(0); BaseDefaultCtor(a, il); }
                for (int i = 0; i < fields.Count; i++)
                {
                    il.EmitLoadArg(0);
                    il.EmitLoadArg(i + 1);
                    il.EmitStoreField(fields[i].Field!);
                }
            });
            return;
        }
        b.Body([], () => CtorPrologue(b, ctor, null));
    }

    void StaticCtor(AliceT a, List<(MExpr? Value, AField F, string File)> inits)
    {
        var cctor = a.Def.DefineConstructor(MethodAttributes.Private | MethodAttributes.HideBySig, isStatic: true);
        var il = Body(cctor);
        var b = new IlBody(this, a, null, il, typeof(void), true, []);
        b.Body([], () =>
        {
            foreach (var (v, f, file) in inits)
            {
                File = file;
                if (v == null) continue;
                b.Emit(v, f.Type);
                il.EmitStoreField(f.Field!);
            }
        });
    }
}

/// <summary>
/// Which methods get [MethodImpl(AggressiveOptimization)]: the C# backend's own policy (Emitter.Kernel, LoopLeaves,
/// LoopNest, PrimitiveLoop), invoked by reflection so the two backends cannot drift.
/// </summary>
internal sealed class HotPolicy
{
    readonly Emitter emitter;
    static readonly MethodInfo? Kernel = typeof(Emitter).GetMethod("Kernel", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo? LoopLeaves = typeof(Emitter).GetMethod("LoopLeaves", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly MethodInfo? LoopNest = typeof(Emitter).GetMethod("LoopNest", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo? PrimitiveLoop = typeof(Emitter).GetMethod("PrimitiveLoop", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>True when every Emitter policy method was found (a test guards it).</summary>
    public static bool Available => Kernel != null && LoopLeaves != null && LoopNest != null && PrimitiveLoop != null;

    public HotPolicy(Binder binder) => emitter = new Emitter(binder, sequential: true);

    public bool IsHot(MethodDecl? d, MethodSym? m, Instance? inst, List<MStmt> body)
    {
        if (!Available) return false;
        try
        {
            if (d == null || m == null) return (bool)LoopNest!.Invoke(null, [body, 0])! || (bool)PrimitiveLoop!.Invoke(emitter, [body])!;
            if ((bool)Kernel!.Invoke(null, [d, m, inst, body])!) return true;
            if (((HashSet<MethodSym>)LoopLeaves!.Invoke(emitter, [])!).Contains(m)) return true;
            if (d.Attrs.Any(x => x is "Hot" or "Inline" or "NoInline")) return false;
            return (bool)LoopNest!.Invoke(null, [body, 0])! || !m.IsAsync && !m.UsesYield && (bool)PrimitiveLoop!.Invoke(emitter, [body])!;
        }
        catch (TargetInvocationException) { return false; }
    }
}
