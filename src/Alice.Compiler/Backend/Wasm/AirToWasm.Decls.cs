using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Wasm;

// Declarations: Alice types and the runtime's exception classes as WClass layouts, methods as wasm functions,
// static fields in the statics area, type ids (a preorder of the class tree), virtual dispatchers. Mirrors
// Backend/Il/AirToIl.Decls.cs.
public sealed partial class AirToWasm
{
    internal readonly List<WClass> classes = new();
    internal readonly Dictionary<TypeSym, WClass> bySym = new();
    internal readonly Dictionary<string, WClass> byCs = new(StringComparer.Ordinal);
    internal WClass ProgramC = null!;
    readonly List<(WMethod M, string File)> toEmit = new();
    readonly List<(WClass Owner, WField F, MExpr? Value, string File)> staticInits = new();
    internal readonly Dictionary<WClass, List<(Expr Init, WField F, string File)>> instanceInits = new();
    internal readonly Dictionary<WMethod, List<WField>> recordCtors = new();

    WClass Add(WClass c)
    {
        classes.Add(c);
        byCs[c.Name] = c;
        if (c.Sym != null) bySym[c.Sym] = c;
        return c;
    }

    // ------------------------------------------------------------------ runtime classes

    static readonly (string Name, string? Base)[] RuntimeExceptions =
    [
        ("System.Exception", null),
        ("System.SystemException", "System.Exception"),
        ("System.ArithmeticException", "System.SystemException"),
        ("System.OverflowException", "System.ArithmeticException"),
        ("System.DivideByZeroException", "System.ArithmeticException"),
        ("System.IndexOutOfRangeException", "System.SystemException"),
        ("System.NullReferenceException", "System.SystemException"),
        ("System.InvalidCastException", "System.SystemException"),
        ("System.FormatException", "System.SystemException"),
        ("System.ArgumentException", "System.SystemException"),
        ("System.ArgumentOutOfRangeException", "System.ArgumentException"),
        ("System.ArgumentNullException", "System.ArgumentException"),
        ("System.InvalidOperationException", "System.SystemException"),
        ("System.NotImplementedException", "System.SystemException"),
        ("System.NotSupportedException", "System.SystemException"),
        ("System.Collections.Generic.KeyNotFoundException", "System.SystemException"),
        ("System.StackOverflowException", "System.SystemException"),
        ("System.OutOfMemoryException", "System.SystemException"),
        ("System.Diagnostics.UnreachableException", "System.Exception"),
        ("System.TimeoutException", "System.SystemException"),
        ("System.IO.IOException", "System.SystemException"),
    ];

    void DeclareRuntimeClasses()
    {
        foreach (var (n, b) in RuntimeExceptions)
        {
            var c = Add(new WClass { Name = n, Base = b != null ? byCs[b] : null });
            if (b == null) c.AddField("Message", WT.Str);
        }
        ProgramC = Add(new WClass { Name = "Program", IsStatic = true, Sym = binder.Program });
    }

    readonly Dictionary<int, WClass> slotClasses = new();

    /// <summary>The slot value of an N-word arena slab (ADR 0037): a struct of N u64 words, `(ulong, ulong, ...)`.</summary>
    internal WClass SlotClass(int words)
    {
        if (slotClasses.TryGetValue(words, out var s)) return s;
        s = Add(new WClass { Name = "System.ValueTuple`" + words, IsStruct = true });
        for (int k = 0; k < words; k++) s.AddField("Item" + (k + 1), WT.U64);
        return slotClasses[words] = s;
    }

    internal WClass RuntimeClass(string name) => byCs.TryGetValue(name, out var c) ? c : throw new WasmUnsupportedException("Type:runtime-class", null, name);

    internal bool IsException(WClass c) => c.IsSubclassOf(byCs["System.Exception"]);

    readonly Dictionary<WClass, WasmFunc> newException = new();

