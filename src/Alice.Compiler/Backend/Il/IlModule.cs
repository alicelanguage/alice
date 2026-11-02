using System.Reflection;
using System.Reflection.Metadata;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>A named type a token or signature can point at: defined here (<see cref="IlTypeDef"/>) or referenced
/// from another assembly (<see cref="IlExternalType"/>).</summary>
public abstract class IlTypeEntity
{
    IlType? type;

    private protected IlTypeEntity() { }

    public abstract string Namespace { get; }
    public abstract string Name { get; }
    public abstract bool IsValueType { get; }

    /// <summary>This type used as a (non-generic) type.</summary>
    public IlType Type => type ??= IlType.Named(this);

    /// <summary>`Def&lt;args&gt;`.</summary>
    public IlType Instantiate(params IlType[] args) => IlType.Generic(this, args);

    public override string ToString() => Namespace.Length > 0 ? Namespace + "." + Name : Name;
}

/// <summary>An AssemblyRef row: identity of a referenced assembly (from the ref pack for framework types).</summary>
public sealed class IlAssemblyRef
{
    internal IlAssemblyRef(string name, Version version, string culture, byte[] publicKeyToken)
    {
        Name = name; Version = version; Culture = culture; PublicKeyToken = publicKeyToken;
    }

    public string Name { get; }
    public Version Version { get; }
    public string Culture { get; }
    public byte[] PublicKeyToken { get; }
    public override string ToString() => $"{Name}, Version={Version}";
}

/// <summary>A TypeRef: a type in another assembly, or nested in such a type.</summary>
public sealed class IlExternalType : IlTypeEntity
{
    readonly string ns, name;
    readonly bool valueType;

    internal IlExternalType(IlAssemblyRef? assembly, IlExternalType? enclosing, string ns, string name, bool valueType)
    {
        Assembly = assembly; Enclosing = enclosing; this.ns = ns; this.name = name; this.valueType = valueType;
    }

    public IlAssemblyRef? Assembly { get; }
    public IlExternalType? Enclosing { get; }
    public override string Namespace => ns;
    public override string Name => name;
    public override bool IsValueType => valueType;

    /// <summary>The reflection full name without assembly: `Ns.Outer+Inner`.</summary>
    public string FullName => Enclosing != null ? Enclosing.FullName + "+" + name : ns.Length > 0 ? ns + "." + name : name;

    /// <summary>The outermost type's assembly.</summary>
    public IlAssemblyRef? RootAssembly => Enclosing != null ? Enclosing.RootAssembly : Assembly;

    /// <summary>The name used in custom attribute blobs: `Ns.Outer+Inner, Assembly`.</summary>
    public string SerializedName => RootAssembly != null ? FullName + ", " + RootAssembly.Name : FullName;

    public override string ToString() => Enclosing != null ? Enclosing + "/" + name : "[" + Assembly?.Name + "]" + base.ToString();
}

/// <summary>A custom attribute: constructor, fixed arguments (in constructor parameter order) and named arguments.
/// Values: primitives, strings, enums (or their underlying number), <see cref="System.Type"/> or type-name strings
/// for `System.Type` parameters, arrays for SZ-array parameters, boxed values for `object` parameters.</summary>
public sealed record IlCustomAttribute(IlMethod Constructor, IReadOnlyList<object?> Arguments, IReadOnlyList<IlNamedArgument>? Named = null);

public sealed record IlNamedArgument(string Name, IlType Type, object? Value, bool IsField = false);

/// <summary>Something that carries custom attributes.</summary>
public abstract class IlAttributed
{
    public List<IlCustomAttribute> CustomAttributes { get; } = [];

    public void AddCustomAttribute(IlMethod constructor, params object?[] arguments) => CustomAttributes.Add(new IlCustomAttribute(constructor, arguments));
}

public sealed class IlGenericParam : IlAttributed
{
    internal IlGenericParam(string name, int index, bool method, GenericParameterAttributes attributes)
    {
        Name = name; Index = index; IsMethodParameter = method; Attributes = attributes;
    }

    public string Name { get; }
    public int Index { get; }
    public bool IsMethodParameter { get; }
    /// <summary>Variance and special constraints (class, struct, new()).</summary>
    public GenericParameterAttributes Attributes { get; set; }
    public List<IlType> Constraints { get; } = [];
    /// <summary>`!i` or `!!i`.</summary>
    public IlType Type => IsMethodParameter ? IlType.MVar(Index) : IlType.Var(Index);
}

