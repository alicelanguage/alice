using System.Reflection;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// Generic Alice types and methods in the IL backend (backend plan D1.10, ADR 0033): monomorphization, not CLR
/// generics. A generic type (class Box(T), enum Option(T), records) is a definition only
/// (<see cref="IlTypes.GenericDefs"/>); every instantiation reached (Box&lt;int&gt; anywhere a type is spelled) becomes its
/// own TypeDef (<see cref="InstantiateTypeImpl"/>) whose members are declared and whose bodies are emitted with the type
/// parameters bound in <see cref="IlTypes.Scope"/>, so T parses as the argument, a.CompareTo(b) binds to the real
/// member, and no constraint needs modelling. A generic method (def id&lt;T&gt;) is a template whose parameters mention
/// stand-in generic parameters; <see cref="IlTypes.Pick"/> infers the arguments like for a CLR generic method and asks
/// <see cref="InstantiateMethodImpl"/> for the instance (one MethodDef per distinct argument list).
/// Observable difference from the C# backend: runtime type names (Box&lt;Int32&gt; is a distinct class per argument list).
/// </summary>
public sealed partial class AirToIl
{
    readonly Dictionary<AliceT, List<Item>> genericItems = new();
    readonly Dictionary<AliceT, string> genericNs = new();
    readonly Dictionary<TypeSym, AliceT> genericBySym = new();
    readonly Dictionary<string, AliceT> typeInstances = new();
    readonly Dictionary<string, AMethod?> methodInstances = new();
    readonly Queue<AliceT> pendingInst = new();
    List<FileNode> allAsts = [];
    int declaring;
    bool draining;

    static string StripGeneric(string cs)
    {
        int lt = cs.IndexOf('<');
        return lt < 0 ? cs : cs[..lt];
    }

    /// <summary>Registers a generic type declaration (or a reopening of it); instances are made on demand.</summary>
    void RegisterGenericDef(Item it, string ns)
    {
        var sym = (TypeSym)it.Sym!;
        if (!genericBySym.TryGetValue(sym, out var def))
        {
            def = new AliceT { Sym = sym };
            if (it is TypeDecl td)
            {
                def.IsValueType = td.Kind is "struct" or "record struct";
                def.IsInterface = td.Kind == "interface";
                def.IsStatic = td.Kind is "module" || sym.Kind == "extension";
                def.IsRecord = td.Kind.StartsWith("record");
                if (sym.SoaStore != null || sym.SoaOf != null) throw new IlUnsupportedException("Decl:soa", Sp(it), sym.Name);
                if (sym.IsStack) throw new IlUnsupportedException("Decl:ref-struct", Sp(it), sym.Name);
                if (def.IsStatic) throw new IlUnsupportedException("Decl:generic-module", Sp(it), sym.Name);
            }
            else if (it is EnumDecl ed)
            {
                if (!ed.IsAdt) throw new IlUnsupportedException("Decl:generic-enum", Sp(it), sym.Name);
                def.IsAdt = true;
            }
            genericBySym[sym] = def;
            genericItems[def] = [];
            genericNs[def] = ns;
            Types.GenericDefs[StripGeneric(sym.Type.Cs) + "`" + sym.TypeParams.Count] = def;
        }
        genericItems[def].Add(it);
    }

    static string Display(IlTypes types, Type t)
    {
        if (types.Alice(t) is { } a) return a.Def.Name;
        if (t.IsArray) return Display(types, t.GetElementType()!) + "[]";
        if (t.IsGenericType) return t.Name.Split((char)96)[0] + "<" + string.Join(",", t.GetGenericArguments().Select(x => Display(types, x))) + ">";
        return t.Name;
    }