    /// <summary>`(message) -> exception`: a new instance of a runtime exception class.</summary>
    internal WasmFunc NewException(WClass cls)
    {
        if (newException.TryGetValue(cls, out var f)) return f;
        f = M.Define("new " + cls.Name, new FuncSig([ValType.I32], [ValType.I32]), ["message"]);
        newException[cls] = f;
        var c = f.Code!;
        int o = c.Local(ValType.I32, "o");
        // the message is the caller's: it may be a fresh string, keep it rooted in a frame while allocating
        int fr = c.Local(ValType.I32, "frame");
        c.I32(8); c.Call(Rt.Func("frame_enter")); c.Set(fr);
        c.Get(fr); c.Get(0); c.Mem(Op.I32Store);
        c.I32(Math.Max(16, cls.Size)); c.I32(cls.Id); c.Call(Rt.Func("alloc")); c.Set(o);
        c.Get(o); c.Get(0); c.Mem(Op.I32Store, (uint)byCs["System.Exception"].FieldsByName["Message"].Offset);
        c.Get(fr); c.I32(8); c.Emit(Op.I32Add); c.GSet(Rt.Global("sp"));
        c.Get(o);
        c.Finish();
        return f;
    }

    // ------------------------------------------------------------------ Alice types

    static IEnumerable<(Item Item, Item? Outer)> AllTypeItems(IEnumerable<Item> items, Item? outer)
    {
        foreach (var it in items)
        {
            switch (it)
            {
                case NamespaceItem n:
                    foreach (var x in AllTypeItems(n.Items, null)) yield return x;
                    break;
                case TypeDecl td:
                    yield return (td, outer);
                    foreach (var x in AllTypeItems(td.Members, td)) yield return x;
                    break;
                case EnumDecl or LibDecl or DelegateDecl:
                    yield return (it, outer);
                    break;
            }
        }
    }

    readonly List<(Item Decl, WClass C)> decls = new();

    void DeclareTypes(List<FileNode> asts)
    {
        var all = asts.SelectMany(a => AllTypeItems(a.Items, null)).ToList();
        foreach (var (it, _) in all)
        {
            switch (it)
            {
                case LibDecl: throw new WasmUnsupportedException("Decl:lib (FFI; runtime = \"dotnet\")", Sp(it));
                case DelegateDecl: throw new WasmUnsupportedException("Decl:delegate", Sp(it));
                case TypeDecl { RefineScope: not null }: throw new WasmUnsupportedException("Decl:refinement", Sp(it));
            }
            var sym = (TypeSym)it.Sym!;
            if (bySym.TryGetValue(sym, out var existing)) { decls.Add((it, existing)); continue; }
            var c = new WClass { Name = sym.Type.Cs, Sym = sym };
            if (it is TypeDecl td)
            {
                if (sym.TypeParams.Count > 0) throw new WasmUnsupportedException("Decl:generic-type", Sp(it), sym.Name);
                if (sym.SoaStore != null || sym.SoaOf != null) throw new WasmUnsupportedException("Decl:soa", Sp(it), sym.Name);
                c.IsStruct = td.Kind is "struct" or "record struct";
                c.IsInterface = td.Kind == "interface";
                c.IsStatic = td.Kind is "module" || sym.Kind == "extension";
                c.IsRecord = td.Kind.StartsWith("record");
                c.IsAbstract = td.IsAbstract;
                if (sym.Kind == "extension") throw new WasmUnsupportedException("Decl:extension (monkey patch of a .NET type)", Sp(it), sym.Name);
            }
            else if (it is EnumDecl ed)
            {
                if (ed.IsAdt) c.IsAdt = true;
                else
                {
                    c.IsEnum = true;
                    c.EnumUnderlying = ed.Underlying != null ? FromAType(binder.ResolveType(ed.Underlying), Sp(ed)) : WT.I32;
                }
            }
            Add(c);
            decls.Add((it, c));
            if (it is EnumDecl { IsAdt: true })
                foreach (var vs in sym.Variants)
                    Add(new WClass { Name = vs.Type.Cs, Sym = vs, IsVariant = true, IsRecord = true, Base = c });
        }
        // bases and interfaces
        foreach (var c in classes.Where(x => x.Sym != null && x.Sym != binder.Program))
        {
            var s = c.Sym!;
            if (c.IsVariant) continue;
            if (s.BaseClass is { } bc && bySym.TryGetValue(bc, out var b)) c.Base = b;
            foreach (var bt in s.Bases)
            {
                if (bt.Sym != null && bySym.TryGetValue(bt.Sym, out var bi))
                {
                    if (bi.IsInterface) c.Interfaces.Add(bi);
                }
                else if (bt.Sym == null && bt.Clr is { } clr)
                {
                    if (clr.IsInterface) continue;   // IComparable, IEquatable...: structural in the wasm runtime
                    string n = clr.FullName ?? clr.Name;
                    if (byCs.TryGetValue(n, out var rc)) c.Base = rc;
                    else if (clr != typeof(object)) throw new WasmUnsupportedException("Decl:.NET-base", Sp((Item)s.Decl! ?? new StmtItem()), n);
                }
            }
        }
    }

