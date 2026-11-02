using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// The targeting pack (`packs/Microsoft.NETCore.App.Ref/&lt;ver&gt;/ref/net&lt;N&gt;`): an index from type full name to
/// the reference assembly that defines it, so emitted AssemblyRefs name System.Runtime/System.Console... as a C#
/// compiler would, never System.Private.CoreLib or other host implementation assemblies. Located the way
/// AliceCompiler finds the ref pack for its analyzers.
/// </summary>
public sealed class RefPack
{
    public readonly record struct AssemblyIdentity(string Name, Version Version, string Culture, byte[] PublicKeyToken);

    public readonly record struct Entry(AssemblyIdentity Assembly, bool IsValueType);

    static readonly Lazy<RefPack> DefaultPack = new(() => new RefPack(Locate()
        ?? throw new InvalidOperationException("no Microsoft.NETCore.App.Ref targeting pack found next to the running .NET")));

    /// <summary>The ref pack of the running .NET major version.</summary>
    public static RefPack Default => DefaultPack.Value;

    readonly Lazy<Dictionary<string, Entry>> index;

    public RefPack(string directory)
    {
        Directory = directory;
        index = new(Build);
    }

    public string Directory { get; }

    /// <summary>`dotnet/packs/Microsoft.NETCore.App.Ref/&lt;newest same-major&gt;/ref/net&lt;major&gt;.0`, or null.</summary>
    public static string? Locate()
    {
        var roots = new List<string>();
        var shared = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(shared)) roots.Add(Path.GetFullPath(Path.Combine(shared, "..", "..", "..")));
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dr) roots.Add(dr);
        string major = Environment.Version.Major + ".";
        foreach (var root in roots)
        {
            var packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!System.IO.Directory.Exists(packs)) continue;
            var ver = System.IO.Directory.GetDirectories(packs).Select(Path.GetFileName).Where(v => v!.StartsWith(major, StringComparison.Ordinal))
                .OrderByDescending(v => Version.TryParse(v!.Split('-')[0], out var x) ? x : new Version()).ThenByDescending(v => v, StringComparer.Ordinal).FirstOrDefault();
            if (ver == null) continue;
            var dir = Path.Combine(packs, ver, "ref", "net" + Environment.Version.Major + ".0");
            if (System.IO.Directory.Exists(dir)) return dir;
        }
        return null;
    }

    public bool TryFind(string fullName, out Entry entry) => index.Value.TryGetValue(fullName, out entry);

    /// <summary>Paths of the reference assemblies (sorted).</summary>
    public IEnumerable<string> Assemblies => System.IO.Directory.GetFiles(Directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal);

    Dictionary<string, Entry> Build()
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var path in Assemblies)
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) continue;
            var a = md.GetAssemblyDefinition();
            var key = md.GetBlobBytes(a.PublicKey);
            var id = new AssemblyIdentity(md.GetString(a.Name), a.Version, md.GetString(a.Culture), Token(key));
            foreach (var h in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(h);
                var name = FullName(md, t);
                if (name == "<Module>" || map.ContainsKey(name)) continue;
                map[name] = new Entry(id, IsValueType(md, t, name));
            }
        }
        return map;
    }

    static string FullName(MetadataReader md, TypeDefinition t)
    {
        var name = md.GetString(t.Name);
        if (t.GetDeclaringType() is { IsNil: false } d) return FullName(md, md.GetTypeDefinition(d)) + "+" + name;
        var ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    static bool IsValueType(MetadataReader md, TypeDefinition t, string name)
    {
        if (name == "System.Enum" || t.BaseType.IsNil) return false;
        (StringHandle ns, StringHandle n) = t.BaseType.Kind switch
        {
            HandleKind.TypeReference => (md.GetTypeReference((TypeReferenceHandle)t.BaseType).Namespace, md.GetTypeReference((TypeReferenceHandle)t.BaseType).Name),
            HandleKind.TypeDefinition => (md.GetTypeDefinition((TypeDefinitionHandle)t.BaseType).Namespace, md.GetTypeDefinition((TypeDefinitionHandle)t.BaseType).Name),
            _ => (default, default),
        };
        return !ns.IsNil && md.GetString(ns) == "System" && md.GetString(n) is "ValueType" or "Enum";
    }

    static byte[] Token(byte[] publicKey)
    {
        if (publicKey.Length == 0) return [];
        var hash = SHA1.HashData(publicKey);
        var token = new byte[8];
        for (int i = 0; i < 8; i++) token[i] = hash[hash.Length - 1 - i];
        return token;
    }
}