/// <summary>A TypeDef row with its members.</summary>
public sealed class IlTypeDef : IlTypeEntity
{
    readonly string ns, name;

    internal IlTypeDef(IlModule module, string ns, string name, TypeAttributes attributes, IlType? baseType, IlTypeDef? enclosing)
    {
        Module = module; this.ns = ns; this.name = name; Attributes = attributes; BaseType = baseType; Enclosing = enclosing;
    }

    public IlModule Module { get; }
    public override string Namespace => ns;
    public override string Name => name;
    public TypeAttributes Attributes { get; set; }
    public IlType? BaseType { get; set; }
    public IlTypeDef? Enclosing { get; }

    public override bool IsValueType => BaseType is IlType.NamedType { Entity: IlExternalType { Namespace: "System", Name: "ValueType" or "Enum", Enclosing: null } }
        && !(ns == "System" && name == "Enum");

    public List<IlFieldDef> Fields { get; } = [];
    public List<IlMethodDef> Methods { get; } = [];
    public List<IlGenericParam> GenericParameters { get; } = [];
    public List<IlType> Interfaces { get; } = [];
    public List<IlProperty> Properties { get; } = [];
    public List<IlEvent> Events { get; } = [];
    public List<IlTypeDef> NestedTypes { get; } = [];
    public List<(IlMethodDef Body, IlMethod Declaration)> MethodOverrides { get; } = [];
    public List<IlCustomAttribute> CustomAttributes { get; } = [];
    /// <summary>ClassLayout row (struct packing/size); both null = none.</summary>
    public ushort? PackingSize { get; set; }
    public uint? ClassSize { get; set; }

    /// <summary>The type instantiated over its own parameters (`C&lt;!0, !1&gt;`), or the plain type.</summary>
    public IlType SelfInstance => GenericParameters.Count == 0 ? Type : Instantiate([.. GenericParameters.Select(g => g.Type)]);

    public IlGenericParam DefineGenericParameter(string name, GenericParameterAttributes attributes = GenericParameterAttributes.None)
    {
        var g = new IlGenericParam(name, GenericParameters.Count, false, attributes);
        GenericParameters.Add(g);
        return g;
    }

    public IlFieldDef DefineField(string name, IlType type, FieldAttributes attributes, object? constant = null)
    {
        var f = new IlFieldDef(this, name, type, attributes, constant);
        Fields.Add(f);
        return f;
    }

    /// <summary>A method; instance unless <paramref name="attributes"/> has Static. For a generic method name its
    /// parameters in <paramref name="genericParameters"/> and spell them `IlType.MVar(i)` in the signature.</summary>
    public IlMethodDef DefineMethod(string name, MethodAttributes attributes, IlType returnType, IlType[]? parameters = null,
        string[]? genericParameters = null, MethodImplAttributes implAttributes = MethodImplAttributes.IL)
    {
        var gps = genericParameters ?? [];
        var sig = new MethodSig(returnType, parameters ?? [], isInstance: (attributes & MethodAttributes.Static) == 0, genericArity: gps.Length);
        var m = new IlMethodDef(this, name, attributes, implAttributes, sig);
        for (int i = 0; i < gps.Length; i++) m.GenericParameters.Add(new IlGenericParam(gps[i], i, true, GenericParameterAttributes.None));
        Methods.Add(m);
        return m;
    }

    /// <summary>An instance constructor `.ctor` (or the static `.cctor` when <paramref name="isStatic"/>).</summary>
    public IlMethodDef DefineConstructor(MethodAttributes attributes, IlType[]? parameters = null, bool isStatic = false) =>
        DefineMethod(isStatic ? ".cctor" : ".ctor",
            attributes | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | (isStatic ? MethodAttributes.Static : 0),
            IlType.Void, isStatic ? [] : parameters);

    public IlTypeDef DefineNestedType(string name, TypeAttributes attributes, IlType? baseType = null)
    {
        var t = Module.NewType("", name, attributes, baseType, this);
        NestedTypes.Add(t);
        return t;
    }

    public IlProperty DefineProperty(string name, IlType type, IlMethodDef? getter, IlMethodDef? setter, PropertyAttributes attributes = PropertyAttributes.None)
    {
        var p = new IlProperty(name, type, getter, setter, attributes);
        Properties.Add(p);
        return p;
    }