    // ------------------------------------------------------------------ members

    void DeclareMembers(List<FileNode> asts)
    {
        foreach (var (it, c) in decls)
        {
            File = FileOf(asts, it);
            switch (it)
            {
                case TypeDecl td: TypeMembers(td, c); break;
                case EnumDecl ed when ed.IsAdt: Variants(ed, c); break;
                case EnumDecl ed: EnumMembers(ed, c); break;
            }
        }
        foreach (var ast in asts)
        {
            File = ast.Path;
            foreach (var it in ast.Items.Concat(ast.Items.OfType<NamespaceItem>().SelectMany(n => n.Items)))
                switch (it)
                {
                    case MethodDecl m: DeclareMethod(m, ProgramC); break;
                    case ConstDecl k: Const(k, ProgramC); break;
                }
        }
        foreach (var c in decls.Select(d => d.C).Distinct()) AutoMembers(c);
        AssignIds();
    }

    static string FileOf(List<FileNode> asts, Item it)
    {
        foreach (var a in asts) if (Contains(a.Items, it)) return a.Path;
        return asts[0].Path;
    }

    static bool Contains(List<Item> items, Item it) => items.Any(x => x == it || x is NamespaceItem n && Contains(n.Items, it) || x is TypeDecl t && Contains(t.Members, it));

    internal static string Bare(string n) => n.StartsWith('@') ? n[1..] : n;

    WField Field(WClass c, FieldSym fs)
    {
        string n = Bare(fs.CsName);
        if (c.FieldsByName.TryGetValue(n, out var have)) return have;
        var t = FromAType(fs.Type, null);
        bool st = fs.IsStatic || c.IsStatic;
        return c.AddField(n, t, st, fs);
    }

    void TypeMembers(TypeDecl d, WClass c)
    {
        var s = c.Sym!;
        if (d.Members.Any(m => m is MethodDecl { Name: "__class_body" })) throw new WasmUnsupportedException("Decl:class-body", Sp(d), s.Name);
        if (s.EnumerableElem != null) throw new WasmUnsupportedException("Decl:enumerable-bridge", Sp(d), s.Name);
        if (d.RecordParams.Count > 0)
        {
            var ps = s.FieldOrder.Select(fn => Field(c, s.Fields[fn])).ToList();
            var ctor = new WMethod { Name = ".ctor", Owner = c, IsCtor = true, Params = ps.Select(f => f.Type).ToList(), ParamNames = ps.Select(f => f.Name).ToList() };
            DefineFunc(ctor);
            c.Ctors.Add(ctor);
            recordCtors[ctor] = ps;
        }
        foreach (var it in d.Members)
        {
            switch (it)
            {
                case AccessorDecl acc:
                    {
                        var f = (FieldSym)acc.Sym!;
                        if (acc.Kind == "event") throw new WasmUnsupportedException("Decl:event", Sp(acc), f.Name);
                        if (c.IsInterface) throw new WasmUnsupportedException("Decl:interface-property", Sp(acc), f.Name);
                        var wf = Field(c, f);
                        if (acc.Init != null) AddInit(c, wf, acc.Init);
                        break;
                    }
                case FieldDecl fd:
                    {
                        var fs = (FieldSym)fd.Sym!;
                        if (fd.Attrs.Count > 0) throw new WasmUnsupportedException("Decl:field-attribute", Sp(fd), string.Join(",", fd.Attrs));
                        var wf = Field(c, fs);
                        if (fd.Init != null) AddInit(c, wf, fd.Init);
                        break;
                    }
                case ConstDecl k: Const(k, c); break;
                case MethodDecl { IsTemplate: true }: break;
                case MethodDecl m: DeclareMethod(m, c); break;
            }
        }
    }