/// <summary>
/// Serializes an <see cref="IlModule"/>: ECMA-335 metadata tables through <see cref="MetadataBuilder"/> (rows in
/// definition order; tables that must be sorted are emitted sorted), method bodies through <see cref="IlBuilder"/>,
/// the PE through <see cref="ManagedPEBuilder"/> with a content-hash id provider (deterministic MVID, PE timestamp
/// and PDB id), and the portable PDB through <see cref="PdbWriter"/>.
/// </summary>
internal sealed class MetadataWriter : IIlTokens
{
    readonly IlModule module;
    readonly MetadataBuilder md = new();
    readonly Dictionary<IlTypeDef, int> typeRows = [];
    readonly Dictionary<IlFieldDef, int> fieldRows = [];
    readonly Dictionary<IlMethodDef, int> methodRows = [];
    readonly Dictionary<IlAssemblyRef, AssemblyReferenceHandle> asmRefs = [];
    readonly Dictionary<IlExternalType, TypeReferenceHandle> typeRefs = [];
    readonly Dictionary<BlobHandle, TypeSpecificationHandle> typeSpecs = [];
    readonly Dictionary<(EntityHandle, StringHandle, BlobHandle), MemberReferenceHandle> memberRefs = [];
    readonly Dictionary<(EntityHandle, BlobHandle), MethodSpecificationHandle> methodSpecs = [];
    readonly Dictionary<BlobHandle, StandaloneSignatureHandle> sigs = [];

    MetadataWriter(IlModule module) => this.module = module;