    public IlEvent DefineEvent(string name, IlType handlerType, IlMethodDef add, IlMethodDef remove, EventAttributes attributes = EventAttributes.None)
    {
        var e = new IlEvent(name, handlerType, add, remove, attributes);
        Events.Add(e);
        return e;
    }

    public void AddCustomAttribute(IlMethod constructor, params object?[] arguments) => CustomAttributes.Add(new IlCustomAttribute(constructor, arguments));
}

/// <summary>A field operand: <see cref="IlFieldDef"/> or a MemberRef (<see cref="IlMemberField"/>).</summary>
public abstract class IlField
{
    private protected IlField() { }
    public abstract string Name { get; }
    public abstract IlType Type { get; }
    public abstract bool IsStatic { get; }
}

public sealed class IlFieldDef : IlField
{
    readonly string name;
    readonly IlType type;
    internal readonly List<IlCustomAttribute> attributes = [];

    internal IlFieldDef(IlTypeDef owner, string name, IlType type, FieldAttributes attrs, object? constant)
    {
        Owner = owner; this.name = name; this.type = type; Attributes = attrs; Constant = constant;
        if (constant != null) Attributes |= FieldAttributes.HasDefault;
    }

    public IlTypeDef Owner { get; }
    public override string Name => name;
    public override IlType Type => type;
    public override bool IsStatic => (Attributes & FieldAttributes.Static) != 0;
    public FieldAttributes Attributes { get; set; }
    /// <summary>The Constant row of a literal (const/enum member).</summary>
    public object? Constant { get; }
    public List<IlCustomAttribute> CustomAttributes => attributes;

    /// <summary>The field of a generic owner on an instantiation (`ldfld !0 C&lt;int&gt;::f`).</summary>
    public IlField On(IlType instance) => new IlMemberField(instance, name, type, IsStatic);

    public override string ToString() => Owner + "::" + name;
}

public sealed class IlMemberField(IlType parent, string name, IlType type, bool isStatic) : IlField
{
    public IlType Parent { get; } = parent;
    public override string Name => name;
    public override IlType Type => type;
    public override bool IsStatic => isStatic;
    public override string ToString() => Parent + "::" + name;
}

/// <summary>A method operand: <see cref="IlMethodDef"/>, a MemberRef (<see cref="IlMemberMethod"/>) or a
/// generic instantiation (<see cref="IlMethodInstance"/>, a MethodSpec).</summary>
public abstract class IlMethod
{
    private protected IlMethod() { }
    public abstract string Name { get; }
    public abstract MethodSig Signature { get; }
    public IlMethod MakeGeneric(params IlType[] args) => new IlMethodInstance(this, args);
}

public sealed class IlParam : IlAttributed
{
    internal IlParam(int sequence, string? name, ParameterAttributes attributes) { Sequence = sequence; Name = name; Attributes = attributes; }
    /// <summary>1-based position (0 = the return value).</summary>
    public int Sequence { get; }
    public string? Name { get; set; }
    public ParameterAttributes Attributes { get; set; }
    public object? DefaultValue { get; set; }
}

public sealed class IlMethodDef : IlMethod
{
    readonly string name;
    readonly MethodSig sig;
    IlBuilder? body;

    internal IlMethodDef(IlTypeDef owner, string name, MethodAttributes attrs, MethodImplAttributes impl, MethodSig sig)
    {
        Owner = owner; this.name = name; Attributes = attrs; ImplAttributes = impl; this.sig = sig;
    }

    public IlTypeDef Owner { get; }
    public override string Name => name;
    public override MethodSig Signature => sig;
    public MethodAttributes Attributes { get; set; }
    public MethodImplAttributes ImplAttributes { get; set; }
    public List<IlGenericParam> GenericParameters { get; } = [];
    public List<IlParam> Parameters { get; } = [];
    public List<IlCustomAttribute> CustomAttributes { get; } = [];
    public bool IsStatic => (Attributes & MethodAttributes.Static) != 0;

    /// <summary>The body (created on first use). Abstract and runtime-implemented methods have none.</summary>
    public IlBuilder Il => body ??= (Attributes & MethodAttributes.Abstract) != 0
        ? throw new InvalidOperationException("abstract method " + this + " has no body")
        : new IlBuilder(sig.ReturnsValue, Owner.Module);

    internal IlBuilder? Body => body;

