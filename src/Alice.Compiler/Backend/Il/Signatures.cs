using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// A type as it appears in IL signatures and tokens (ADR 0033). Structure only: a named type points at an
/// <see cref="IlTypeEntity"/> (a type defined in this module or one referenced from another assembly), and the
/// metadata writer turns it into a TypeDef, TypeRef or TypeSpec when the module is written. Instances are
/// immutable and compare by reference.
/// </summary>
public abstract class IlType
{
    private protected IlType() { }

    /// <summary>A value type on the evaluation stack (primitives other than string/object, structs, enums).</summary>
    public abstract bool IsValueType { get; }

    public static readonly IlType Void = new PrimType(PrimitiveTypeCode.Void);
    public static readonly IlType Bool = new PrimType(PrimitiveTypeCode.Boolean);
    public static readonly IlType Char = new PrimType(PrimitiveTypeCode.Char);
    public static readonly IlType SByte = new PrimType(PrimitiveTypeCode.SByte);
    public static readonly IlType Byte = new PrimType(PrimitiveTypeCode.Byte);
    public static readonly IlType Int16 = new PrimType(PrimitiveTypeCode.Int16);
    public static readonly IlType UInt16 = new PrimType(PrimitiveTypeCode.UInt16);
    public static readonly IlType Int32 = new PrimType(PrimitiveTypeCode.Int32);
    public static readonly IlType UInt32 = new PrimType(PrimitiveTypeCode.UInt32);
    public static readonly IlType Int64 = new PrimType(PrimitiveTypeCode.Int64);
    public static readonly IlType UInt64 = new PrimType(PrimitiveTypeCode.UInt64);
    public static readonly IlType Single = new PrimType(PrimitiveTypeCode.Single);
    public static readonly IlType Double = new PrimType(PrimitiveTypeCode.Double);
    public static readonly IlType IntPtr = new PrimType(PrimitiveTypeCode.IntPtr);
    public static readonly IlType UIntPtr = new PrimType(PrimitiveTypeCode.UIntPtr);
    public static readonly IlType String = new PrimType(PrimitiveTypeCode.String);
    public static readonly IlType Object = new PrimType(PrimitiveTypeCode.Object);
    public static readonly IlType TypedReference = new PrimType(PrimitiveTypeCode.TypedReference);

    public static IlType Prim(PrimitiveTypeCode code) => code switch
    {
        PrimitiveTypeCode.Void => Void, PrimitiveTypeCode.Boolean => Bool, PrimitiveTypeCode.Char => Char,
        PrimitiveTypeCode.SByte => SByte, PrimitiveTypeCode.Byte => Byte, PrimitiveTypeCode.Int16 => Int16,
        PrimitiveTypeCode.UInt16 => UInt16, PrimitiveTypeCode.Int32 => Int32, PrimitiveTypeCode.UInt32 => UInt32,
        PrimitiveTypeCode.Int64 => Int64, PrimitiveTypeCode.UInt64 => UInt64, PrimitiveTypeCode.Single => Single,
        PrimitiveTypeCode.Double => Double, PrimitiveTypeCode.IntPtr => IntPtr, PrimitiveTypeCode.UIntPtr => UIntPtr,
        PrimitiveTypeCode.String => String, PrimitiveTypeCode.Object => Object,
        _ => TypedReference,
    };

    /// <summary>`!index`: a type parameter of the enclosing type.</summary>
    public static IlType Var(int index) => new GenericParamType(index, false);

    /// <summary>`!!index`: a type parameter of the method.</summary>
    public static IlType MVar(int index) => new GenericParamType(index, true);

    public static IlType Named(IlTypeEntity entity) => new NamedType(entity);

    public static IlType Generic(IlTypeEntity definition, params IlType[] args) => new GenericInstType(definition, [.. args]);

    public static IlType FnPtr(MethodSig signature) => new FnPtrType(signature);

    public IlType SzArray() => new SzArrayType(this);

    public IlType Array(int rank) => rank == 1 ? new ArrayType(this, 1) : new ArrayType(this, rank);

    public IlType ByRef() => new ByRefType(this);

    public IlType Pointer() => new PointerType(this);

