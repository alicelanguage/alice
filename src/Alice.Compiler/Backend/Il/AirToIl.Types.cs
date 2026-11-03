using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Binder = Alice.Compiler.Semantics.Binder;

namespace Alice.Compiler.Backend.Il;

/// <summary>A MIR node (or declaration) the IL backend cannot emit yet. <see cref="NodeKind"/> is the statistics key
/// (`MCall:unresolved-instance`, `Lowerer.Extra`, ...).</summary>
public sealed class IlUnsupportedException : Exception
{
    public IlUnsupportedException(string nodeKind, Span? span = null, string? detail = null)
        : base(nodeKind + (detail != null ? ": " + detail : "") + (span is { } s ? $" ({s.File}:{s.Line})" : ""))
    {
        NodeKind = nodeKind;
        Span = span;
        Detail = detail;
    }

    public string NodeKind { get; }
    public Span? Span { get; }
    public string? Detail { get; }
}

/// <summary>An Alice-declared type in the IL module: its TypeDef, the reflection stand-in used for typing
/// (<see cref="Proxy"/>: generic instantiations, arrays and base-class chains work through reflection) and members.</summary>
/// <summary>The type parameters of a generic type or method instance in scope (monomorphization).</summary>
internal sealed class TypeScope
{
    static int next;
    public readonly int Id = Interlocked.Increment(ref next);
    public readonly Dictionary<string, Type> Map;
    public TypeScope(Dictionary<string, Type> map) => Map = map;
}

internal sealed class AliceT
{
    public TypeSym Sym = null!;
    public TypeScope? Scope;             // a generic type's instance: T -> concrete
    public AliceT? GenericDef;
    public Type? OpenProxy;              // a generic type definition: its generic stand-in (see IlTypes.MakeOpenProxy)
    public Type[] TypeArgs = [];
    public IlTypeDef Def = null!;
    public Type Proxy = null!;
    public AliceT? Base;
    public Type? ClrBase;
    /// <summary>A closure display: the type whose method it was converted from (free calls resolve there).</summary>
    public AliceT? Host;
    public bool IsValueType, IsInterface, IsEnum, IsStatic, IsRecord, IsAdt, IsVariant, IsDelegate, IsRefStruct;
    public Type? EnumUnderlying;
    public readonly Dictionary<TypeSym, AliceT> InstVariants = new();   // a generic sum type's instance: its nested variants
    public readonly Dictionary<string, AField> Fields = new(StringComparer.Ordinal);
    public readonly List<AMethod> Methods = [];
    public readonly List<AMethod> Ctors = [];
    public IlType Il => Def.Type;
    public override string ToString() => Host != null ? Host + "/" + Def.Name : Sym.Type.Cs;

    public AField? FindField(string name)
    {
        for (var t = this; t != null; t = t.Base)
            if (t.Fields.TryGetValue(name, out var f)) return f;
        return null;
    }

    public IEnumerable<AMethod> FindMethods(string name)
    {
        var seen = new HashSet<string>();
        for (var t = this; t != null; t = t.Base)
            foreach (var m in t.Methods)
                if (m.Name == name && seen.Add(string.Join(",", m.Params.Select(p => p.FullName)))) yield return m;
    }
}

/// <summary>A field, auto property or enum member of an Alice type.</summary>
internal sealed class AField
{
    public string Name = "";
    public Type Type = null!;
    public bool IsStatic;
    public AliceT Owner = null!;
    public IlFieldDef? Field;            // the storage (backing field of a property)
    public IlMethodDef? Getter, Setter;  // property accessors (null: plain field)
    public IlMethodDef? Adder, Remover;  // event accessors (the field is the delegate storage)
    public object? Literal;              // enum member value
    public bool ReadOnly;
}

/// <summary>A method or constructor of an Alice type (one per specialization).</summary>
internal sealed class AMethod
{
    public string Name = "";
    public AliceT Owner = null!;
    public Type Ret = typeof(void);
    public Type[] Params = [];
    public string[] ParamNames = [];
    public object?[]? Defaults;          // per parameter: default value (null entry: required)
    public bool[]? HasDefault;
    public bool IsStatic, IsVirtual, IsAbstract, IsExtension, IsCtor, ParamsArray;
    public IlMethodDef Def = null!;
    public MethodSym? Sym;
    public Instance? Inst;
    public Syntax.MethodDecl? Decl;
    public TypeScope? Scope;             // the type parameters in scope (owner instance, own instance)
    public Type[]? GenericParams;        // a generic method template: its type parameters (stand-ins, never emitted)
    public override string ToString() => Owner + "::" + Name;
}

/// <summary>One overload candidate: a reflection method/constructor or an Alice method, with its parameter types.</summary>
internal sealed record Cand(MethodBase? Clr, AMethod? Alice, Type[] Params, Type Ret, bool IsStatic, bool Expanded, int Optional, object?[] DefaultsTail, bool Extension);

/// <summary>An argument as overload resolution sees it.</summary>
internal readonly record struct ArgInfo(Type Type, object? Literal, bool IsNull, string Prefix);

/// <summary>Type names, reflection lookups and C#-like conversions for <see cref="AirToIl"/>.</summary>
internal sealed class IlTypes
{
    static int proxyN;
    readonly ModuleBuilder proxies;
    readonly Binder binder;
    readonly IlModule module;
    readonly Dictionary<string, Type?> cache = new(StringComparer.Ordinal);
    public readonly Dictionary<Type, AliceT> ByProxy = new();
    public readonly Dictionary<string, AliceT> ByCs = new(StringComparer.Ordinal);
    public readonly Dictionary<TypeSym, AliceT> BySym = new();
    public readonly List<string> Usings = [.. ClrTypes.DefaultNamespaces];