    /// <summary>Names (and flags) parameter <paramref name="sequence"/> (1-based; 0 is the return value).</summary>
    public IlParam DefineParameter(int sequence, string? name, ParameterAttributes attributes = ParameterAttributes.None)
    {
        if (sequence < 0 || sequence > sig.Parameters.Length) throw new ArgumentOutOfRangeException(nameof(sequence));
        var p = Parameters.FirstOrDefault(x => x.Sequence == sequence);
        if (p == null) { p = new IlParam(sequence, name, attributes); Parameters.Add(p); }
        else { p.Name = name; p.Attributes = attributes; }
        return p;
    }

    /// <summary>The method of a generic owner on an instantiation (a MemberRef on the TypeSpec).</summary>
    public IlMethod On(IlType instance) => new IlMemberMethod(instance, name, sig);

    public override string ToString() => Owner + "::" + name;
}

public sealed class IlMemberMethod(IlType parent, string name, MethodSig signature) : IlMethod
{
    public IlType Parent { get; } = parent;
    public override string Name => name;
    public override MethodSig Signature => signature;
    public override string ToString() => Parent + "::" + name;
}

public sealed class IlMethodInstance : IlMethod
{
    internal IlMethodInstance(IlMethod generic, IReadOnlyList<IlType> args)
    {
        if (generic.Signature.GenericArity != args.Count) throw new ArgumentException($"{generic} takes {generic.Signature.GenericArity} type arguments, not {args.Count}");
        Generic = generic; Args = [.. args];
    }

    public IlMethod Generic { get; }
    public IReadOnlyList<IlType> Args { get; }
    public override string Name => Generic.Name;
    public override MethodSig Signature => Generic.Signature;
    public override string ToString() => Generic + "<" + string.Join(", ", Args) + ">";
}

public sealed class IlProperty(string name, IlType type, IlMethodDef? getter, IlMethodDef? setter, PropertyAttributes attributes) : IlAttributed
{
    public string Name { get; } = name;
    public IlType Type { get; } = type;
    public IlMethodDef? Getter { get; } = getter;
    public IlMethodDef? Setter { get; } = setter;
    public PropertyAttributes Attributes { get; } = attributes;
    internal bool IsInstance => !((Getter ?? Setter)?.IsStatic ?? false);
    /// <summary>Index parameters (the getter's parameters).</summary>
    internal IReadOnlyList<IlType> IndexParameters => Getter != null ? Getter.Signature.Parameters : Setter != null ? Setter.Signature.Parameters[..^1] : [];
}

public sealed class IlEvent(string name, IlType handlerType, IlMethodDef add, IlMethodDef remove, EventAttributes attributes) : IlAttributed
{
    public string Name { get; } = name;
    public IlType HandlerType { get; } = handlerType;
    public IlMethodDef Add { get; } = add;
    public IlMethodDef Remove { get; } = remove;
    public IlMethodDef? Raise { get; set; }
    public EventAttributes Attributes { get; } = attributes;
}

/// <summary>A source document of the PDB. With <see cref="Source"/> the PDB stores its SHA-256 (and the text
/// itself when <see cref="Embed"/>).</summary>
public sealed class IlDocument
{
    /// <summary>The language GUID Alice documents are tagged with.</summary>
    public static readonly Guid AliceLanguage = new("a11ce0de-5a1c-4e6b-9d2f-0000a11ce001");

    internal IlDocument(string path, string? source, bool embed) { Path = path; Source = source; Embed = embed; }

    public string Path { get; }
    public string? Source { get; set; }
    public bool Embed { get; set; }
    public Guid Language { get; set; } = AliceLanguage;
}

/// <summary>
/// The IL backend facade (ADR 0033): `new IlModule(name)` -> <see cref="DefineType"/> -> <see cref="IlTypeDef.DefineMethod"/>
/// -> <see cref="IlMethodDef.Il"/> (an <see cref="IlBuilder"/>) ... -> <see cref="Write"/>(pe, pdb). References to
/// framework types resolve against the targeting pack (<see cref="RefPack"/>), never the host's implementation
/// assemblies. Output is deterministic: tables follow definition order and the MVID/PDB id are content hashes.
/// </summary>
public sealed class IlModule
{
    readonly List<IlTypeDef> topLevel = [];
    readonly List<IlTypeDef> allTypes = [];
    readonly Dictionary<string, IlAssemblyRef> assemblies = new(StringComparer.Ordinal);
    readonly Dictionary<string, IlExternalType> externals = new(StringComparer.Ordinal);
    readonly List<IlDocument> documents = [];