    /// <summary>`modreq(modifier) this` (required) or `modopt(modifier) this`.</summary>
    public IlType WithModifier(IlType modifier, bool required) => new ModifiedType(this, modifier, required);

    public sealed class PrimType : IlType
    {
        internal PrimType(PrimitiveTypeCode code) => Code = code;
        public PrimitiveTypeCode Code { get; }
        public override bool IsValueType => Code is not (PrimitiveTypeCode.String or PrimitiveTypeCode.Object or PrimitiveTypeCode.Void);
        public override string ToString() => Code.ToString().ToLowerInvariant();
    }

    public sealed class NamedType : IlType
    {
        internal NamedType(IlTypeEntity entity) => Entity = entity;
        public IlTypeEntity Entity { get; }
        public override bool IsValueType => Entity.IsValueType;
        public override string ToString() => Entity.ToString();
    }

    public sealed class GenericInstType : IlType
    {
        internal GenericInstType(IlTypeEntity definition, ImmutableArray<IlType> args) { Definition = definition; Args = args; }
        public IlTypeEntity Definition { get; }
        public ImmutableArray<IlType> Args { get; }
        public override bool IsValueType => Definition.IsValueType;
        public override string ToString() => Definition + "<" + string.Join(", ", Args) + ">";
    }

    public sealed class SzArrayType : IlType
    {
        internal SzArrayType(IlType elem) => Element = elem;
        public IlType Element { get; }
        public override bool IsValueType => false;
        public override string ToString() => Element + "[]";
    }

    /// <summary>A multi-dimensional array with zero lower bounds and no sizes (the C# `T[,]` shape).</summary>
    public sealed class ArrayType : IlType
    {
        internal ArrayType(IlType elem, int rank) { Element = elem; Rank = rank; }
        public IlType Element { get; }
        public int Rank { get; }
        public override bool IsValueType => false;
        public override string ToString() => Element + "[" + new string(',', Rank - 1) + "]";
    }

    public sealed class ByRefType : IlType
    {
        internal ByRefType(IlType elem) => Element = elem;
        public IlType Element { get; }
        public override bool IsValueType => false;
        public override string ToString() => Element + "&";
    }

    public sealed class PointerType : IlType
    {
        internal PointerType(IlType elem) => Element = elem;
        public IlType Element { get; }
        public override bool IsValueType => true;
        public override string ToString() => Element + "*";
    }

    public sealed class GenericParamType : IlType
    {
        internal GenericParamType(int index, bool method) { Index = index; IsMethodParameter = method; }
        public int Index { get; }
        public bool IsMethodParameter { get; }
        /// <summary>Unknown until instantiated: treated as a reference for stack purposes.</summary>
        public override bool IsValueType => false;
        public override string ToString() => (IsMethodParameter ? "!!" : "!") + Index;
    }

    public sealed class FnPtrType : IlType
    {
        internal FnPtrType(MethodSig sig) => Signature = sig;
        public MethodSig Signature { get; }
        public override bool IsValueType => true;
        public override string ToString() => "method " + Signature;
    }

    public sealed class ModifiedType : IlType
    {
        internal ModifiedType(IlType inner, IlType modifier, bool required) { Inner = inner; Modifier = modifier; Required = required; }
        public IlType Inner { get; }
        public IlType Modifier { get; }
        public bool Required { get; }
        public override bool IsValueType => Inner.IsValueType;
        public override string ToString() => Inner + (Required ? " modreq(" : " modopt(") + Modifier + ")";
    }
}

/// <summary>
/// A method signature: calling convention, `this`, generic arity, return and parameter types. It is also the
/// stack effect of a call (<see cref="IlBuilder"/> computes max-stack from it). Varargs are not supported.
/// </summary>
public sealed class MethodSig
{
    public MethodSig(IlType ret, IReadOnlyList<IlType> parameters, bool isInstance = false, int genericArity = 0,
        SignatureCallingConvention callingConvention = SignatureCallingConvention.Default, bool explicitThis = false)
    {
        if (callingConvention == SignatureCallingConvention.VarArgs) throw new NotSupportedException("varargs signatures");
        Return = ret;
        Parameters = [.. parameters];
        IsInstance = isInstance;
        GenericArity = genericArity;
        CallingConvention = callingConvention;
        ExplicitThis = explicitThis;
    }

