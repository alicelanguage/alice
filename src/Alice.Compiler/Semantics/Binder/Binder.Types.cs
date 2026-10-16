using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    static readonly Dictionary<string, AType> PrimNames = new()
    {
        ["Int8"] = AType.SByte,
        ["Int16"] = AType.Short,
        ["Int32"] = AType.Int,
        ["Int"] = AType.Int,
        ["Int64"] = AType.Long,
        ["UInt8"] = AType.Byte,
        ["UInt16"] = AType.UShort,
        ["UInt32"] = AType.UInt,
        ["UInt64"] = AType.ULong,
        ["Float32"] = AType.Float,
        ["Float64"] = AType.Double,
        ["Float"] = AType.Double,
        ["Decimal"] = AType.Decimal,
        ["Bool"] = AType.Bool,
        ["String"] = AType.String,
        ["Char"] = AType.Char,
        ["Object"] = AType.Object,
        ["Any"] = AType.Dynamic,
        ["Nil"] = AType.Void,
        ["Void"] = AType.Void,
    };

    public static string PrimCs(string aliceName) => PrimNames.TryGetValue(aliceName, out var t) && t.Kind != "void" ? t.Cs : aliceName;

    public AType ResolveType(TypeRef r)
    {
        AType t = ResolveCore(r);
        if (r.Pointer) t = new AType("prim", t.Cs + "*", t.Clr?.MakePointerType());
        if (r.Nullable) t = t.AsNullable();
        return t;
    }

    AType ResolveCore(TypeRef r)
    {
        var args = r.Args.Select(ResolveType).ToList();
        if (r.Special == "tuple") return Tuple(args);
        if (r.Special == "proc") return Func(args.Take(args.Count - 1).ToList(), args[^1]);
        string name = r.Name;
        if (args.Count == 0 && PrimNames.TryGetValue(name, out var prim)) return prim;
        if (args.Count == 0 && typeParams.Contains(name)) return new AType("param", name);
        switch (name)
        {
            case "Array" when args.Count == 1: return AType.Array(args[0]);
            case "Pointer" when args.Count == 1: return new AType("prim", args[0].Cs + "*", args[0].Clr?.MakePointerType());
            case "Tuple": return Tuple(args);
            case "Proc" when args.Count >= 1: return Func(args.Take(args.Count - 1).ToList(), args[^1]);
            case "Hash" when args.Count == 2:
                return Builtins.Generic(typeof(Dictionary<,>), args[0], args[1]);
            case "SoA" when args.Count == 1:
                if (args[0].Sym is { SoaStore: null, Decl: { } sd } lazy && sd.Attrs.Contains("SoA")) EnsureSoaStore(lazy);
                if (args[0].Sym?.SoaStore is TypeSym store) return store.Type;
                throw new CompileException($"{file}:{r.Line}:{r.Col}: SoA({args[0].Cs}) needs `@[SoA]` on {args[0].Cs}");
        }
        if (Types.TryGetValue(name, out var sym))
        {
            if (args.Count == 0) return sym.Type;
            return new AType(sym.Type.Kind, sym.Type.Cs + "<" + string.Join(", ", args.Select(a => a.Cs)) + ">") { Sym = sym, Args = args };
        }
        var clr = Clr.Find(name, args.Count, usings);
        if (clr != null)
        {
            var closed = ClrTypes.Close(clr, args);
            if (closed != null) return ClrTypes.FromClr(closed);
            var open = ClrTypes.FromClr(clr.IsGenericTypeDefinition ? clr : clr);
            string baseName = clr.IsGenericTypeDefinition ? GenericBase(clr) : open.Cs;
            return new AType(clr.IsValueType ? "struct" : "class", args.Count > 0 ? baseName + "<" + string.Join(", ", args.Select(a => a.Cs)) + ">" : baseName) { Args = args };
        }
        string cs = name.Replace("::", ".");
        return new AType("class", args.Count > 0 ? cs + "<" + string.Join(", ", args.Select(a => a.Cs)) + ">" : cs) { Args = args };
    }

    /// <summary>Nearest shared base class (Alice or .NET), e.g. two sum-type variants -> their enum.</summary>
    static AType? CommonBase(AType a, AType b)
    {
        for (var x = a.Sym; x != null; x = x.BaseClass ?? x.Parent)
            for (var y = b.Sym; y != null; y = y.BaseClass ?? y.Parent)
                if (x == y) return x.Type;
        if (a.Clr == null || b.Clr == null || a.Clr.IsValueType || b.Clr.IsValueType) return null;
        for (var x = a.Clr; x != null && x != typeof(object); x = x.BaseType)
            if (x.IsAssignableFrom(b.Clr)) return ClrTypes.FromClr(x);
        return null;
    }

    static string GenericBase(Type open)
    {
        string n = open.FullName ?? open.Name;
        int tick = n.IndexOf('`');
        return "global::" + (tick >= 0 ? n.Substring(0, tick) : n).Replace('+', '.');
    }

    static AType Tuple(List<AType> items)
    {
        var t = new AType("tuple", "(" + string.Join(", ", items.Select(i => i.Cs)) + ")") { Args = items };
        if (items.Count is >= 2 and <= 7 && items.All(i => i.Clr != null))
        {
            var open = Type.GetType("System.ValueTuple`" + items.Count)!;
            t.Clr = open.MakeGenericType(items.Select(i => i.Clr!).ToArray());
        }
        return t;
    }

    public static AType Func(List<AType> ps, AType ret)
    {
        string cs = ret.IsVoid
            ? (ps.Count == 0 ? "global::System.Action" : "global::System.Action<" + string.Join(", ", ps.Select(p => p.Cs)) + ">")
            : "global::System.Func<" + string.Join(", ", ps.Select(p => p.Cs).Append(ret.Cs)) + ">";
        var t = new AType("func", cs) { Args = ps.Append(ret).ToList() };
        return t;
    }

    public bool IsTypeName(string name) => Types.ContainsKey(name) || (Clr.Find(name, 0, Array.Empty<string>()) is Type t && !IsNamespace(name) && t != null);

    static bool IsNamespace(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => { try { return a.GetExportedTypes().Any(t => t.Namespace == name); } catch (Exception) { return false; } });

    /// <summary>Resolves a dotted const path to a type, if the whole path names one.</summary>
    AType? TypeByName(string name, List<TypeRef> args)
    {
        if (args.Count == 0 && PrimNames.TryGetValue(name, out var prim) && !prim.IsVoid && prim.Kind != "dynamic") return prim;
        if (args.Count == 0 && typeParams.Contains(name)) return new AType("param", name);
        if (name is "Array" or "Pointer" or "Tuple" or "Proc" or "SoA" or "Hash" && args.Count > 0) return ResolveType(new TypeRef { Name = name, Args = args });
        if (Types.ContainsKey(name)) return ResolveType(new TypeRef { Name = name, Args = args });
        if (Clr.Find(name, args.Count, usings) != null) return ResolveType(new TypeRef { Name = name, Args = args });
        return null;
    }

    static AType Unify(IEnumerable<AType> types)
    {
        AType? r = null;
        bool sawNull = false;
        foreach (var t in types)
        {
            if (t.Kind == "null") { sawNull = true; continue; }
            if (t.IsUnknown) continue;
            if (r == null) { r = t; continue; }
            if (r.Same(t)) continue;
            var pr = r.Sym?.Parent ?? r.Sym;
            var pt = t.Sym?.Parent ?? t.Sym;
            if (pr != null && pr == pt && pr.Kind == "adt") { r = pr.Type; continue; }
            if (r.IsNumeric && t.IsNumeric) { r = r.NumericRank >= t.NumericRank ? r : t; continue; }
            if (r.Kind == "dynamic" || t.Kind == "dynamic") { r = AType.Dynamic; continue; }
            r = CommonBase(r, t) ?? AType.Object;
        }
        if (r == null) return sawNull ? AType.Null : AType.Unknown;
        return sawNull ? r.AsNullable() : r;
    }
}