    public IlModule(string name, Version? version = null, RefPack? references = null)
    {
        Name = name;
        Version = version ?? new Version(1, 0, 0, 0);
        References = references ?? RefPack.Default;
    }

    public string Name { get; }
    public Version Version { get; }
    public RefPack References { get; }

    /// <summary>Static `Main`; setting it makes the module an executable (`.exe` module name, CUI subsystem).</summary>
    public IlMethodDef? EntryPoint { get; set; }

    /// <summary>Source Link JSON stored in the PDB (`{"documents": {"/src/*": "https://.../*"}}`).</summary>
    public string? SourceLinkJson { get; set; }

    /// <summary>Assembly-level custom attributes.</summary>
    public List<IlCustomAttribute> CustomAttributes { get; } = [];

    /// <summary>Top-level types in definition order.</summary>
    public IReadOnlyList<IlTypeDef> Types => topLevel;

    /// <summary>Every type, nested ones right after their enclosing type (TypeDef row order).</summary>
    internal IEnumerable<IlTypeDef> TypeDefOrder()
    {
        IEnumerable<IlTypeDef> Walk(IlTypeDef t) => t.NestedTypes.SelectMany(Walk).Prepend(t);
        return topLevel.SelectMany(Walk);
    }

    public IReadOnlyList<IlDocument> Documents => documents;

    public IlTypeDef DefineType(string ns, string name, TypeAttributes attributes, IlType? baseType = null)
    {
        var t = NewType(ns, name, attributes, baseType, null);
        topLevel.Add(t);
        return t;
    }

    internal IlTypeDef NewType(string ns, string name, TypeAttributes attributes, IlType? baseType, IlTypeDef? enclosing)
    {
        if ((attributes & TypeAttributes.Interface) == 0) baseType ??= CoreType("Object");
        var t = new IlTypeDef(this, ns, name, attributes, baseType, enclosing);
        allTypes.Add(t);
        return t;
    }

    /// <summary>A value type (`sealed`, base System.ValueType).</summary>
    public IlTypeDef DefineStruct(string ns, string name, TypeAttributes attributes = TypeAttributes.Public) =>
        DefineType(ns, name, attributes | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit, CoreType("ValueType"));

    // ---------------------------------------------------------------- documents

    /// <summary>The document for <paramref name="path"/>, added on first use.</summary>
    public IlDocument Document(string path)
    {
        foreach (var d in documents) if (d.Path == path) return d;
        return AddDocument(path);
    }

    public IlDocument AddDocument(string path, string? source = null, bool embed = false)
    {
        if (documents.Any(d => d.Path == path)) throw new ArgumentException("document " + path + " already added");
        var d = new IlDocument(path, source, embed);
        documents.Add(d);
        return d;
    }

    // ---------------------------------------------------------------- references

    /// <summary>A framework type by name, resolved in the ref pack (its assembly and value-typeness), or in
    /// <paramref name="assembly"/> when given.</summary>
    public IlType TypeRef(string ns, string name, IlAssemblyRef? assembly = null, bool? isValueType = null) => External(ns, name, assembly, isValueType).Type;

    /// <summary>A TypeRef entity (for generic definitions: `TypeRefEntity("System.Collections.Generic", "List`1")`).</summary>
    public IlExternalType External(string ns, string name, IlAssemblyRef? assembly = null, bool? isValueType = null)
    {
        string full = ns.Length > 0 ? ns + "." + name : name;
        if (assembly == null)
        {
            if (!References.TryFind(full, out var e)) throw new ArgumentException($"{full} is not in the reference pack {References.Directory}");
            assembly = AssemblyRef(e.Assembly.Name, e.Assembly.Version, e.Assembly.Culture, e.Assembly.PublicKeyToken);
            isValueType ??= e.IsValueType;
        }
        string key = assembly.Name + "|" + full;
        if (externals.TryGetValue(key, out var t)) return t;
        return externals[key] = new IlExternalType(assembly, null, ns, name, isValueType ?? false);
    }

    /// <summary>A type nested in a referenced type.</summary>
    public IlExternalType NestedExternal(IlExternalType enclosing, string name, bool? isValueType = null)
    {
        string full = enclosing.FullName + "+" + name;
        string key = enclosing.RootAssembly?.Name + "|" + full;
        if (externals.TryGetValue(key, out var t)) return t;
        if (isValueType == null && References.TryFind(full, out var e)) isValueType = e.IsValueType;
        return externals[key] = new IlExternalType(null, enclosing, "", name, isValueType ?? false);
    }