    // monomorphization (AirToIl.Generics.cs): type parameter names -> concrete types while a generic body or declaration is processed
    public TypeScope? Scope;
    public readonly Dictionary<string, AliceT> GenericDefs = new(StringComparer.Ordinal);   // "Name`arity" -> generic type definition
    public Func<AliceT, Type[], AliceT>? InstantiateType;
    public Func<AMethod, Type[], AMethod?>? InstantiateMethod;

    public IlTypes(Binder binder, IlModule module)
    {
        this.binder = binder;
        this.module = module;
        int n = Interlocked.Increment(ref proxyN);
        proxies = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AliceIlProxies" + n), AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule("proxies");
    }

    // ------------------------------------------------------------------ proxies

    int pn;

    /// <summary>The reflection stand-in of a closure display class (AirToIl.Closures.cs).</summary>
    public Type MakeDisplayProxy(AliceT a)
    {
        var t = proxies.DefineType("P" + pn++ + "_display", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class).CreateType();
        ByProxy[t] = a;
        return t;
    }

    /// <summary>The argument type of a lambda (an <see cref="AirDelegateCreate"/> in <see cref="ArgInfo.Literal"/>): it
    /// converts to any delegate of its arity; its result type is inferred through <see cref="LambdaReturn"/>.</summary>
    public static readonly Type LambdaArg = typeof(LambdaMarker);
    sealed class LambdaMarker { }

    /// <summary>Set by the body resolving a call: the result type of a lambda argument for given parameter types.</summary>
    public Func<object, Type[], Type?>? LambdaReturn;

    static bool IsDelegate(Type t) => typeof(Delegate).IsAssignableFrom(t) && t != typeof(Delegate) && t != typeof(MulticastDelegate);

    /// <summary><paramref name="t"/> with the method generic parameters replaced by <paramref name="bound"/> (null: unbound).</summary>
    static Type? Subst(Type t, Type?[] bound)
    {
        if (t.IsGenericParameter) return t.DeclaringMethod != null ? bound[t.GenericParameterPosition] : null;
        if (t.IsByRef) return Subst(t.GetElementType()!, bound)?.MakeByRefType();
        if (t.IsArray) return Subst(t.GetElementType()!, bound) is { } e ? (t.IsSZArray ? e.MakeArrayType() : e.MakeArrayType(t.GetArrayRank())) : null;
        if (t.IsGenericType && t.ContainsGenericParameters)
        {
            var args = t.GetGenericArguments().Select(a => Subst(a, bound)).ToArray();
            return args.Any(a => a == null) ? null : t.GetGenericTypeDefinition().MakeGenericType(args!);
        }
        return t;
    }