    void EnumMembers(EnumDecl e, WClass c)
    {
        long next = 0;
        for (int i = 0; i < e.Names.Count; i++)
        {
            long v = next;
            if (e.Values[i] is { } ex)
            {
                var me = Init(ex, binder.Program);
                if (me is AirConst ac && Il.IlTypes.TryLiteral(ac.Text, out _, out var lit) && lit != null) v = System.Convert.ToInt64(lit);
                else if (me is MCode mc && Il.IlTypes.TryLiteral(mc.Text, out _, out var lit2) && lit2 is int or long or uint or ulong) v = System.Convert.ToInt64(lit2);
                else throw new WasmUnsupportedException("Decl:enum-value", Sp(e), e.Names[i]);
            }
            var f = c.AddField(e.Names[i], c.Type, isStatic: true);
            f.Literal = v;
            next = v + 1;
        }
    }

    void Variants(EnumDecl e, WClass adt)
    {
        foreach (var vs in adt.Sym!.Variants)
        {
            var va = bySym[vs];
            var ps = vs.FieldOrder.Select(fn => Field(va, vs.Fields[fn])).ToList();
            var ctor = new WMethod { Name = ".ctor", Owner = va, IsCtor = true, Params = ps.Select(f => f.Type).ToList(), ParamNames = ps.Select(f => f.Name).ToList() };
            DefineFunc(ctor);
            va.Ctors.Add(ctor);
            recordCtors[ctor] = ps;
            // a variant prints as the C# record it lowers to: `B { l = A { x = 1 }, r = ... }`
            if (!va.Methods.Any(m => m.Name == "ToString" && m.Params.Count == 0))
            {
                var ts = new WMethod { Name = "ToString", Owner = va, Ret = WT.Str, Synthetic = b => b.RecordToString() };
                DefineFunc(ts);
                va.Methods.Add(ts);
                synthMethods.Add(ts);
            }
            if (ps.Count == 0)
            {
                var vf = va.AddField("Value", va.Type, isStatic: true);
                va.UnitValue = vf;
                staticInits.Add((va, vf, new MNew { TypeCs = vs.Type.Cs }, File));
            }
        }
    }

    void Const(ConstDecl k, WClass c)
    {
        var fs = (FieldSym)k.Sym!;
        var t = FromAType(fs.Type, Sp(k));
        var f = c.AddField(k.Name, t, isStatic: true, sym: fs);
        staticInits.Add((c, f, Init(k.Value, c.Sym ?? binder.Program), File));
    }

    void AddInit(WClass c, WField f, Expr init)
    {
        if (f.IsStatic) { staticInits.Add((c, f, Init(init, c.Sym!), File)); return; }
        if (!instanceInits.TryGetValue(c, out var l)) instanceInits[c] = l = new();
        l.Add((init, f, File));
    }