    /// <summary>An AssemblyRef (one per name).</summary>
    public IlAssemblyRef AssemblyRef(string name, Version version, string culture = "", byte[]? publicKeyToken = null)
    {
        if (assemblies.TryGetValue(name, out var a)) return a;
        return assemblies[name] = new IlAssemblyRef(name, version, culture, publicKeyToken ?? []);
    }

    /// <summary>`System.X` from the core assembly (System.Runtime in the ref pack).</summary>
    public IlType CoreType(string name) => TypeRef("System", name);

    /// <summary>A method on any type by name and signature (a MemberRef).</summary>
    public IlMethod MethodRef(IlType declaring, string name, MethodSig signature) => new IlMemberMethod(declaring, name, signature);

    public IlField FieldRef(IlType declaring, string name, IlType type, bool isStatic = false) => new IlMemberField(declaring, name, type, isStatic);

    /// <summary>A reflection type as an IL type. Framework types map to their ref-pack assembly; other types to
    /// the assembly that defines them.</summary>
    public IlType Import(Type t)
    {
        if (t.IsByRef) return Import(t.GetElementType()!).ByRef();
        if (t.IsPointer) return Import(t.GetElementType()!).Pointer();
        if (t.IsSZArray) return Import(t.GetElementType()!).SzArray();
        if (t.IsArray) return Import(t.GetElementType()!).Array(t.GetArrayRank());
        if (t.IsGenericParameter) return t.DeclaringMethod != null ? IlType.MVar(t.GenericParameterPosition) : IlType.Var(t.GenericParameterPosition);
        if (t.IsFunctionPointer)
        {
            var cc = t.IsUnmanagedFunctionPointer ? SignatureCallingConvention.Unmanaged : SignatureCallingConvention.Default;
            return IlType.FnPtr(new MethodSig(Import(t.GetFunctionPointerReturnType()), [.. t.GetFunctionPointerParameterTypes().Select(Import)], callingConvention: cc));
        }
        if (Primitive(t) is { } p) return p;
        if (t.IsConstructedGenericType) return IlType.Generic(Entity(t.GetGenericTypeDefinition()), [.. t.GetGenericArguments().Select(Import)]);
        return Entity(t).Type;
    }

    static IlType? Primitive(Type t) =>
        t == typeof(void) ? IlType.Void : t == typeof(bool) ? IlType.Bool : t == typeof(char) ? IlType.Char
        : t == typeof(sbyte) ? IlType.SByte : t == typeof(byte) ? IlType.Byte : t == typeof(short) ? IlType.Int16
        : t == typeof(ushort) ? IlType.UInt16 : t == typeof(int) ? IlType.Int32 : t == typeof(uint) ? IlType.UInt32
        : t == typeof(long) ? IlType.Int64 : t == typeof(ulong) ? IlType.UInt64 : t == typeof(float) ? IlType.Single
        : t == typeof(double) ? IlType.Double : t == typeof(nint) ? IlType.IntPtr : t == typeof(nuint) ? IlType.UIntPtr
        : t == typeof(string) ? IlType.String : t == typeof(object) ? IlType.Object
        : t == typeof(System.TypedReference) ? IlType.TypedReference : null;

    IlTypeEntity Entity(Type t)
    {
        if (t.IsNested) return NestedExternal((IlExternalType)Entity(t.DeclaringType!), t.Name, t.IsValueType);
        string ns = t.Namespace ?? "";
        if (References.TryFind(t.FullName!, out _)) return External(ns, t.Name, null, t.IsValueType);
        var an = t.Assembly.GetName();
        return External(ns, t.Name, AssemblyRef(an.Name!, an.Version ?? new Version(0, 0, 0, 0), an.CultureName ?? "", an.GetPublicKeyToken()), t.IsValueType);
    }