    /// <summary>The instance of generic type <paramref name="def"/> for <paramref name="args"/>: the TypeDef and proxy now, the members
    /// at the end of the current declaration pass (or immediately when called while a body is emitted).</summary>
    AliceT InstantiateTypeImpl(AliceT def, Type[] args)
    {
        var sym = def.Sym;
        if (args.Length != sym.TypeParams.Count) throw new IlUnsupportedException("Type:generic-arity", null, sym.Name);
        if (args.Any(t => t.ContainsGenericParameters)) throw new IlUnsupportedException("Decl:generic-type-over-generic-method", null, sym.Name);
        string key = sym.Name + "<" + string.Join(",", args.Select(t => t.FullName ?? t.Name)) + ">";
        if (typeInstances.TryGetValue(key, out var hit)) return hit;
        var map = new Dictionary<string, Type>();
        for (int i = 0; i < args.Length; i++) map[sym.TypeParams[i]] = args[i];
        var scope = new TypeScope(map);
        var a = new AliceT
        {
            Sym = sym, IsValueType = def.IsValueType, IsInterface = def.IsInterface, IsStatic = def.IsStatic, IsRecord = def.IsRecord, IsAdt = def.IsAdt,
            GenericDef = def, TypeArgs = args, Scope = scope,
        };
        typeInstances[key] = a;
        var prevScope = Types.Scope;
        Types.Scope = scope;
        try
        {
            var ifaces = new List<IlType>();
            foreach (var b in sym.Bases)
            {
                var bt = Types.FromAType(b);
                if (Types.Alice(bt) is { } ba)
                {
                    if (ba.IsInterface) ifaces.Add(ba.Il);
                    else a.Base = ba;
                }
                else if (bt.IsInterface) ifaces.Add(Types.Il(bt));
                else if (bt != typeof(object) && bt.IsClass) a.ClrBase = bt;
            }
            var first = genericItems[def][0];
            a.Proxy = Types.MakeProxy(a, a.Base?.Proxy ?? a.ClrBase, null);
            a.Def = Module.DefineType(genericNs[def], sym.Name + "<" + string.Join(",", args.Select(t => Display(Types, t))) + ">", TypeAttrs(a, first, false));
            if (a.IsAdt)
            {
                foreach (var vs in sym.Variants)
                {
                    var va = new AliceT { Sym = vs, IsVariant = true, IsRecord = true, Base = a, Scope = scope, GenericDef = def, TypeArgs = args };
                    a.InstVariants[vs] = va;
                    va.Proxy = Types.MakeProxy(va, a.Proxy, null);
                    va.Def = a.Def.DefineNestedType(vs.Name, TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
                    va.Def.BaseType = a.Il;
                }
            }
            if (a.IsValueType) a.Def.BaseType = Module.CoreType("ValueType");
            else if (a.Base != null) a.Def.BaseType = a.Base.Il;
            else if (a.ClrBase != null) a.Def.BaseType = Types.Il(a.ClrBase);
            foreach (var i in ifaces) a.Def.Interfaces.Add(i);
            if (!a.IsInterface && !a.IsAdt) a.Def.AddCustomAttribute(Module.Import(typeof(Alice.Runtime.AliceTypeAttribute).GetConstructor(Type.EmptyTypes)!));
        }
        finally { Types.Scope = prevScope; }
        decls.Add((genericItems[def][0], a));
        pendingInst.Enqueue(a);
        if (declaring == 0) DrainInstances();
        return a;
    }

    /// <summary>Declares the members of every instance made since the last drain (instances made meanwhile are drained too).</summary>
    void DrainInstances()
    {
        if (draining) return;
        draining = true;
        try
        {
            while (pendingInst.Count > 0) DeclareInstance(pendingInst.Dequeue());
        }
        finally { draining = false; }
    }

    void DeclareInstance(AliceT a)
    {
        if (!SynthOn || synth == null) throw new IlUnsupportedException("Decl:generic-type (needs Synthesize)", null, a.ToString());
        var prevScope = Types.Scope;
        var prevFile = File;
        Types.Scope = a.Scope;
        declaring++;
        try
        {
            foreach (var it in genericItems[a.GenericDef!])
            {
                File = FileOf(allAsts, it);
                if (it is TypeDecl td) SynthUserMembers(td, a);
            }
            if (!a.IsEnum && !a.IsInterface && !a.IsStatic && !a.IsAdt)
                foreach (var f in a.Sym.Fields.Values)
                    if (f.Kind is "inferred" or "inferred-emitted" or "auto" or "auto-emitted" && !a.Fields.ContainsKey(Bare(f.CsName)))
                        Field(a, f, FieldAttributes.Private);
            if (synth.Types.FirstOrDefault(t => t.Sym == a.Sym) is { } airDecl) SynthDeclare(a, airDecl);
        }
        finally { declaring--; Types.Scope = prevScope; File = prevFile; }
    }

    /// <summary>The nested variant of generic sum-type instance <paramref name="a"/>, or the plain variant of a non-generic one.</summary>
    AliceT? VariantOf(AliceT a, TypeSym variant) =>
        a.InstVariants.TryGetValue(variant, out var v) ? v : Types.BySym.GetValueOrDefault(variant);

    // ------------------------------------------------------------------ generic methods

    static class StandIns
    {
        public static void M1<A>() { }
        public static void M2<A, B>() { }
        public static void M3<A, B, C>() { }
        public static void M4<A, B, C, D>() { }
        public static void M5<A, B, C, D, E>() { }
        public static void M6<A, B, C, D, E, F>() { }
    }

    /// <summary>Declares generic method <paramref name="m"/> as a template: its signature over stand-in generic parameters.</summary>
    void DeclareTemplate(MethodDecl d, AliceT a, MethodSym m)
    {
        var sp = Sp(d);
        int n = m.TypeParams.Count;
        if (n > 6) throw new IlUnsupportedException("Decl:generic-method (more than 6 type parameters)", sp, m.Name);
        if (a.IsInterface || d.IsAbstract || m.IsCtor) throw new IlUnsupportedException("Decl:generic-method (interface, abstract or constructor)", sp, m.Name);
        var gps = typeof(StandIns).GetMethod("M" + n)!.GetGenericArguments();
        var map = new Dictionary<string, Type>(Types.Scope?.Map ?? []);
        for (int i = 0; i < n; i++) map[m.TypeParams[i]] = gps[i];
        var scope = new TypeScope(map);
        var prev = Types.Scope;
        Types.Scope = scope;
        try
        {
            bool ext = a.Sym.Kind == "extension";
            var ps = new List<Type>();
            var names = new List<string>();
            if (ext) { ps.Add(Types.FromAType(a.Sym.ExtTarget!, sp)); names.Add("self"); }
            for (int i = 0; i < m.Params.Count; i++)
            {
                var pt = Types.FromAType(m.Params[i].Type, sp);
                ps.Add(m.Params[i].Splat ? pt.MakeArrayType() : pt);
                names.Add(Bare(m.Params[i].CsName));
            }
            var ret = Types.FromAType(m.Ret, sp);
            var am = new AMethod
            {
                Name = m.CsName, Owner = a, Ret = ret, Params = [.. ps], ParamNames = [.. names], IsStatic = m.IsStatic || a.IsStatic || ext,
                IsExtension = ext, Sym = m, Decl = d, Scope = scope, GenericParams = gps[..n],
                ParamsArray = m.Params.Count > 0 && m.Params[^1].Splat,
            };
            a.Methods.Add(am);
        }
        finally { Types.Scope = prev; }
    }

    /// <summary>One MethodDef per distinct type-argument list of a generic method template (null: not instantiable).</summary>
    AMethod? InstantiateMethodImpl(AMethod template, Type[] args)
    {
        if (args.Any(t => t.ContainsGenericParameters)) return null;
        string key = template.Owner.GetHashCode() + "::" + template.Name + "/" + template.Params.Length + "<" + string.Join(",", args.Select(t => t.FullName ?? t.Name)) + ">";
        if (methodInstances.TryGetValue(key, out var hit)) return hit;
        var m = template.Sym!;
        var map = new Dictionary<string, Type>(template.Owner.Scope?.Map ?? []);
        for (int i = 0; i < args.Length; i++) map[m.TypeParams[i]] = args[i];
        var scope = new TypeScope(map);
        var prev = Types.Scope;
        Types.Scope = scope;
        declaring++;
        AMethod? inst;
        try
        {
            int before = template.Owner.Methods.Count;
            DeclareVariant(template.Decl!, template.Owner, m, null);
            inst = template.Owner.Methods.Skip(before).FirstOrDefault(x => x.Decl == template.Decl && x.Scope == scope);
        }
        finally { declaring--; Types.Scope = prev; }
        methodInstances[key] = inst;
        if (declaring == 0) DrainInstances();
        return inst;
    }
}