    public static MethodSig Static(IlType ret, params IlType[] parameters) => new(ret, parameters);

    public static MethodSig Instance(IlType ret, params IlType[] parameters) => new(ret, parameters, isInstance: true);

    public IlType Return { get; }
    public ImmutableArray<IlType> Parameters { get; }
    public bool IsInstance { get; }
    public int GenericArity { get; }
    public SignatureCallingConvention CallingConvention { get; }
    /// <summary>`this` is spelled as the first of <see cref="Parameters"/>.</summary>
    public bool ExplicitThis { get; }

    /// <summary>Values a call pops (arguments and the implicit `this`).</summary>
    internal int ArgumentCount => Parameters.Length + (IsInstance && !ExplicitThis ? 1 : 0);

    internal bool ReturnsValue => !(Return is IlType.PrimType { Code: PrimitiveTypeCode.Void }
        || Return is IlType.ModifiedType { Inner: IlType.PrimType { Code: PrimitiveTypeCode.Void } });

    internal SignatureHeader Header
    {
        get
        {
            var attrs = SignatureAttributes.None;
            if (IsInstance) attrs |= SignatureAttributes.Instance;
            if (ExplicitThis) attrs |= SignatureAttributes.ExplicitThis;
            if (GenericArity > 0) attrs |= SignatureAttributes.Generic;
            return new SignatureHeader(SignatureKind.Method, CallingConvention, attrs);
        }
    }

    public override string ToString() =>
        (IsInstance ? "instance " : "") + Return + (GenericArity > 0 ? "<" + GenericArity + ">" : "") + "(" + string.Join(", ", Parameters) + ")";
}

/// <summary>Turns a named type into its TypeDef or TypeRef handle (implemented by the metadata writer).</summary>
public interface IIlTypeResolver
{
    EntityHandle Resolve(IlTypeEntity entity);
}

/// <summary>
/// Blob encoders for every signature kind ECMA-335 II.23.2 needs: types (primitives, class/valuetype refs, generic
/// instantiations, SZ and multi-dimensional arrays, byrefs, pointers, function pointers with calling conventions,
/// custom modifiers), method, field, property, local-variable, TypeSpec and MethodSpec blobs.
/// </summary>
public static class Signatures
{
    const byte ElementVoid = 0x01, ElementByRef = 0x10, ElementTypedByRef = 0x16, ElementPinned = 0x45;

    /// <summary>Writes one `Type` (with any leading custom modifiers, `&amp;` and `void`) into <paramref name="b"/>.</summary>
    public static void EncodeType(BlobBuilder b, IlType t, IIlTypeResolver r)
    {
        var e = new SignatureTypeEncoder(b);
        switch (t)
        {
            case IlType.ModifiedType m:
                e.CustomModifiers().AddModifier(ModifierHandle(m.Modifier, r), isOptional: !m.Required);
                EncodeType(b, m.Inner, r);
                break;
            case IlType.PrimType { Code: PrimitiveTypeCode.Void }:
                b.WriteByte(ElementVoid);
                break;
            case IlType.PrimType { Code: PrimitiveTypeCode.TypedReference }:
                b.WriteByte(ElementTypedByRef);
                break;
            case IlType.PrimType p:
                e.PrimitiveType(p.Code);
                break;
            case IlType.NamedType n:
                e.Type(r.Resolve(n.Entity), n.Entity.IsValueType);
                break;
            case IlType.GenericInstType g:
                var args = e.GenericInstantiation(r.Resolve(g.Definition), g.Args.Length, g.Definition.IsValueType);
                foreach (var a in g.Args) EncodeType(args.AddArgument().Builder, a, r);
                break;
            case IlType.SzArrayType s:
                EncodeType(e.SZArray().Builder, s.Element, r);
                break;
            case IlType.ArrayType a:
                e.Array(out var elem, out var shape);
                EncodeType(elem.Builder, a.Element, r);
                shape.Shape(a.Rank, ImmutableArray<int>.Empty, ImmutableArray.CreateRange(Enumerable.Repeat(0, a.Rank)));
                break;
            case IlType.ByRefType br:
                b.WriteByte(ElementByRef);
                EncodeType(b, br.Element, r);
                break;
            case IlType.PointerType { Element: IlType.PrimType { Code: PrimitiveTypeCode.Void } }:
                e.VoidPointer();
                break;
            case IlType.PointerType p:
                EncodeType(e.Pointer().Builder, p.Element, r);
                break;
            case IlType.GenericParamType gp when gp.IsMethodParameter:
                e.GenericMethodTypeParameter(gp.Index);
                break;
            case IlType.GenericParamType gp:
                e.GenericTypeParameter(gp.Index);
                break;
            case IlType.FnPtrType f:
                b.WriteByte((byte)SignatureTypeCode.FunctionPointer);
                EncodeMethod(b, f.Signature, r);
                break;
            default:
                throw new NotSupportedException("IL type " + t);
        }
    }