    /// <summary>A field initializer or default value lowered as the C# backend does.</summary>
    internal MExpr Init(Expr e, TypeSym owner)
    {
        lowerer.Type = owner;
        lowerer.Method = null;
        lowerer.File = File;
        var tmp = lowerer.LowerBody([new ExprStmt { Expr = e, Line = e.Line }], Tail.Return, [], e.Line);
        Optimizer.Pipeline.Run(tmp);
        if (tmp.Count != 1 || tmp[^1] is not MReturn { Value: { } v }) throw new WasmUnsupportedException("Init:statements", Sp(e));
        CheckLowerer(Sp(e));
        return v;
    }

    void AutoMembers(WClass c)
    {
        if (c == ProgramC || c.IsEnum || c.IsInterface || c.IsAdt && !c.IsVariant) return;
        foreach (var f in c.Sym!.Fields.Values)
            if (f.Kind is "inferred" or "inferred-emitted" or "auto" or "auto-emitted" && !c.FieldsByName.ContainsKey(Bare(f.CsName)))
                Field(c, f);
        if (c.IsStatic) return;
        if (c.Ctors.Count == 0)
        {
            var ctor = new WMethod { Name = ".ctor", Owner = c, IsCtor = true };
            DefineFunc(ctor);
            c.Ctors.Add(ctor);
            synthCtors.Add(ctor);
        }
        if (c.IsRecord && !c.Methods.Any(m => m.Name == "ToString" && m.Params.Count == 0))
        {
            var ts = new WMethod { Name = "ToString", Owner = c, Ret = WT.Str, Synthetic = b => b.RecordToString() };
            DefineFunc(ts);
            c.Methods.Add(ts);
            synthMethods.Add(ts);
        }
    }

    internal readonly List<WMethod> synthCtors = new();
    internal readonly List<WMethod> synthMethods = new();

    static readonly Dictionary<string, string> AliceOps = new()
    {
        ["+"] = "op_Add", ["-"] = "op_Sub", ["*"] = "op_Mul", ["/"] = "op_Div", ["%"] = "op_Mod", ["=="] = "op_Eq", ["<=>"] = "op_Cmp",
        ["!="] = "op_Ne", ["<"] = "op_Lt", [">"] = "op_Gt", ["<="] = "op_Le", [">="] = "op_Ge", ["<<"] = "op_Shl", [">>"] = "op_Shr",
        ["&"] = "op_BitAnd", ["|"] = "op_BitOr", ["^"] = "op_Xor", ["~"] = "op_Inv", ["==="] = "op_CaseEq", ["[]="] = "op_IndexSet",
    };

    static readonly Dictionary<string, string> AliceUnary = new() { ["-"] = "op_Neg", ["+"] = "op_Plus", ["~"] = "op_Inv" };

    void DeclareMethod(MethodDecl d, WClass c)
    {
        var m = (MethodSym)d.Sym!;
        if (d.IsPartial) throw new WasmUnsupportedException("Decl:partial-method", Sp(d), m.Name);
        if (m.IsAsync) throw new WasmUnsupportedException("Decl:async (phase 2; runtime = \"dotnet\")", Sp(d), m.Name);
        if (m.IsIterator || m.UsesYield) throw new WasmUnsupportedException("Decl:iterator", Sp(d), m.Name);
        if (m.TypeParams.Count > 0 && !m.HasInferredGenerics) throw new WasmUnsupportedException("Decl:generic-method", Sp(d), m.Name);
        if (d.Attrs.Any(x => x is not ("Inline" or "NoInline" or "Hot" or "StrictMath" or "Open" or "Stack" or "Pure") && !x.StartsWith("__enumerable:")))
            throw new WasmUnsupportedException("Decl:attribute", Sp(d), string.Join(",", d.Attrs));
        if (m.Params.Any(p => p.Block || p.Mod != "")) throw new WasmUnsupportedException("Decl:param-kind", Sp(d), m.Name);
        if (!m.HasInferredGenerics || !d.HasBody) { DeclareVariant(d, c, m, null); return; }
        int n = 0;
        foreach (var inst in m.Instances.Values.Where(i => i.Seen)) { DeclareVariant(d, c, m, inst); n++; }
        if (m.NeedsGeneric || n == 0) throw new WasmUnsupportedException("Decl:generic-method", Sp(d), m.Name);
    }