    /// <summary>Hash-based ids: the same content always yields the same MVID, timestamp and PDB id.</summary>
    internal static BlobContentId ContentId(IEnumerable<Blob> content)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var b in content)
        {
            var seg = b.GetBytes();
            h.AppendData(seg.Array!, seg.Offset, seg.Count);
        }
        return BlobContentId.FromHash(h.GetHashAndReset());
    }

    public static void Write(IlModule module, Stream peStream, Stream? pdbStream) => new MetadataWriter(module).Run(peStream, pdbStream);

    void Run(Stream peStream, Stream? pdbStream)
    {
        bool exe = module.EntryPoint != null;
        var mvid = md.ReserveGuid();
        md.AddModule(0, md.GetOrAddString(module.Name + (exe ? ".exe" : ".dll")), mvid.Handle, default, default);
        var asm = md.AddAssembly(md.GetOrAddString(module.Name), module.Version, default, default, 0, AssemblyHashAlgorithm.Sha1);

        // rows: <Module> is TypeDef 1; types follow with nested types right after their enclosing type
        var types = module.TypeDefOrder().ToList();
        var methods = new List<IlMethodDef>();
        int field = 1, method = 1;
        for (int i = 0; i < types.Count; i++)
        {
            typeRows[types[i]] = i + 2;
            foreach (var f in types[i].Fields) fieldRows[f] = field++;
            foreach (var m in types[i].Methods) { methodRows[m] = method++; methods.Add(m); }
        }

        md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // bodies first (they create references), then the definition tables in row order
        var il = new BlobBuilder();
        var bodyEncoder = new MethodBodyStreamEncoder(il);
        var bodies = new List<IlEncodedBody?>();
        foreach (var m in methods) bodies.Add(m.Body is { } b ? b.Encode(bodyEncoder, this) : null);

        int nextField = 1, nextMethod = 1, nextParam = 1;
        foreach (var t in types)
        {
            md.AddTypeDefinition(t.Attributes, md.GetOrAddString(t.Namespace), md.GetOrAddString(t.Name),
                t.BaseType != null ? Type(t.BaseType) : default,
                MetadataTokens.FieldDefinitionHandle(nextField), MetadataTokens.MethodDefinitionHandle(nextMethod));
            foreach (var f in t.Fields)
            {
                var fh = md.AddFieldDefinition(f.Attributes, md.GetOrAddString(f.Name), md.GetOrAddBlob(Signatures.Field(f.Type, this)));
                if (f.Constant != null) md.AddConstant(fh, f.Constant);
                nextField++;
            }
            foreach (var m in t.Methods)
            {
                var body = bodies[nextMethod - 1];
                md.AddMethodDefinition(m.Attributes, m.ImplAttributes, md.GetOrAddString(m.Name),
                    md.GetOrAddBlob(Signatures.Method(m.Signature, this)), body?.Offset ?? -1, MetadataTokens.ParameterHandle(nextParam));
                foreach (var p in m.Parameters.OrderBy(p => p.Sequence))
                {
                    var ph = md.AddParameter(p.Attributes, p.Name != null ? md.GetOrAddString(p.Name) : default, p.Sequence);
                    if (p.DefaultValue != null) md.AddConstant(ph, p.DefaultValue);
                    nextParam++;
                }
                nextMethod++;
            }
        }

        // sorted tables: InterfaceImpl (class, interface), NestedClass, ClassLayout, MethodImpl, GenericParam (owner, number)
        foreach (var t in types)
        {
            var th = TypeDef(t);
            foreach (var i in t.Interfaces.Select(Type).OrderBy(h => CodedIndex.TypeDefOrRefOrSpec(h))) md.AddInterfaceImplementation(th, i);
        }
        foreach (var t in types) if (t.Enclosing != null) md.AddNestedType(TypeDef(t), TypeDef(t.Enclosing));
        foreach (var t in types)
            if (t.PackingSize != null || t.ClassSize != null) md.AddTypeLayout(TypeDef(t), t.PackingSize ?? 0, t.ClassSize ?? 0);
        foreach (var t in types)
            foreach (var (body, decl) in t.MethodOverrides) md.AddMethodImplementation(TypeDef(t), MethodDef(body), Method(decl));

        var gps = new List<(EntityHandle Owner, IlGenericParam Param)>();
        foreach (var t in types)
        {
            foreach (var g in t.GenericParameters) gps.Add((TypeDef(t), g));
            foreach (var m in t.Methods) foreach (var g in m.GenericParameters) gps.Add((MethodDef(m), g));
        }
        var gpHandles = new List<(GenericParameterHandle, IlGenericParam)>();
        foreach (var (owner, g) in gps.OrderBy(x => CodedIndex.TypeOrMethodDef(x.Owner)).ThenBy(x => x.Param.Index))
            gpHandles.Add((md.AddGenericParameter(owner, g.Attributes, md.GetOrAddString(g.Name), g.Index), g));
        foreach (var (h, g) in gpHandles) foreach (var c in g.Constraints) md.AddGenericParameterConstraint(h, Type(c));

        // properties and events (map rows in type order, semantics are sorted by the builder)
        int nextProp = 1, nextEvent = 1;
        var props = new List<(PropertyDefinitionHandle, IlProperty)>();
        var events = new List<(EventDefinitionHandle, IlEvent)>();
        foreach (var t in types)
        {
            if (t.Properties.Count > 0) md.AddPropertyMap(TypeDef(t), MetadataTokens.PropertyDefinitionHandle(nextProp));
            foreach (var p in t.Properties)
            {
                var ph = md.AddProperty(p.Attributes, md.GetOrAddString(p.Name),
                    md.GetOrAddBlob(Signatures.Property(p.Type, p.IndexParameters, p.IsInstance, this)));
                if (p.Getter != null) md.AddMethodSemantics(ph, MethodSemanticsAttributes.Getter, MethodDef(p.Getter));
                if (p.Setter != null) md.AddMethodSemantics(ph, MethodSemanticsAttributes.Setter, MethodDef(p.Setter));
                props.Add((ph, p));
                nextProp++;
            }
            if (t.Events.Count > 0) md.AddEventMap(TypeDef(t), MetadataTokens.EventDefinitionHandle(nextEvent));
            foreach (var e in t.Events)
            {
                var eh = md.AddEvent(e.Attributes, md.GetOrAddString(e.Name), Type(e.HandlerType));
                md.AddMethodSemantics(eh, MethodSemanticsAttributes.Adder, MethodDef(e.Add));
                md.AddMethodSemantics(eh, MethodSemanticsAttributes.Remover, MethodDef(e.Remove));
                if (e.Raise != null) md.AddMethodSemantics(eh, MethodSemanticsAttributes.Raiser, MethodDef(e.Raise));
                events.Add((eh, e));
                nextEvent++;
            }
        }

        // custom attributes (the builder sorts the table)
        foreach (var a in module.CustomAttributes) Attribute(asm, a);
        foreach (var t in types)
        {
            foreach (var a in t.CustomAttributes) Attribute(TypeDef(t), a);
            foreach (var f in t.Fields) foreach (var a in f.CustomAttributes) Attribute(MetadataTokens.FieldDefinitionHandle(fieldRows[f]), a);
            foreach (var m in t.Methods)
            {
                foreach (var a in m.CustomAttributes) Attribute(MethodDef(m), a);
            }
        }
        int paramRow = 1;
        foreach (var m in methods)
            foreach (var p in m.Parameters.OrderBy(p => p.Sequence))
            {
                foreach (var a in p.CustomAttributes) Attribute(MetadataTokens.ParameterHandle(paramRow), a);
                paramRow++;
            }
        foreach (var (h, g) in gpHandles) foreach (var a in g.CustomAttributes) Attribute(h, a);
        foreach (var (h, p) in props) foreach (var a in p.CustomAttributes) Attribute(h, a);
        foreach (var (h, e) in events) foreach (var a in e.CustomAttributes) Attribute(h, a);

        var entry = module.EntryPoint != null ? MethodDef(module.EntryPoint) : default;

        var debug = new DebugDirectoryBuilder();
        if (pdbStream != null)
        {
            var (pdbBlob, pdbId) = PdbWriter.Build(module, methods, bodies, md.GetRowCounts(), entry, ContentId);
            var pdbBytes = pdbBlob.ToArray();
            debug.AddCodeViewEntry(module.Name + ".pdb", pdbId, PdbWriter.PortablePdbVersion);
            debug.AddPdbChecksumEntry("SHA256", ImmutableArray.Create(SHA256.HashData(pdbBytes)));
            pdbStream.Write(pdbBytes);
        }
        debug.AddReproducibleEntry();

        var header = new PEHeaderBuilder(imageCharacteristics: exe ? Characteristics.ExecutableImage : Characteristics.ExecutableImage | Characteristics.Dll);
        var pe = new ManagedPEBuilder(header, new MetadataRootBuilder(md), il, debugDirectoryBuilder: debug, entryPoint: entry,
            flags: CorFlags.ILOnly, deterministicIdProvider: ContentId);
        var image = new BlobBuilder();
        var id = pe.Serialize(image);
        new BlobWriter(mvid.Content).WriteGuid(id.Guid);
        image.WriteContentTo(peStream);
    }

    // ---------------------------------------------------------------- handles

    TypeDefinitionHandle TypeDef(IlTypeDef t) => typeRows.TryGetValue(t, out var r)
        ? MetadataTokens.TypeDefinitionHandle(r)
        : throw new InvalidOperationException($"type {t} belongs to another module");

    MethodDefinitionHandle MethodDef(IlMethodDef m) => methodRows.TryGetValue(m, out var r)
        ? MetadataTokens.MethodDefinitionHandle(r)
        : throw new InvalidOperationException($"method {m} belongs to another module");

    public EntityHandle Resolve(IlTypeEntity entity) => entity switch
    {
        IlTypeDef d => TypeDef(d),
        IlExternalType e => TypeRef(e),
        _ => throw new NotSupportedException(entity.GetType().Name),
    };

    TypeReferenceHandle TypeRef(IlExternalType e)
    {
        if (typeRefs.TryGetValue(e, out var h)) return h;
        EntityHandle scope = e.Enclosing != null ? TypeRef(e.Enclosing) : AssemblyRef(e.Assembly!);
        return typeRefs[e] = md.AddTypeReference(scope, md.GetOrAddString(e.Namespace), md.GetOrAddString(e.Name));
    }

    AssemblyReferenceHandle AssemblyRef(IlAssemblyRef a)
    {
        if (asmRefs.TryGetValue(a, out var h)) return h;
        return asmRefs[a] = md.AddAssemblyReference(md.GetOrAddString(a.Name), a.Version,
            a.Culture.Length > 0 ? md.GetOrAddString(a.Culture) : default,
            a.PublicKeyToken.Length > 0 ? md.GetOrAddBlob(a.PublicKeyToken) : default, default, default);
    }

    static readonly Dictionary<PrimitiveTypeCode, string> PrimNames = new()
    {
        [PrimitiveTypeCode.Void] = "Void", [PrimitiveTypeCode.Boolean] = "Boolean", [PrimitiveTypeCode.Char] = "Char",
        [PrimitiveTypeCode.SByte] = "SByte", [PrimitiveTypeCode.Byte] = "Byte", [PrimitiveTypeCode.Int16] = "Int16",
        [PrimitiveTypeCode.UInt16] = "UInt16", [PrimitiveTypeCode.Int32] = "Int32", [PrimitiveTypeCode.UInt32] = "UInt32",
        [PrimitiveTypeCode.Int64] = "Int64", [PrimitiveTypeCode.UInt64] = "UInt64", [PrimitiveTypeCode.Single] = "Single",
        [PrimitiveTypeCode.Double] = "Double", [PrimitiveTypeCode.IntPtr] = "IntPtr", [PrimitiveTypeCode.UIntPtr] = "UIntPtr",
        [PrimitiveTypeCode.String] = "String", [PrimitiveTypeCode.Object] = "Object", [PrimitiveTypeCode.TypedReference] = "TypedReference",
    };

    /// <summary>A TypeDefOrRefOrSpec token: primitives as their System.X TypeRef (as C# compilers emit them),
    /// named types as TypeDef/TypeRef, everything else as a (deduplicated) TypeSpec.</summary>
    public EntityHandle Type(IlType t)
    {
        switch (t)
        {
            case IlType.PrimType p: return Resolve(((IlType.NamedType)module.CoreType(PrimNames[p.Code])).Entity);
            case IlType.NamedType n: return Resolve(n.Entity);
        }
        var blob = md.GetOrAddBlob(Signatures.TypeSpec(t, this));
        if (typeSpecs.TryGetValue(blob, out var h)) return h;
        return typeSpecs[blob] = md.AddTypeSpecification(blob);
    }

    MemberReferenceHandle MemberRef(EntityHandle parent, string name, BlobBuilder sig)
    {
        var key = (parent, md.GetOrAddString(name), md.GetOrAddBlob(sig));
        if (memberRefs.TryGetValue(key, out var h)) return h;
        return memberRefs[key] = md.AddMemberReference(key.Item1, key.Item2, key.Item3);
    }

    public EntityHandle Method(IlMethod m)
    {
        switch (m)
        {
            case IlMethodDef d when d.Owner.GenericParameters.Count > 0:
                return Method(d.On(d.Owner.SelfInstance));
            case IlMethodDef d:
                return MethodDef(d);
            case IlMemberMethod r:
                return MemberRef(Type(r.Parent), r.Name, Signatures.Method(r.Signature, this));
            case IlMethodInstance i:
                var key = (Method(i.Generic), md.GetOrAddBlob(Signatures.MethodSpec(i.Args, this)));
                if (methodSpecs.TryGetValue(key, out var h)) return h;
                return methodSpecs[key] = md.AddMethodSpecification(key.Item1, key.Item2);
            default:
                throw new NotSupportedException(m.GetType().Name);
        }
    }

    public EntityHandle Field(IlField f) => f switch
    {
        IlFieldDef d when d.Owner.GenericParameters.Count > 0 => Field(d.On(d.Owner.SelfInstance)),
        IlFieldDef d => fieldRows.TryGetValue(d, out var r) ? MetadataTokens.FieldDefinitionHandle(r) : throw new InvalidOperationException($"field {d} belongs to another module"),
        IlMemberField r => MemberRef(Type(r.Parent), r.Name, Signatures.Field(r.Type, this)),
        _ => throw new NotSupportedException(f.GetType().Name),
    };

    public UserStringHandle String(string value) => md.GetOrAddUserString(value);

    StandaloneSignatureHandle Standalone(BlobBuilder sig)
    {
        var blob = md.GetOrAddBlob(sig);
        if (sigs.TryGetValue(blob, out var h)) return h;
        return sigs[blob] = md.AddStandaloneSignature(blob);
    }

    public StandaloneSignatureHandle CallSite(MethodSig signature) => Standalone(Signatures.Method(signature, this));

    public StandaloneSignatureHandle Locals(IReadOnlyList<IlLocal> locals) => Standalone(Signatures.Locals(locals, this));

    // ---------------------------------------------------------------- custom attributes

    void Attribute(EntityHandle parent, IlCustomAttribute a)
    {
        var ps = a.Constructor.Signature.Parameters;
        if (ps.Length != a.Arguments.Count) throw new ArgumentException($"{a.Constructor} takes {ps.Length} arguments, not {a.Arguments.Count}");
        var b = new BlobBuilder();
        new BlobEncoder(b).CustomAttributeSignature(out var fixedArgs, out var namedArgs);
        for (int i = 0; i < ps.Length; i++) Literal(fixedArgs.AddArgument(), ps[i], a.Arguments[i]);
        var named = a.Named ?? [];
        var na = namedArgs.Count(named.Count);
        foreach (var n in named)
        {
            na.AddArgument(n.IsField, out var type, out var name, out var literal);
            NamedType(type, n.Type);
            name.Name(n.Name);
            Literal(literal, n.Type, n.Value);
        }
        md.AddCustomAttribute(parent, Method(a.Constructor), md.GetOrAddBlob(b));
    }

    static bool IsSystemType(IlType t) => t is IlType.NamedType { Entity: IlExternalType { Namespace: "System", Name: "Type", Enclosing: null } };

    static object? Underlying(object? v) => v is Enum e ? Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), System.Globalization.CultureInfo.InvariantCulture) : v;

    void Literal(LiteralEncoder e, IlType t, object? v)
    {
        switch (t)
        {
            case IlType.SzArrayType arr:
                if (v == null) { e.Scalar().NullArray(); return; }
                var items = (Array)v;
                var lits = e.Vector().Count(items.Length);
                foreach (var item in items) Literal(lits.AddLiteral(), arr.Element, item);
                return;
            case IlType.PrimType { Code: PrimitiveTypeCode.Object }:
                e.TaggedScalar(out var tag, out var scalar);
                v = Underlying(v);
                if (v is System.Type ty) { tag.SystemType(); scalar.SystemType(ty.AssemblyQualifiedName); return; }
                if (v == null) { tag.String(); scalar.Constant(null); return; }
                tag.PrimitiveType(SerializationCode(v.GetType()));
                scalar.Constant(v);
                return;
            case var _ when IsSystemType(t):
                e.Scalar().SystemType(v is System.Type st ? st.AssemblyQualifiedName : (string?)v);
                return;
            default:
                e.Scalar().Constant(Underlying(v));
                return;
        }
    }

    static PrimitiveSerializationTypeCode SerializationCode(System.Type t) => System.Type.GetTypeCode(t) switch
    {
        TypeCode.Boolean => PrimitiveSerializationTypeCode.Boolean, TypeCode.Char => PrimitiveSerializationTypeCode.Char,
        TypeCode.SByte => PrimitiveSerializationTypeCode.SByte, TypeCode.Byte => PrimitiveSerializationTypeCode.Byte,
        TypeCode.Int16 => PrimitiveSerializationTypeCode.Int16, TypeCode.UInt16 => PrimitiveSerializationTypeCode.UInt16,
        TypeCode.Int32 => PrimitiveSerializationTypeCode.Int32, TypeCode.UInt32 => PrimitiveSerializationTypeCode.UInt32,
        TypeCode.Int64 => PrimitiveSerializationTypeCode.Int64, TypeCode.UInt64 => PrimitiveSerializationTypeCode.UInt64,
        TypeCode.Single => PrimitiveSerializationTypeCode.Single, TypeCode.Double => PrimitiveSerializationTypeCode.Double,
        TypeCode.String => PrimitiveSerializationTypeCode.String,
        _ => throw new NotSupportedException("custom attribute value of type " + t),
    };

    static PrimitiveSerializationTypeCode SerializationCode(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Boolean => PrimitiveSerializationTypeCode.Boolean, PrimitiveTypeCode.Char => PrimitiveSerializationTypeCode.Char,
        PrimitiveTypeCode.SByte => PrimitiveSerializationTypeCode.SByte, PrimitiveTypeCode.Byte => PrimitiveSerializationTypeCode.Byte,
        PrimitiveTypeCode.Int16 => PrimitiveSerializationTypeCode.Int16, PrimitiveTypeCode.UInt16 => PrimitiveSerializationTypeCode.UInt16,
        PrimitiveTypeCode.Int32 => PrimitiveSerializationTypeCode.Int32, PrimitiveTypeCode.UInt32 => PrimitiveSerializationTypeCode.UInt32,
        PrimitiveTypeCode.Int64 => PrimitiveSerializationTypeCode.Int64, PrimitiveTypeCode.UInt64 => PrimitiveSerializationTypeCode.UInt64,
        PrimitiveTypeCode.Single => PrimitiveSerializationTypeCode.Single, PrimitiveTypeCode.Double => PrimitiveSerializationTypeCode.Double,
        PrimitiveTypeCode.String => PrimitiveSerializationTypeCode.String,
        _ => throw new NotSupportedException("custom attribute argument of type " + c),
    };

    static void NamedType(NamedArgumentTypeEncoder e, IlType t)
    {
        switch (t)
        {
            case IlType.PrimType { Code: PrimitiveTypeCode.Object }: e.Object(); return;
            case IlType.SzArrayType arr:
                var at = e.SZArray();
                if (arr.Element is IlType.PrimType { Code: PrimitiveTypeCode.Object }) at.ObjectArray(); else ElementType(at.ElementType(), arr.Element);
                return;
            default: ElementType(e.ScalarType(), t); return;
        }
    }

    static void ElementType(CustomAttributeElementTypeEncoder e, IlType t)
    {
        switch (t)
        {
            case IlType.PrimType p: e.PrimitiveType(SerializationCode(p.Code)); return;
            case var _ when IsSystemType(t): e.SystemType(); return;
            case IlType.NamedType { Entity: IlExternalType x, IsValueType: true }: e.Enum(x.SerializedName); return;
            case IlType.NamedType { Entity: IlTypeDef d, IsValueType: true }: e.Enum(d.ToString()); return;
            default: throw new NotSupportedException("named custom attribute argument of type " + t);
        }
    }
}