    /// <summary>A reflection method or constructor as a MemberRef (and MethodSpec for generic instantiations).
    /// The signature is the open definition's, with custom modifiers (`in` parameters, `volatile` fields).</summary>
    public IlMethod Import(MethodBase m)
    {
        if (m is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } mi)
            return new IlMethodInstance(Import(mi.GetGenericMethodDefinition()), [.. mi.GetGenericArguments().Select(Import)]);
        var def = m.Module.ResolveMethod(m.MetadataToken) ?? m;
        IlType Param(ParameterInfo p) => Modified(Import(p.ParameterType), p.GetRequiredCustomModifiers(), p.GetOptionalCustomModifiers());
        var ret = def is MethodInfo dm ? Param(dm.ReturnParameter) : IlType.Void;
        var sig = new MethodSig(ret, [.. def.GetParameters().Select(Param)], isInstance: !def.IsStatic,
            genericArity: def.IsGenericMethodDefinition ? def.GetGenericArguments().Length : 0);
        return new IlMemberMethod(Import(m.DeclaringType!), m.Name, sig);
    }

    public IlField Import(FieldInfo f)
    {
        var def = f.Module.ResolveField(f.MetadataToken) ?? f;
        var type = Modified(Import(def.FieldType), def.GetRequiredCustomModifiers(), def.GetOptionalCustomModifiers());
        return new IlMemberField(Import(f.DeclaringType!), f.Name, type, f.IsStatic);
    }

    IlType Modified(IlType t, Type[] required, Type[] optional)
    {
        foreach (var m in optional.Reverse()) t = t.WithModifier(Import(m), required: false);
        foreach (var m in required.Reverse()) t = t.WithModifier(Import(m), required: true);
        return t;
    }

    /// <summary>The IL type of an AIR type. Alice-declared types, type parameters and anything else this module
    /// cannot know go through <paramref name="resolve"/> (the AIR emitter's own declarations).</summary>
    public IlType FromAir(AirType t, Func<AirType, IlType?>? resolve = null)
    {
        IlType Go(AirType a) => FromAir(a, resolve);
        IlType core = t switch
        {
            AirType.Prim p => p.Kind switch
            {
                PrimKind.Void => IlType.Void, PrimKind.Bool => IlType.Bool, PrimKind.Char => IlType.Char, PrimKind.SByte => IlType.SByte,
                PrimKind.Byte => IlType.Byte, PrimKind.Short => IlType.Int16, PrimKind.UShort => IlType.UInt16, PrimKind.Int => IlType.Int32,
                PrimKind.UInt => IlType.UInt32, PrimKind.Long => IlType.Int64, PrimKind.ULong => IlType.UInt64, PrimKind.Float => IlType.Single,
                PrimKind.Double => IlType.Double, PrimKind.Decimal => CoreType("Decimal"), PrimKind.String => IlType.String,
                _ => IlType.Object,   // object, dynamic (dispatched by Alice.Runtime.Dyn), the nil literal
            },
            AirType.ClrRef { Clr: { } clr } c when c.Args.Count > 0 && clr.IsGenericTypeDefinition =>
                IlType.Generic(((IlType.NamedType)Import(clr)).Entity, [.. c.Args.Select(Go)]),
            AirType.ClrRef { Clr: { } clr } => Import(clr),
            AirType.Array a => a.Rank == 1 ? Go(a.Elem).SzArray() : Go(a.Elem).Array(a.Rank),
            AirType.ByRef r => Go(r.Elem).ByRef(),
            AirType.Pointer p => Go(p.Elem).Pointer(),
            AirType.FnPtr f => IlType.FnPtr(new MethodSig(Go(f.Ret), [.. f.Params.Select(Go)],
                callingConvention: f.CallConv.Length == 0 ? SignatureCallingConvention.Default : SignatureCallingConvention.Unmanaged)),
            AirType.Tuple tu when tu.Items.Count is >= 1 and <= 7 =>
                IlType.Generic(External("System", "ValueTuple`" + tu.Items.Count), [.. tu.Items.Select(Go)]),
            _ => resolve?.Invoke(t) ?? throw new NotSupportedException("no IL type for AIR type " + t.ToCs()),
        };
        if (t.Nullable && core.IsValueType && core is not IlType.PointerType and not IlType.FnPtrType)
            return IlType.Generic(External("System", "Nullable`1"), core);
        return core;
    }

    // ---------------------------------------------------------------- output

    /// <summary>Writes the PE image and, when <paramref name="pdb"/> is given, its portable PDB.</summary>
    public void Write(Stream pe, Stream? pdb = null) => MetadataWriter.Write(this, pe, pdb);

    /// <summary>The PE image (and PDB) as byte arrays.</summary>
    public (byte[] Pe, byte[] Pdb) Emit()
    {
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        Write(pe, pdb);
        return (pe.ToArray(), pdb.ToArray());
    }
}