    /// <summary>The reflection stand-in of an Alice type: a class (with the Alice base chain), struct or enum.</summary>
    public Type MakeProxy(AliceT a, Type? parent, IReadOnlyList<(string Name, object Value)>? enumValues)
    {
        string name = "P" + pn++ + "_" + a.Sym.Name;
        Type t;
        if (a.IsEnum)
        {
            var eb = proxies.DefineEnum(name, TypeAttributes.Public, a.EnumUnderlying ?? typeof(int));
            foreach (var (n, v) in enumValues ?? []) eb.DefineLiteral(n, v);
            t = eb.CreateType();
        }
        else if (a.IsInterface)
            t = proxies.DefineType(name, TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType();
        else if (a.IsValueType)
            t = proxies.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType)).CreateType();
        else
        {
            try { t = proxies.DefineType(name, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Class, parent ?? typeof(object)).CreateType(); }
            catch (Exception) when (parent != null) { t = proxies.DefineType(name + "_", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Class).CreateType(); }
        }
        ByProxy[t] = a;
        return t;
    }

    /// <summary>The reflection stand-in of an Alice delegate type: a sealed MulticastDelegate with the runtime-implemented
    /// constructor and Invoke, so overload resolution and conversions see a real delegate.</summary>
    public Type MakeDelegateProxy(AliceT a, Type ret, Type[] ps)
    {
        var tb = proxies.DefineType("P" + pn++ + "_" + a.Sym.Name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(MulticastDelegate));
        var ctor = tb.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName, CallingConventions.Standard, [typeof(object), typeof(IntPtr)]);
        ctor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        var inv = tb.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual, ret, ps);
        inv.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        var t = tb.CreateType();
        ByProxy[t] = a;
        return t;
    }

    public AliceT? Alice(Type t) => ByProxy.GetValueOrDefault(t);

    public bool HasProxy(Type t) =>
        ByProxy.ContainsKey(t) || t.HasElementType && HasProxy(t.GetElementType()!) || t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericArguments().Any(HasProxy);

    // ------------------------------------------------------------------ C# type text -> reflection type

    static readonly Dictionary<string, Type> Keywords = new()
    {
        ["void"] = typeof(void), ["bool"] = typeof(bool), ["char"] = typeof(char), ["sbyte"] = typeof(sbyte), ["byte"] = typeof(byte),
        ["short"] = typeof(short), ["ushort"] = typeof(ushort), ["int"] = typeof(int), ["uint"] = typeof(uint), ["long"] = typeof(long),
        ["ulong"] = typeof(ulong), ["float"] = typeof(float), ["double"] = typeof(double), ["decimal"] = typeof(decimal),
        ["string"] = typeof(string), ["object"] = typeof(object), ["nint"] = typeof(nint), ["nuint"] = typeof(nuint),
    };

    public Type Parse(string cs, Span? span = null) => TryParse(cs) ?? throw new IlUnsupportedException("Type:unresolved", span, cs);

    public Type? TryParse(string cs)
    {
        cs = cs.Trim();
        var scope = Scope;
        string key = scope == null ? cs : scope.Id + "|" + cs;
        if (cache.TryGetValue(key, out var hit)) return hit;
        Type? r;
        try { r = ParseCore(cs); }
        catch (ArgumentException) { r = null; }
        return cache[key] = r;
    }

    Type? ParseCore(string cs)
    {
        if (cs == "dynamic") return typeof(object); // il-emit2: Any is an object (DynSites, D1.9)
        if (cs.Length == 0 || cs is "var") return null;
        if (cs.EndsWith('?'))
        {
            var inner = TryParse(cs[..^1]);
            return inner == null ? null : inner.IsValueType && !inner.IsByRefLike ? typeof(Nullable<>).MakeGenericType(inner) : inner;
        }
        if (cs.EndsWith(']') && cs.LastIndexOf('[') is int lb and > 0 && cs[(lb + 1)..^1].All(c => c == ','))
        {
            var el = TryParse(cs[..lb]);
            int rank = cs.Length - lb - 1;
            return el == null ? null : rank == 1 ? el.MakeArrayType() : el.MakeArrayType(rank);
        }
        if (cs.EndsWith('*') && !cs.StartsWith("delegate*")) return TryParse(cs[..^1])?.MakePointerType();
        if (cs.StartsWith('(') && cs.EndsWith(')'))
        {
            var items = SplitTop(cs[1..^1]).Select(StripName).Select(TryParse).ToList();
            if (items.Any(t => t == null) || items.Count is < 2 or > 7) return null;
            return Type.GetType("System.ValueTuple`" + items.Count)!.MakeGenericType(items.ToArray()!);
        }
        if (Keywords.TryGetValue(cs, out var k)) return k;
        if (Scope != null && Scope.Map.TryGetValue(cs, out var tp)) return tp;
        if (ByCs.TryGetValue(cs, out var at)) return at.Proxy;
        string bare = cs.StartsWith("global::") ? cs[8..] : cs;
        if (ByCs.TryGetValue(bare, out var at2)) return at2.Proxy;
        // a type nested in a generic one: `List<int>.Enumerator`, `Dictionary<K, V>.KeyCollection`
        int gt = bare.LastIndexOf('>');
        if (gt > 0 && gt + 1 < bare.Length && bare[gt + 1] == '.' && IsIdentifier(bare[(gt + 2)..]))
        {
            var outer = TryParse(bare[..(gt + 1)]);
            var nested = outer is { IsGenericType: true } ? outer.GetGenericTypeDefinition().GetNestedType(bare[(gt + 2)..]) : null;
            return nested == null ? null : nested.IsGenericTypeDefinition ? nested.MakeGenericType(outer!.GetGenericArguments()) : nested;
        }
        int lt = bare.IndexOf('<');
        if (lt > 0 && bare.EndsWith('>'))
        {
            var args = SplitTop(bare[(lt + 1)..^1]).Select(TryParse).ToList();
            if (args.Any(a => a == null)) return null;
            if (GenericDefs.TryGetValue(bare[..lt] + "`" + args.Count, out var gdef))
                return args.Any(x => x!.ContainsGenericParameters) ? gdef.OpenProxy!.MakeGenericType(args.ToArray()!) : InstantiateType!(gdef, args.ToArray()!).Proxy;
            var open = binder.Clr.Find(bare[..lt], args.Count, Usings);
            return open?.MakeGenericType(args.ToArray()!);
        }
        if (bare.Contains('(') || bare.Contains(' ')) return null;
        return binder.Clr.Find(bare, 0, Usings);
    }

    static bool IsIdentifier(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>`int x` -> `int` (named tuple element); a lone type stays.</summary>
    static string StripName(string item)
    {
        int depth = 0;
        for (int i = item.Length - 1; i >= 0; i--)
        {
            char c = item[i];
            if (c is '>' or ')' or ']') depth++;
            else if (c is '<' or '(' or '[') depth--;
            else if (c == ' ' && depth == 0) return item[..i].Trim();
        }
        return item;
    }

    public static List<string> SplitTop(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>A binder type as a reflection type (through its C# spelling, which the binder guarantees valid).</summary>
    public Type FromAType(AType t, Span? span = null)
    {
        if (t.IsVoid) return typeof(void);
        if (t.Sym != null && BySym.TryGetValue(t.Sym, out var a) && t.Args.Count == 0 && !t.Nullable) return a.Proxy;
        if (t.Kind == "dynamic") return typeof(object); // il-emit2: Any is an object (DynSites, D1.9)
        if (t.IsUnknown) throw new IlUnsupportedException("Type:dynamic", span, t.Cs);
        return Parse(Lowering.Lowerer.TypeCs(t), span);
    }

    // ------------------------------------------------------------------ reflection type -> IL type

    public IlType Il(Type t)
    {
        if (ByProxy.TryGetValue(t, out var a)) return a.Il;
        if (t.IsByRef) return Il(t.GetElementType()!).ByRef();
        if (t.IsPointer) return Il(t.GetElementType()!).Pointer();
        if (t.IsSZArray) return Il(t.GetElementType()!).SzArray();
        if (t.IsArray) return Il(t.GetElementType()!).Array(t.GetArrayRank());
        if (t.IsConstructedGenericType && HasProxy(t))
            return IlType.Generic(((IlType.NamedType)module.Import(t.GetGenericTypeDefinition())).Entity, [.. t.GetGenericArguments().Select(Il)]);
        return module.Import(t);
    }

    /// <summary>A reflection method or constructor as a MemberRef on its (possibly proxy-instantiated) declaring type,
    /// with the open definition's signature (custom modifiers kept), and a MethodSpec for generic instantiations.</summary>
    public IlMethod Method(MethodBase m)
    {
        var def = m.Module.ResolveMethod(m.MetadataToken)!;
        if (def is MethodInfo { IsGenericMethod: true } gd && !gd.IsGenericMethodDefinition) def = gd.GetGenericMethodDefinition();
        var ret = def is MethodInfo dm ? Sig(dm.ReturnType, dm.ReturnParameter.GetRequiredCustomModifiers(), dm.ReturnParameter.GetOptionalCustomModifiers()) : IlType.Void;
        var ps = def.GetParameters().Select(p => Sig(p.ParameterType, p.GetRequiredCustomModifiers(), p.GetOptionalCustomModifiers())).ToArray();
        var sig = new MethodSig(ret, ps, isInstance: !def.IsStatic, genericArity: def.IsGenericMethodDefinition ? def.GetGenericArguments().Length : 0);
        IlMethod r = new IlMemberMethod(Il(m.DeclaringType!), m.Name, sig);
        if (m is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } mi) r = r.MakeGeneric([.. mi.GetGenericArguments().Select(Il)]);
        return r;
    }

    public IlField Field(FieldInfo f)
    {
        var def = f.Module.ResolveField(f.MetadataToken)!;
        return new IlMemberField(Il(f.DeclaringType!), f.Name, Sig(def.FieldType, def.GetRequiredCustomModifiers(), def.GetOptionalCustomModifiers()), f.IsStatic);
    }

    /// <summary>A type as it appears in an open member signature: generic parameters as `!i`/`!!i`, a generic type
    /// definition used inside its own members (`Vector&lt;T&gt; get_Zero()`) as an instantiation over its parameters.</summary>
    IlType Sig(Type t, Type[] required, Type[] optional)
    {
        var r = Sig(t);
        foreach (var m in optional.Reverse()) r = r.WithModifier(module.Import(m), required: false);
        foreach (var m in required.Reverse()) r = r.WithModifier(module.Import(m), required: true);
        return r;
    }

    IlType Sig(Type t)
    {
        if (t.IsByRef) return Sig(t.GetElementType()!).ByRef();
        if (t.IsPointer) return Sig(t.GetElementType()!).Pointer();
        if (t.IsSZArray) return Sig(t.GetElementType()!).SzArray();
        if (t.IsArray) return Sig(t.GetElementType()!).Array(t.GetArrayRank());
        if (t.IsGenericParameter) return t.DeclaringMethod != null ? IlType.MVar(t.GenericParameterPosition) : IlType.Var(t.GenericParameterPosition);
        if (t.IsGenericType && !t.IsFunctionPointer)
        {
            var defT = t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition();
            var entity = ((IlType.NamedType)module.Import(defT)).Entity;
            return IlType.Generic(entity, [.. t.GetGenericArguments().Select(Sig)]);
        }
        return Il(t);
    }

    // ------------------------------------------------------------------ facts

    public static bool IsIntegral(Type t) => t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) || t == typeof(short) || t == typeof(ushort) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(char) || t == typeof(nint) || t == typeof(nuint);
    public static bool IsUnsigned(Type t) => t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(byte) || t == typeof(char) || t == typeof(nuint);
    public static bool IsFloat(Type t) => t == typeof(float) || t == typeof(double);
    public static bool IsNumeric(Type t) => IsIntegral(t) || IsFloat(t);
    public static bool Is64(Type t) => t == typeof(long) || t == typeof(ulong);
    public static bool IsNullable(Type t) => Nullable.GetUnderlyingType(t) != null;

    static readonly Dictionary<Type, Type[]> Implicit = new()
    {
        [typeof(sbyte)] = [typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(nint), typeof(decimal)],
        [typeof(byte)] = [typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(nint), typeof(nuint), typeof(decimal)],
        [typeof(short)] = [typeof(int), typeof(long), typeof(float), typeof(double), typeof(nint), typeof(decimal)],
        [typeof(ushort)] = [typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(nint), typeof(nuint), typeof(decimal)],
        [typeof(int)] = [typeof(long), typeof(float), typeof(double), typeof(nint), typeof(decimal)],
        [typeof(uint)] = [typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(nuint), typeof(decimal)],
        [typeof(long)] = [typeof(float), typeof(double), typeof(decimal)],
        [typeof(ulong)] = [typeof(float), typeof(double), typeof(decimal)],
        [typeof(char)] = [typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(nint), typeof(nuint), typeof(decimal)],
        [typeof(float)] = [typeof(double)],
    };

    public static bool ImplicitNumeric(Type from, Type to) => Implicit.TryGetValue(from, out var tos) && tos.Contains(to);

    /// <summary>C# implicit constant conversion of an integer literal.</summary>
    public static bool LiteralFits(object? v, Type to)
    {
        if (v is not (int or long or uint or ulong)) return false;
        var big = v switch { int i => (System.Numerics.BigInteger)i, long l => l, uint u => u, ulong ul => ul, _ => 0 };
        if (to == typeof(int)) return big >= int.MinValue && big <= int.MaxValue;
        if (to == typeof(uint)) return big >= 0 && big <= uint.MaxValue;
        if (to == typeof(long)) return big >= long.MinValue && big <= long.MaxValue;
        if (to == typeof(ulong)) return big >= 0;
        if (to == typeof(short)) return big >= short.MinValue && big <= short.MaxValue;
        if (to == typeof(ushort)) return big >= 0 && big <= ushort.MaxValue;
        if (to == typeof(byte)) return big >= 0 && big <= byte.MaxValue;
        if (to == typeof(sbyte)) return big >= sbyte.MinValue && big <= sbyte.MaxValue;
        if (to == typeof(nint) || to == typeof(nuint)) return big >= int.MinValue && big <= int.MaxValue && (to == typeof(nint) || big >= 0);
        return to == typeof(float) || to == typeof(double) || to == typeof(decimal);
    }

    /// <summary>C# binary numeric promotion; an integer literal adopts the other operand's type when it fits.</summary>
    public static Type Promote(Type l, Type r, object? litL, object? litR)
    {
        if (l == r) return Widen(l);
        // Only an `int`-typed literal adopts the other operand's type (C# implicit constant conversion); a typed wide
        // literal (`1L`, `4294967295UL`) keeps its own type and promotes normally (review-r1 F1).
        if (litL is int && IsNumeric(r) && LiteralFits(litL, r) && !(IsIntegral(r) && Widen(r) != r)) return Widen(r);
        if (litR is int && IsNumeric(l) && LiteralFits(litR, l) && !(IsIntegral(l) && Widen(l) != l)) return Widen(l);
        if (l == typeof(decimal) || r == typeof(decimal)) return typeof(decimal);
        if (l == typeof(double) || r == typeof(double)) return typeof(double);
        if (l == typeof(float) || r == typeof(float)) return typeof(float);
        if (l == typeof(ulong) || r == typeof(ulong)) return typeof(ulong);
        if (l == typeof(long) || r == typeof(long)) return typeof(long);
        if (l == typeof(uint) && IsSignedSmall(r) || r == typeof(uint) && IsSignedSmall(l)) return typeof(long);
        if (l == typeof(uint) || r == typeof(uint)) return typeof(uint);
        return typeof(int);
    }

    static bool IsSignedSmall(Type t) => t == typeof(int) || t == typeof(short) || t == typeof(sbyte);
    public static Type Widen(Type t) => t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(char) ? typeof(int) : t;

    /// <summary>The type a base/interface clause of <paramref name="s"/> names (a generic instance resolves in the scope of <paramref name="s"/>).</summary>
    public Type? BaseOf(AliceT s, AType b)
    {
        var prev = Scope;
        Scope = s.Scope;
        try { return b.Sym != null && BySym.TryGetValue(b.Sym, out var bi) ? bi.Proxy : TryParse(b.Cs); }
        finally { Scope = prev; }
    }

    /// <summary>The generic stand-in of generic Alice type <paramref name="def"/> (arity of its type parameters): the pattern
    /// `Box&lt;!!0&gt;` that generic-method inference unifies against the instances.</summary>
    public Type MakeOpenProxy(AliceT def)
    {
        var tb = proxies.DefineType("G" + pn++ + "_" + def.Sym.Name, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Class);
        tb.DefineGenericParameters([.. def.Sym.TypeParams]);
        var t = tb.CreateType();
        OpenDefs[t] = def;
        return t;
    }

    public readonly Dictionary<Type, AliceT> OpenDefs = new();

    /// <summary>An instance proxy and what it derives from / implements, as instance proxies (for inference through bases and interfaces).</summary>
    IEnumerable<Type> AliceSupertypes(Type t)
    {
        var seen = new HashSet<Type>();
        var work = new Stack<Type>();
        work.Push(t);
        while (work.Count > 0)
        {
            var x = work.Pop();
            if (!seen.Add(x)) continue;
            yield return x;
            if (!ByProxy.TryGetValue(x, out var a)) continue;
            foreach (var b in a.Sym.Bases)
                if (BaseOf(a, b) is { } bt) work.Push(bt);
        }
    }

    /// <summary>Reference/boxing assignability, with Alice interfaces and base classes.</summary>
    public bool Assignable(Type from, Type to)
    {
        if (from == to || to == typeof(object)) return true;
        if (to.IsAssignableFrom(from)) return true;
        if (ByProxy.TryGetValue(from, out var a))
        {
            for (var s = a; s != null; s = s.Base)
            {
                if (s.ClrBase != null && to.IsAssignableFrom(s.ClrBase)) return true;
                foreach (var b in s.Sym.Bases)
                    if (BaseOf(s, b) is { } bt && (bt == to || Assignable(bt, to) || to.IsAssignableFrom(bt))) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ literals

    /// <summary>A C# literal as the lowerer prints it.</summary>
    public static bool TryLiteral(string text, out Type type, out object? value)
    {
        type = typeof(void);
        value = null;
        string s = text.Trim();
        if (s == "true" || s == "false") { type = typeof(bool); value = s == "true"; return true; }
        if (s == "null") { type = typeof(object); value = null; return true; }
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') { type = typeof(string); value = Unescape(s[1..^1]); return true; }
        if (s.Length >= 3 && s[0] == '@' && s[1] == '"' && s[^1] == '"') { type = typeof(string); value = s[2..^1].Replace("\"\"", "\""); return true; }
        if (s.Length >= 3 && s[0] == '\'' && s[^1] == '\'') { var u = Unescape(s[1..^1]); if (u.Length != 1) return false; type = typeof(char); value = u[0]; return true; }
        if (s.Length == 0) return false;
        bool neg = s[0] == '-';
        string b = (neg ? s[1..] : s).Replace("_", "");
        if (b.Length == 0 || !(char.IsDigit(b[0]) || b[0] == '.' && b.Length > 1 && char.IsDigit(b[1]))) return false;
        string lower = b.ToLowerInvariant();
        bool hex = lower.StartsWith("0x"), bin = lower.StartsWith("0b");
        if (!hex && !bin && (lower.EndsWith('f') || lower.EndsWith('d') || lower.EndsWith('m') || lower.Contains('.') || lower.Contains('e')))
        {
            char suf = lower[^1];
            string num = char.IsLetter(suf) ? b[..^1] : b;
            if (neg) num = "-" + num;
            if (suf == 'f') { if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false; type = typeof(float); value = f; return true; }
            if (suf == 'm') { if (!decimal.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return false; type = typeof(decimal); value = m; return true; }
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd)) return false;
            type = typeof(double); value = dd; return true;
        }
        string suffix = "";
        while (lower.Length > 0 && (lower[^1] == 'u' || lower[^1] == 'l')) { suffix = lower[^1] + suffix; lower = lower[..^1]; }
        System.Numerics.BigInteger v;
        if (hex) { if (!ulong.TryParse(lower[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return false; v = h; }
        else if (bin) { try { v = Convert.ToUInt64(lower[2..], 2); } catch (FormatException) { return false; } }
        else if (!System.Numerics.BigInteger.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return false;
        if (neg) v = -v;
        bool u8 = suffix.Contains('u'), l8 = suffix.Contains('l');
        if (!u8 && !l8 && v >= int.MinValue && v <= int.MaxValue) { type = typeof(int); value = (int)v; return true; }
        if (!l8 && v >= 0 && v <= uint.MaxValue) { type = typeof(uint); value = (uint)v; return true; }
        if (!u8 && v >= long.MinValue && v <= long.MaxValue) { type = typeof(long); value = (long)v; return true; }
        if (v >= 0 && v <= ulong.MaxValue) { type = typeof(ulong); value = (ulong)v; return true; }
        return false;
    }

    static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'e': sb.Append('\u001b'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                case 'U': sb.Append(char.ConvertFromUtf32(Convert.ToInt32(s.Substring(i + 1, 8), 16))); i += 8; break;
                case 'x':
                    {
                        int n = 0;
                        while (n < 4 && i + 1 + n < s.Length && Uri.IsHexDigit(s[i + 1 + n])) n++;
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, n), 16));
                        i += n;
                        break;
                    }
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ methods

    static readonly Assembly[] ExtensionAssemblies =
        [typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(Alice.Runtime.Prelude).Assembly, typeof(System.Collections.Generic.LinkedList<>).Assembly];

    Dictionary<string, List<MethodInfo>>? extensions;

    /// <summary>Extension methods visible through the default usings, by name.</summary>
    public IEnumerable<MethodInfo> ExtensionsNamed(string name)
    {
        if (extensions == null)
        {
            extensions = new(StringComparer.Ordinal);
            var ns = Usings.ToHashSet();
            foreach (var asm in ExtensionAssemblies.Distinct())
            {
                Type[] ts;
                try { ts = asm.GetExportedTypes(); } catch (Exception) { continue; }
                foreach (var t in ts)
                {
                    if (!(t.IsAbstract && t.IsSealed) || t.IsGenericType || t.Namespace == null || !ns.Contains(t.Namespace)) continue;
                    if (!t.IsDefined(typeof(ExtensionAttribute), false)) continue;
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.IsDefined(typeof(ExtensionAttribute), false))
                        {
                            if (!extensions.TryGetValue(m.Name, out var l)) extensions[m.Name] = l = [];
                            l.Add(m);
                        }
                }
            }
        }
        return extensions.GetValueOrDefault(name) ?? [];
    }

    static readonly MethodInfo[] PreludeStatics = typeof(Alice.Runtime.Prelude).GetMethods(BindingFlags.Public | BindingFlags.Static);

    public static IEnumerable<MethodInfo> PreludeNamed(string name) => PreludeStatics.Where(m => m.Name == name);

    /// <summary>Binds generic parameters of <paramref name="p"/> from <paramref name="a"/> (T, T[], C&lt;T&gt; through bases and interfaces).</summary>
    public void Unify(Type p, Type a, Type?[] bound)
    {
        if (p.IsByRef) p = p.GetElementType()!;
        if (a.IsByRef) a = a.GetElementType()!;
        if (p.IsGenericType && !p.IsGenericTypeDefinition && OpenDefs.TryGetValue(p.GetGenericTypeDefinition(), out var gd))
        {
            // Box<!!0> against an instance of generic Alice type Box (or a type deriving from / implementing one)
            var pargs = p.GetGenericArguments();
            foreach (var s in AliceSupertypes(a))
                if (ByProxy.TryGetValue(s, out var inst) && inst.GenericDef == gd)
                {
                    for (int i = 0; i < pargs.Length; i++) Unify(pargs[i], inst.TypeArgs[i], bound);
                    return;
                }
            return;
        }
        if (p.IsGenericParameter)
        {
            if (p.DeclaringMethod != null) bound[p.GenericParameterPosition] ??= a;
            return;
        }
        if (p.IsArray && a.IsArray && p.GetArrayRank() == a.GetArrayRank()) { Unify(p.GetElementType()!, a.GetElementType()!, bound); return; }
        if (!p.IsGenericType || !p.ContainsGenericParameters) return;
        var pd = p.GetGenericTypeDefinition();
        var pa = p.GetGenericArguments();
        foreach (var c in Supertypes(a))
            if (c.IsGenericType && c.GetGenericTypeDefinition() == pd)
            {
                var ca = c.GetGenericArguments();
                for (int i = 0; i < pa.Length; i++) Unify(pa[i], ca[i], bound);
                return;
            }
    }

    static IEnumerable<Type> Supertypes(Type t)
    {
        for (var b = t; b != null; b = b.BaseType) yield return b;
        if (t.IsArray && t.GetArrayRank() == 1)
        {
            var e = t.GetElementType()!;
            yield return typeof(IEnumerable<>).MakeGenericType(e);
            yield return typeof(IList<>).MakeGenericType(e);
            yield return typeof(IReadOnlyList<>).MakeGenericType(e);
            yield return typeof(ICollection<>).MakeGenericType(e);
            yield return typeof(IReadOnlyCollection<>).MakeGenericType(e);
        }
        Type[] ifs;
        try { ifs = t.GetInterfaces(); } catch (Exception) { ifs = []; }
        foreach (var i in ifs) yield return i;
    }

    /// <summary>How well an argument converts to a parameter (higher is better; int.MinValue: not at all).</summary>
    public int Score(Type pt, ArgInfo a)
    {
        if (a.Prefix.StartsWith("out") || a.Prefix.StartsWith("ref") || a.Prefix.StartsWith("in "))
            return pt.IsByRef && (a.Prefix.StartsWith("out var") || pt.GetElementType() == a.Type) ? 6 : int.MinValue;
        if (a.Type == LambdaArg)
            return IsDelegate(pt) && a.Literal is AirDelegateCreate dc && pt.GetMethod("Invoke")?.GetParameters().Length == dc.Method.Params.Count ? 4 : int.MinValue;
        if (pt.IsByRef)
        {
            var el = pt.GetElementType()!;   // `in` parameter: the argument is passed by reference implicitly
            return el == a.Type ? 5 : int.MinValue;
        }
        if (pt == a.Type) return 6;
        if (a.IsNull) return !pt.IsValueType || IsNullable(pt) ? 3 : int.MinValue;
        if (a.Literal != null && IsNumeric(pt) && LiteralFits(a.Literal, pt) && IsIntegral(a.Type)) return 5;
        if (ImplicitNumeric(a.Type, pt)) return IsIntegral(pt) == IsIntegral(a.Type) ? 4 : 3;
        if (IsNullable(pt) && (Nullable.GetUnderlyingType(pt) == a.Type || ImplicitNumeric(a.Type, Nullable.GetUnderlyingType(pt)!))) return 3;
        if (pt.IsGenericParameter) return 2;
        if (!pt.IsValueType && !pt.IsPointer && Assignable(a.Type, pt)) return a.Type.IsValueType ? 1 : 2;
        if (ImplicitOp(a.Type, pt) != null) return 1;
        return int.MinValue;
    }

    /// <summary>A user-defined implicit conversion operator from -> to (string -> ReadOnlySpan&lt;char&gt;, T[] -> Span&lt;T&gt;).</summary>
    public static MethodInfo? ImplicitOp(Type from, Type to)
    {
        if (from.IsPointer || to.IsPointer || from == typeof(void)) return null;
        foreach (var host in new[] { to, from })
        {
            if (host.IsGenericParameter || host.IsArray && host != from) continue;
            MethodInfo[] ms;
            try { ms = host.GetMethods(BindingFlags.Public | BindingFlags.Static); } catch (Exception) { continue; }
            foreach (var m in ms)
                if (m.Name == "op_Implicit" && m.ReturnType == to && m.GetParameters() is [var p] && (p.ParameterType == from))
                    return m;
        }
        return null;
    }

    /// <summary>C#-like overload resolution over <paramref name="cands"/>; null when nothing applies.</summary>
    public Cand? Pick(IEnumerable<Cand> cands, IReadOnlyList<ArgInfo> args)
    {
        Cand? best = null;
        long bestScore = long.MinValue;
        bool ambiguous = false;
        foreach (var c0 in cands)
        {
            foreach (var c in Expand(c0, args))
            {
                var ps = c.Params;
                int n = ps.Length - c.Optional;
                if (n != args.Count) continue;
                long score = 0;
                for (int i = 0; i < n; i++)
                {
                    int s = Score(ps[i], args[i]);
                    if (s == int.MinValue) { score = long.MinValue; break; }
                    score += s;
                }
                if (score == long.MinValue) continue;
                bool generic = c.Clr is MethodInfo { IsGenericMethod: true };
                score = score * 8 - (generic ? 2 : 0) - (c.Expanded ? 4 : 0) - c.Optional;
                if (score > bestScore) { best = c; bestScore = score; ambiguous = false; }
                else if (score == bestScore) ambiguous = true;
            }
        }
        _ = ambiguous;   // first declared wins on a tie (C# would report CS0121; the binder accepted the call)
        return best;
    }

    /// <summary>A candidate in its applicable forms: generic methods closed by inference, optional parameters
    /// defaulted, `params` arrays expanded.</summary>
    IEnumerable<Cand> Expand(Cand c, IReadOnlyList<ArgInfo> args)
    {
        if (c.Alice is { GenericParams: { } tps } template)
        {
            // an Alice generic method: infer its type arguments like a CLR one, then use the monomorphic instance
            var bound = new Type?[tps.Length];
            for (int i = 0; i < Math.Min(c.Params.Length, args.Count); i++)
                if (!args[i].IsNull && args[i].Type != LambdaArg) Unify(c.Params[i], args[i].Type, bound);
            for (int i = 0; i < Math.Min(c.Params.Length, args.Count) && bound.Any(b => b == null) && LambdaReturn != null; i++)
            {
                if (args[i].Type != LambdaArg || !IsDelegate(c.Params[i])) continue;
                var linv = c.Params[i].GetMethod("Invoke");
                if (linv == null || !linv.ReturnType.ContainsGenericParameters) continue;
                var lps = linv.GetParameters().Select(p => Subst(p.ParameterType, bound)).ToArray();
                if (lps.Any(p => p == null)) continue;
                if (LambdaReturn(args[i].Literal!, lps!) is { } r && r != typeof(void)) Unify(linv.ReturnType, r, bound);
            }
            if (bound.Any(b => b == null) || InstantiateMethod!(template, bound!) is not { } inst) yield break;
            c = c with { Alice = inst, Params = inst.Params, Ret = inst.Ret };
        }
        if (c.Clr is MethodInfo { IsGenericMethodDefinition: true } gm)
        {
            var ps0 = gm.GetParameters();
            var bound = new Type?[gm.GetGenericArguments().Length];
            int k = c.Extension ? 0 : 0;
            for (int i = 0; i < Math.Min(ps0.Length, args.Count); i++)
            {
                if (args[i].IsNull || args[i].Type == LambdaArg) continue;
                var pt = ps0[i].ParameterType;
                if (ps0[i].IsDefined(typeof(ParamArrayAttribute)) && pt.IsArray && !(args[i].Type.IsArray && args.Count == ps0.Length)) pt = pt.GetElementType()!;
                Unify(pt, args[i].Type, bound);
            }
            _ = k;
            // generic parameters only a lambda's result binds (`par_sum<T, R>(T[], Func<T, R>)`)
            for (int i = 0; i < Math.Min(ps0.Length, args.Count) && bound.Any(b => b == null) && LambdaReturn != null; i++)
            {
                if (args[i].Type != LambdaArg || !IsDelegate(ps0[i].ParameterType)) continue;
                var inv = ps0[i].ParameterType.GetMethod("Invoke");
                if (inv == null || !inv.ReturnType.ContainsGenericParameters) continue;
                var lps = inv.GetParameters().Select(p => Subst(p.ParameterType, bound)).ToArray();
                if (lps.Any(p => p == null)) continue;
                if (LambdaReturn(args[i].Literal!, lps!) is { } r && r != typeof(void)) Unify(inv.ReturnType, r, bound);
            }
            if (bound.Any(b => b == null)) yield break;
            MethodInfo closed;
            try { closed = gm.MakeGenericMethod(bound!); }
            catch (ArgumentException) { yield break; }
            c = c with { Clr = closed, Params = [.. closed.GetParameters().Select(p => p.ParameterType)], Ret = closed.ReturnType };
        }
        var info = c.Clr?.GetParameters();
        var ps = c.Params;
        // optional parameters: drop trailing ones the call does not pass
        int optional = 0;
        if (args.Count < ps.Length)
        {
            optional = ps.Length - args.Count;
            for (int i = args.Count; i < ps.Length; i++)
            {
                bool hasDefault = info != null ? info[i].HasDefaultValue || info[i].IsOptional : c.Alice?.HasDefault?[i] == true;
                bool isParams = info != null && i == ps.Length - 1 && info[i].IsDefined(typeof(ParamArrayAttribute)) || c.Alice?.ParamsArray == true && i == ps.Length - 1;
                if (!hasDefault && !isParams) { optional = -1; break; }
            }
            if (optional > 0)
            {
                var defs = new object?[optional];
                for (int i = 0; i < optional; i++)
                {
                    int pi = args.Count + i;
                    defs[i] = info != null ? (info[pi].HasDefaultValue ? info[pi].DefaultValue : ParamsMarker) : c.Alice?.Defaults?[pi];
                    if (info == null && c.Alice?.ParamsArray == true && pi == ps.Length - 1) defs[i] = ParamsMarker;
                }
                yield return c with { Optional = optional, DefaultsTail = defs };
            }
        }
        bool lastParams = ps.Length > 0 && (info != null && info[^1].IsDefined(typeof(ParamArrayAttribute)) || c.Alice?.ParamsArray == true);
        if (lastParams && args.Count >= ps.Length - 1)
        {
            var el = ps[^1].GetElementType()!;
            bool normal = args.Count == ps.Length && (args[^1].Type == ps[^1] || args[^1].IsNull || Assignable(args[^1].Type, ps[^1]));
            if (!normal)
            {
                var exp = ps.Take(ps.Length - 1).Concat(Enumerable.Repeat(el, args.Count - ps.Length + 1)).ToArray();
                yield return c with { Params = exp, Expanded = true };
                yield break;
            }
        }
        if (args.Count == ps.Length) yield return c;
    }

    /// <summary>The default of an omitted `params` parameter: an empty array.</summary>
    public static readonly object ParamsMarker = new();

    public static Cand FromClr(MethodBase m, bool extension = false) =>
        new(m, null, [.. m.GetParameters().Select(p => p.ParameterType)], m is MethodInfo mi ? mi.ReturnType : m.DeclaringType!, m.IsStatic, false, 0, [], extension);

    public static Cand FromAlice(AMethod m) => new(null, m, m.Params, m.IsCtor ? m.Owner.Proxy : m.Ret, m.IsStatic, false, 0, [], m.IsExtension);
}
