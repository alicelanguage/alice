namespace Alice.Compiler.Semantics;

/// <summary>
/// A resolved Alice type. Identity is the C# spelling (<see cref="Cs"/>); <see cref="Clr"/> is set when
/// the type exists in a referenced .NET assembly, <see cref="Sym"/> when it is declared in Alice source.
/// </summary>
public sealed class AType
{
    public string Kind;        // prim class struct iface enum array generic param dynamic unknown void null tuple func
    public string Cs;
    public Type? Clr;
    public TypeSym? Sym;
    public List<AType> Args = new();
    public AType? Elem;
    public bool Nullable;

    public AType(string kind, string cs, Type? clr = null)
    {
        Kind = kind;
        Cs = cs;
        Clr = clr;
    }

    public static readonly AType Unknown = new("unknown", "var");
    public static readonly AType Void = new("void", "void", typeof(void));
    public static readonly AType Null = new("null", "object");
    public static readonly AType Dynamic = new("dynamic", "dynamic");
    public static readonly AType Object = new("class", "object", typeof(object));
    public static readonly AType Bool = new("prim", "bool", typeof(bool));
    public static readonly AType Char = new("prim", "char", typeof(char));
    public static readonly AType String = new("class", "string", typeof(string));
    public static readonly AType SByte = new("prim", "sbyte", typeof(sbyte));
    public static readonly AType Byte = new("prim", "byte", typeof(byte));
    public static readonly AType Short = new("prim", "short", typeof(short));
    public static readonly AType UShort = new("prim", "ushort", typeof(ushort));
    public static readonly AType Int = new("prim", "int", typeof(int));
    public static readonly AType UInt = new("prim", "uint", typeof(uint));
    public static readonly AType Long = new("prim", "long", typeof(long));
    public static readonly AType ULong = new("prim", "ulong", typeof(ulong));
    public static readonly AType Float = new("prim", "float", typeof(float));
    public static readonly AType Double = new("prim", "double", typeof(double));
    public static readonly AType Decimal = new("prim", "decimal", typeof(decimal));

    public static readonly AType[] Prims = [Bool, Char, String, Object, SByte, Byte, Short, UShort, Int, UInt, Long, ULong, Float, Double, Decimal];

    public bool IsUnknown => Kind == "unknown";
    public bool IsKnown => Kind != "unknown" && Kind != "dynamic";
    public bool IsVoid => Kind == "void";
    public bool IsString => Cs == "string";
    public bool IsBool => Cs == "bool";
    public bool IsNumeric => NumericRank > 0;
    public bool IsInteger => NumericRank is > 0 and <= 8;

    /// <summary>Promotion order for implicit numeric widening.</summary>
    public int NumericRank => Cs switch
    {
        "sbyte" => 1,
        "byte" => 2,
        "short" => 3,
        "ushort" => 4,
        "int" => 5,
        "uint" => 6,
        "long" => 7,
        "ulong" => 8,
        "float" => 9,
        "double" => 10,
        "decimal" => 11,
        _ => 0,
    };

    public static AType Array(AType elem)
    {
        var a = new AType("array", elem.Cs + "[]", elem.Clr?.MakeArrayType()) { Elem = elem };
        return a;
    }

    public AType AsNullable()
    {
        if (Nullable || Kind is "unknown" or "dynamic" or "void") return this;
        return new AType(Kind, Cs + "?", Clr) { Sym = Sym, Args = Args, Elem = Elem, Nullable = true };
    }

    public bool Same(AType o) => Cs == o.Cs;

    public override string ToString() => Cs;
}
