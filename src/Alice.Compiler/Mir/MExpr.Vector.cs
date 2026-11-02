namespace Alice.Compiler.Mir;

// AIR nodes of the SIMD/stack lowerings (Lowerer.Vectorize, Simt, StackArrays, FlatRows; ADR 0032, backend plan
// D0.6). The C# printer spells each one byte-for-byte as the former MCode text (Backend/Printer.Vector.cs); MirDump
// cases are in Mir/MirDump.Vector.cs. The optimizer passes (MirRewriter, MirFacts) do not descend into the vector
// nodes, exactly as they never looked into the MCode text those replace.

// ---- common leaves: stand-ins for air-b1's Mir/MExpr.Core.cs (D0.4, #plan 894) ------------------------------------
// TODO merge: delete this section and use MExpr.Core.cs. The public API below is a copy of air-b1's, so the lowerings
// compile unchanged against either. The difference: air-b1's leaves derive from MCode (MCode is sealed in this tree),
// so until the merge the optimizer passes that read leaf text (MirFacts.IntLiteral/Writes, Affine) do not see these.

/// <summary>Stand-in base: the C# spelling, derived once from the typed fields.</summary>
public abstract class AirLeafStandIn : MExpr
{
    public string Text { get; }
    private protected AirLeafStandIn(string text) => Text = text;
}

public enum AirConstKind { Int, Float, Char, String, Bool }

public sealed class AirConst : AirLeafStandIn
{
    public AirConstKind Kind { get; }
    public string Literal { get; }

    public AirConst(AirConstKind kind, string literal, AirType? type = null) : base(kind == AirConstKind.String ? "\"" + literal + "\"" : literal)
    {
        Kind = kind;
        Literal = literal;
        Type = type;
    }

    public static AirConst Int(long v) => new(AirConstKind.Int, v.ToString(System.Globalization.CultureInfo.InvariantCulture), new AirType.Prim(PrimKind.Int));

    public static AirConst Bool(bool v) => new(AirConstKind.Bool, v ? "true" : "false", new AirType.Prim(PrimKind.Bool));

    public static AirConstKind KindOf(string litKind) => litKind switch
    {
        "int" => AirConstKind.Int,
        "float" => AirConstKind.Float,
        "char" => AirConstKind.Char,
        "str" => AirConstKind.String,
        "bool" => AirConstKind.Bool,
        _ => throw new ArgumentException("no AIR literal kind for `" + litKind + "`"),
    };
}

public sealed class AirLocalRef : AirLeafStandIn
{
    public string Name { get; }
    public bool Narrowed { get; }

    public AirLocalRef(string name, AirType? type = null, bool narrowed = false) : base(narrowed ? name + ".Value" : name)
    {
        Name = name;
        Narrowed = narrowed;
        Type = type;
    }
}

public sealed class AirTypeRef : AirLeafStandIn
{
    public AirType Ref { get; }

    public AirTypeRef(AirType type) : base(type.ToCs()) => Ref = type;

    public static AirTypeRef Named(string cs) => new(new AirType.ClrRef(cs, null, []));

    public static AirTypeRef Prelude() => Named("Prelude");
}

public sealed class AirDefault : AirLeafStandIn
{
    public bool Forgiving { get; }

    public AirDefault(AirType? type, bool forgiving = false) : base((type == null ? "default" : "default(" + type.ToCs() + ")") + (forgiving ? "!" : ""))
    {
        Type = type;
        Forgiving = forgiving;
    }
}

/// <summary>Escape hatch: C# text no typed node expresses yet; every construction site says why.</summary>
public sealed class AirRawCs : AirLeafStandIn
{
    public AirRawCs(string cs) : base(cs) { }
}

// ---- vectors ------------------------------------------------------------------------------------------------------

/// <summary>The vector types: Width 0 is the variable-size <c>System.Numerics.Vector&lt;T&gt;</c>, 128/256/512 the
/// explicit <c>System.Runtime.Intrinsics</c> tiers.</summary>
public static class AirVector
{
    public const string Numerics = "global::System.Numerics.Vector";
    const string Intrinsics = "global::System.Runtime.Intrinsics.Vector";

    /// <summary>The static API class of a width (`global::System.Numerics.Vector`, `...Intrinsics.Vector256`).</summary>
    public static string Api(int width) => width == 0 ? Numerics : Intrinsics + width;

    public static int WidthOf(string api) =>
        api == Numerics ? 0 : api.StartsWith(Intrinsics) && int.TryParse(api[Intrinsics.Length..], out int w) ? w : throw new ArgumentException(api);

    /// <summary>`Vector&lt;elem&gt;` of <paramref name="width"/> as an AIR type (prints as the C# vector type).</summary>
    public static AirType.ClrRef Of(AirType elem, int width) => new(Api(width), width switch
    {
        0 => typeof(System.Numerics.Vector<>),
        128 => typeof(System.Runtime.Intrinsics.Vector128<>),
        256 => typeof(System.Runtime.Intrinsics.Vector256<>),
        512 => typeof(System.Runtime.Intrinsics.Vector512<>),
        _ => throw new ArgumentOutOfRangeException(nameof(width)),
    }, [elem]);