    void DeclareVariant(MethodDecl d, WClass c, MethodSym m, Instance? inst)
    {
        var sp = Sp(d);
        var retA = inst?.Ret ?? m.Ret;
        var wm = new WMethod
        {
            Owner = c, Sym = m, Decl = d, Inst = inst, IsCtor = m.IsCtor, IsStatic = m.IsStatic || c.IsStatic,
            IsAbstract = d.IsAbstract || c.IsInterface && !d.HasBody,
            Ret = m.IsCtor ? WT.Void : FromAType(retA, sp),
        };
        for (int i = 0; i < m.Params.Count; i++)
        {
            var pt = FromAType(inst != null ? inst.Args[i] : m.Params[i].Type, sp);
            wm.Params.Add(m.Params[i].Splat ? WT.ArrayOf(pt) : pt);
            wm.ParamNames.Add(Bare(m.Params[i].CsName));
        }
        string name = m.CsName;
        if (m.IsCtor) name = ".ctor";
        else if (d.Name == "to_s" && m.Params.Count == 0) name = "ToString";
        else if (d.Name == "[]") name = "get_Item";
        else if (AliceOps.TryGetValue(d.Name, out var on)) name = m.Params.Count == 0 && AliceUnary.TryGetValue(d.Name, out var un) ? un : on;
        wm.Name = name;
        wm.Defaults = new MExpr?[m.Params.Count];
        for (int i = 0; i < m.Params.Count; i++)
            if (d.Params[i].Default is { } dv) wm.Defaults[i] = Init(dv, c.Sym ?? binder.Program);
        DefineFunc(wm);
        if (m.IsCtor) c.Ctors.Add(wm); else c.Methods.Add(wm);
        if (!wm.IsAbstract && d.HasBody) toEmit.Add((wm, d.File.Length > 0 ? d.File : File));
    }

    /// <summary>The wasm function of a method: [sret][this] params -> result. A struct result is written through
    /// the hidden sret pointer; struct parameters are addresses.</summary>
    internal void DefineFunc(WMethod m)
    {
        var ps = new List<ValType>();
        var names = new List<string?>();
        if (m.Ret.IsStruct) { ps.Add(ValType.I32); names.Add("sret"); }
        if (!m.IsStatic) { ps.Add(ValType.I32); names.Add("this"); }
        foreach (var (t, n) in m.Params.Zip(m.ParamNames)) { ps.Add(t.Val); names.Add(n); }
        var rs = m.Ret.K == WK.Void || m.Ret.IsStruct ? Array.Empty<ValType>() : [m.Ret.Val];
        m.Func = M.Define(m.Owner.Name + "." + m.Name + (m.Inst != null ? "<" + string.Join(",", m.Params.Select(p => p.Name)) + ">" : ""), new FuncSig(ps.ToArray(), rs), names);
    }

    // ------------------------------------------------------------------ type ids and dispatch

    void AssignIds()
    {
        int id = 16;
        foreach (var c in classes) c.Subclasses.Clear();
        foreach (var c in classes) c.Base?.Subclasses.Add(c);
        void Visit(WClass c)
        {
            c.Id = id++;
            foreach (var s in c.Subclasses) Visit(s);
            c.IdEnd = id - 1;
        }
        foreach (var c in classes.Where(c => c.Base == null && !c.IsInterface && !c.IsStruct && !c.IsEnum)) Visit(c);
        foreach (var c in classes.Where(c => c.IsInterface || c.IsStruct || c.IsEnum)) { c.Id = id++; c.IdEnd = c.Id; }
        if (id > 0xFFFFFF) throw new WasmUnsupportedException("Decl:too-many-types");
        // virtual: an instance method redefined (same name and arity) in a subclass, or an interface method
        foreach (var c in classes)
            foreach (var m in c.Methods)
            {
                if (m.IsStatic || m.IsCtor) continue;
                bool overridden = Descendants(c).Any(s => s.Methods.Any(x => x.Name == m.Name && x.Params.Count == m.Params.Count));
                bool overrides = Ancestors(c).Any(b => b.Methods.Any(x => x.Name == m.Name && x.Params.Count == m.Params.Count));
                m.IsVirtual = c.IsInterface || overridden || overrides || m.IsAbstract || m.Name == "ToString";
            }
    }