    static EntityHandle ModifierHandle(IlType modifier, IIlTypeResolver r) => modifier is IlType.NamedType n
        ? r.Resolve(n.Entity)
        : throw new ArgumentException("a custom modifier must be a named type, not " + modifier);

    /// <summary>MethodDefSig/MethodRefSig/StandAloneMethodSig: header, generic arity, count, return, parameters.</summary>
    public static void EncodeMethod(BlobBuilder b, MethodSig s, IIlTypeResolver r)
    {
        b.WriteByte(s.Header.RawValue);
        if (s.GenericArity > 0) b.WriteCompressedInteger(s.GenericArity);
        b.WriteCompressedInteger(s.Parameters.Length);
        EncodeType(b, s.Return, r);
        foreach (var p in s.Parameters) EncodeType(b, p, r);
    }

    public static BlobBuilder Method(MethodSig s, IIlTypeResolver r)
    {
        var b = new BlobBuilder();
        EncodeMethod(b, s, r);
        return b;
    }

    public static BlobBuilder Field(IlType type, IIlTypeResolver r)
    {
        var b = new BlobBuilder();
        b.WriteByte(new SignatureHeader(SignatureKind.Field, SignatureCallingConvention.Default, SignatureAttributes.None).RawValue);
        EncodeType(b, type, r);
        return b;
    }

    /// <summary>PropertySig: `HASTHIS`? count, type, index parameters.</summary>
    public static BlobBuilder Property(IlType type, IReadOnlyList<IlType> indexParameters, bool isInstance, IIlTypeResolver r)
    {
        var b = new BlobBuilder();
        b.WriteByte(new SignatureHeader(SignatureKind.Property, SignatureCallingConvention.Default,
            isInstance ? SignatureAttributes.Instance : SignatureAttributes.None).RawValue);
        b.WriteCompressedInteger(indexParameters.Count);
        EncodeType(b, type, r);
        foreach (var p in indexParameters) EncodeType(b, p, r);
        return b;
    }

    /// <summary>LocalVarSig: each local is `CustomMod* [pinned] [&amp;] Type` in index order.</summary>
    public static BlobBuilder Locals(IReadOnlyList<IlLocal> locals, IIlTypeResolver r)
    {
        var b = new BlobBuilder();
        b.WriteByte(new SignatureHeader(SignatureKind.LocalVariables, SignatureCallingConvention.Default, SignatureAttributes.None).RawValue);
        b.WriteCompressedInteger(locals.Count);
        foreach (var l in locals)
        {
            var t = l.Type;
            while (t is IlType.ModifiedType m)
            {
                new SignatureTypeEncoder(b).CustomModifiers().AddModifier(ModifierHandle(m.Modifier, r), !m.Required);
                t = m.Inner;
            }
            if (l.Pinned) b.WriteByte(ElementPinned);
            EncodeType(b, t, r);
        }
        return b;
    }

    public static BlobBuilder TypeSpec(IlType type, IIlTypeResolver r)
    {
        var b = new BlobBuilder();
        EncodeType(b, type, r);
        return b;
    }

    public static BlobBuilder MethodSpec(IReadOnlyList<IlType> args, IIlTypeResolver r)
    {
        var b = new BlobBuilder();
        var e = new BlobEncoder(b).MethodSpecificationSignature(args.Count);
        foreach (var a in args) EncodeType(e.AddArgument().Builder, a, r);
        return b;
    }
}