    public static AirType.ClrRef Of(AirType elem, string api) => Of(elem, WidthOf(api));

    /// <summary>The lane type of a vector type made by <see cref="Of(AirType, int)"/>.</summary>
    public static AirType Lane(AirType vec) => ((AirType.ClrRef)vec).Args[0];

    public static int Width(AirType vec) => WidthOf(((AirType.ClrRef)vec).Name);

    /// <summary>A primitive lane type from its C# keyword (`double`, `int`, ...).</summary>
    public static AirType.Prim Prim(string cs) => TryPrim(cs) ?? throw new ArgumentException($"no primitive lane type `{cs}`");

    public static AirType.Prim? TryPrim(string cs) => cs switch
    {
        "sbyte" => new(PrimKind.SByte), "byte" => new(PrimKind.Byte), "short" => new(PrimKind.Short),
        "ushort" => new(PrimKind.UShort), "int" => new(PrimKind.Int), "uint" => new(PrimKind.UInt),
        "long" => new(PrimKind.Long), "ulong" => new(PrimKind.ULong), "float" => new(PrimKind.Float),
        "double" => new(PrimKind.Double), "bool" => new(PrimKind.Bool), "char" => new(PrimKind.Char),
        _ => null,
    };
}

/// <summary>Operations of <see cref="AirVectorOp"/>.</summary>
public enum AirVecOp
{
    // constants of the vector type (no arguments); Count is the lane count (int), IsAccelerated a bool
    Zero, One, Indices, Count, IsAccelerated,
    /// <summary>Broadcast of one scalar, converted to the lane type, to every lane.</summary>
    Splat,
    // lane-wise arithmetic
    Add, Sub, Mul, Div, Neg, Abs, Max, Min, Sqrt,
    // lane-wise comparisons (all-bits masks of the same vector type) and mask algebra
    Equals, LessThan, GreaterThan, LessThanOrEqual, GreaterThanOrEqual,
    BitwiseAnd, BitwiseOr, OnesComplement, ConditionalSelect,
    /// <summary>Reinterpret as `Vector&lt;long&gt;` (Vec is the result type).</summary>
    AsVectorInt64,
    /// <summary>All lanes equal (bool).</summary>
    EqualsAll,
}

/// <summary>A lane-wise vector operation on <see cref="Vec"/>.</summary>
public sealed class AirVectorOp : MExpr
{
    public AirVecOp Op;
    public AirType Vec = null!;
    public List<MExpr> Args = new();
}

/// <summary>`Vec.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(Array), (nuint)Index)`: unchecked load of one
/// vector from an array (the caller proved the bounds).</summary>
public sealed class AirVectorLoad : MExpr
{
    public AirType Vec = null!;
    public MExpr Array = null!;
    public MExpr Index = null!;
}

/// <summary>Unchecked store of <see cref="Value"/> into <see cref="Array"/> at <see cref="Index"/>.</summary>
public sealed class AirVectorStore : MExpr
{
    public AirType Vec = null!;
    public MExpr Value = null!;
    public MExpr Array = null!;
    public MExpr Index = null!;
}

public enum AirVecReduce { Sum }

/// <summary>Horizontal reduction of a vector to one lane value, converted to <see cref="CastTo"/> when set.</summary>
public sealed class AirVectorReduce : MExpr
{
    public AirVecReduce Op;
    public AirType Vec = null!;
    public MExpr Arg = null!;
    public AirType? CastTo;
}

/// <summary>How <see cref="AirVectorFits"/> tests a vector loop's bound.</summary>
public enum AirFitsForm
{
    /// <summary>A full vector starts at Index: `Index &lt;= End - Count`.</summary>
    Fits,
    /// <summary>A full vector starting at Index runs past End: `Index &gt; End - Count`.</summary>
    Overruns,
    /// <summary>The vector loop is worth entering: `IsHardwareAccelerated &amp;&amp; End - Index &gt;= Count`.</summary>
    Guard,
}

/// <summary>
/// The full-vector bound test of a vector loop over [Index, End) (End + 1 when <see cref="EndInclusive"/>). A bool;
/// one node so the printer reproduces the unparenthesized C# the lowering always emitted.
/// </summary>
public sealed class AirVectorFits : MExpr
{
    public AirFitsForm Form;
    public AirType Vec = null!;
    public MExpr Index = null!;
    public MExpr End = null!;
    public bool EndInclusive;
}

/// <summary>`stackalloc Elem[Count]`: a method-entry stack buffer (a `Span&lt;Elem&gt;`, ADR 0032 StackAlloc fact).</summary>
public sealed class AirStackAlloc : MExpr
{
    public AirType Elem = null!;
    public MExpr Count = null!;
}