    static IEnumerable<WClass> Descendants(WClass c)
    {
        foreach (var s in c.Subclasses)
        {
            yield return s;
            foreach (var x in Descendants(s)) yield return x;
        }
    }

    static IEnumerable<WClass> Ancestors(WClass c)
    {
        for (var b = c.Base; b != null; b = b.Base) yield return b;
    }

    /// <summary>The implementation of <paramref name="name"/>/<paramref name="arity"/> a class uses (its own or inherited).</summary>
    internal static WMethod? Implementation(WClass c, string name, int arity)
    {
        for (var k = c; k != null; k = k.Base)
            foreach (var m in k.Methods)
                if (m.Name == name && m.Params.Count == arity && !m.IsAbstract && !m.IsStatic) return m;
        return null;
    }

    readonly Dictionary<string, (WasmFunc F, WClass Root, WMethod Proto)> dispatchers = new(StringComparer.Ordinal);

    /// <summary>A virtual call: `dispatch(this, args)` switches on the receiver's type id to the implementation
    /// of every class that can be the receiver (a subtree of the class tree, or the implementers of an interface).</summary>
    internal WasmFunc Dispatcher(WMethod proto, WClass staticType)
    {
        var root = staticType;
        string key = root.Name + "::" + proto.Name + "/" + proto.Params.Count;
        if (dispatchers.TryGetValue(key, out var d)) return d.F;
        var f = M.Define("dispatch " + key, proto.Func.Sig);
        dispatchers[key] = (f, root, proto);
        return f;
    }

    void EmitDispatchers()
    {
        foreach (var (key, (f, root, proto)) in dispatchers)
        {
            var targets = root.IsInterface
                ? classes.Where(c => !c.IsInterface && !c.IsStruct && c.IsSubclassOf(root)).ToList()
                : Descendants(root).Prepend(root).ToList();
            var impls = targets.Select(t => (t, Implementation(t, proto.Name, proto.Params.Count))).Where(x => x.Item2 != null).ToList();
            var c = f.Code!;
            int self = proto.Ret.IsStruct ? 1 : 0;
            if (impls.Count == 0) { c.Emit(Op.Unreachable); c.Finish(); continue; }
            int lo = impls.Min(x => x.t.Id), hi = impls.Max(x => x.t.Id);
            var distinct = impls.Select(x => x.Item2!).Distinct().ToList();
            var outer = c.Block(proto.Func.Sig.Results.Length > 0 ? proto.Func.Sig.Results[0] : null);
            var blocks = new List<WasmLabel>();
            var dflt = c.Block();
            for (int i = 0; i < distinct.Count; i++) blocks.Add(c.Block());
            // type id - lo
            c.Get(self);
            c.Mem(Op.I32Load);
            c.I32(0xFFFFFF);
            c.Emit(Op.I32And);
            c.I32(lo);
            c.Emit(Op.I32Sub);
            var table = new List<WasmLabel>();
            for (int id = lo; id <= hi; id++)
            {
                var hit = impls.FirstOrDefault(x => x.t.Id == id).Item2;
                table.Add(hit == null ? dflt : blocks[distinct.IndexOf(hit)]);
            }
            c.BrTable(table, dflt);
            for (int i = distinct.Count - 1; i >= 0; i--)
            {
                c.End(blocks[i]);
                for (int p = 0; p < f.Sig.Params.Length; p++) c.Get(p);
                c.Call(distinct[i].Func);
                c.Br(outer);
            }
            c.End(dflt);
            c.Emit(Op.Unreachable);
            c.End(outer);
            c.Finish();
        }
    }

    // ------------------------------------------------------------------ bodies

    void CheckLowerer(Span? sp)
    {
        if (lowerer.Extra.Count > 0)
        {
            lowerer.Extra.Clear();
            throw new WasmUnsupportedException("Lowerer.Extra (C# helper text)", sp);
        }
        if (lowerer.FlushSpecializations().Length > 0) throw new WasmUnsupportedException("Lowerer.Specialization (C# text)", sp);
        if (lowerer.UsesGpu) throw new WasmUnsupportedException("Lowerer.Gpu", sp);
    }

    static void IlLower(List<MStmt> body) => Air.Passes.IlLowering.Run(body);

    void Guard(string where, Action a)
    {
        Stats.Methods++;
        try
        {
            a();
            Stats.MethodsOk++;
        }
        catch (WasmUnsupportedException e) { Fail(e); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or NullReferenceException or KeyNotFoundException or IndexOutOfRangeException or InvalidCastException or CompileException or FormatException)
        {
            Fail(new WasmUnsupportedException("Internal:" + e.GetType().Name, null, where + ": " + e.Message));
        }
    }

    void EmitBodies(List<FileNode> asts)
    {
        foreach (var (m, file) in toEmit) Guard(m.ToString(), () => MethodBody(m, file));
        foreach (var ctor in synthCtors.Concat(recordCtors.Keys)) Guard(ctor.ToString(), () => new WasmBody(this, ctor).SyntheticCtor());
        foreach (var m in synthMethods) Guard(m.ToString(), () => new WasmBody(this, m).Synthetic());
        // static initializers in declaration order, one function
        if (staticInits.Count > 0)
        {
            var init = new WMethod { Name = "<static>", Owner = ProgramC, IsStatic = true };
            DefineFunc(init);
            Guard("<static>", () => new WasmBody(this, init).StaticInits(staticInits));
            staticInitFuncs.Add(init.Func);
        }
        var mains = asts.Where(f => f.Items.OfType<StmtItem>().Any()).ToList();
        if (mains.Count > 1) Fail(new WasmUnsupportedException("Main:several-files"));
        if (mains.Count == 1)
        {
            var f = mains[0];
            var main = new WMethod { Name = "Main", Owner = ProgramC, IsStatic = true };
            DefineFunc(main);
            Guard("Main", () =>
            {
                File = f.Path;
                lowerer.File = f.Path;
                var stmts = f.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList();
                lowerer.Type = binder.Program;
                lowerer.Method = null;
                var body = lowerer.LowerBody(stmts, Tail.None, f.HoistedLocals, stmts[0].Line);
                Optimizer.Pipeline.Run(body);
                CheckLowerer(Sp(stmts[0]));
                IlLower(body);
                new WasmBody(this, main).Body(body);
            });
            mainFunc = main.Func;
        }
    }

    void MethodBody(WMethod wm, string file)
    {
        var d = wm.Decl!;
        var m = wm.Sym!;
        File = file;
        lowerer.File = File;
        if (wm.Inst != null || m.HasInferredGenerics) binder.Rebind(d, wm.Owner.Sym ?? binder.Program, wm.Inst);
        lowerer.Type = wm.Owner.Sym ?? binder.Program;
        lowerer.Method = m;
        lowerer.Forked = false;
        var retA = wm.Inst?.Ret ?? m.Ret;
        bool returns = !retA.IsVoid && !m.IsCtor && !m.IsIterator;
        var body = lowerer.LowerBody(d.Body, returns ? Tail.Return : Tail.None, d.HoistedLocals, d.Line);
        Optimizer.Pipeline.Run(body);
        CheckLowerer(Sp(d));
        IlLower(body);
        var b = new WasmBody(this, wm);
        b.Body(body, m.IsCtor ? () => b.CtorPrologue(d) : null);
    }
}
