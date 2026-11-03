using System.Globalization;
using System.Runtime.CompilerServices;

namespace Alice.Core.Text;

/// <summary>Kind of a <see cref="FormatArg"/>.</summary>
public enum FormatArgKind : byte
{
    /// <summary>`nil`.</summary>
    Null,
    /// <summary>A signed integer.</summary>
    Int,
    /// <summary>An unsigned integer above <see cref="long.MaxValue"/>.</summary>
    UInt,
    /// <summary>A floating-point number.</summary>
    Float,
    /// <summary>A UTF-16 code unit.</summary>
    Char,
    /// <summary>A Boolean.</summary>
    Bool,
    /// <summary>A .NET string.</summary>
    String,
    /// <summary>A <see cref="Utf8String"/>.</summary>
    Utf8,
    /// <summary>Any other object (formatted through the Alice `to_s`).</summary>
    Object,
}

/// <summary>
/// One argument of <see cref="Format"/>: a tagged value, so `params ReadOnlySpan&lt;FormatArg&gt;` passes numbers
/// without boxing and without an <c>object[]</c> (0 bytes per call). Implicit conversions from the primitive types,
/// strings and <see cref="Utf8String"/>; <see cref="From(object?)"/> unboxes a boxed primitive.
/// </summary>
public readonly struct FormatArg
{
    readonly long bits;
    readonly object? obj;

    /// <summary>The kind.</summary>
    public FormatArgKind Kind { get; }

    FormatArg(FormatArgKind k, long bits, object? obj) { Kind = k; this.bits = bits; this.obj = obj; }

    internal long Int => bits;
    internal ulong UInt => (ulong)bits;
    internal double Float => BitConverter.Int64BitsToDouble(bits);
    internal char Char => (char)bits;
    internal bool Bool => bits != 0;
    internal string? Str => obj as string;
    internal byte[]? Utf8Bytes => obj as byte[];
    internal object? Obj => obj;

    /// <summary>`nil`.</summary>
    public static FormatArg Null => default;

#pragma warning disable CS1591 // conversions are self-describing
    public static implicit operator FormatArg(int v) => new(FormatArgKind.Int, v, null);
    public static implicit operator FormatArg(long v) => new(FormatArgKind.Int, v, null);
    public static implicit operator FormatArg(short v) => new(FormatArgKind.Int, v, null);
    public static implicit operator FormatArg(sbyte v) => new(FormatArgKind.Int, v, null);
    public static implicit operator FormatArg(byte v) => new(FormatArgKind.Int, v, null);
    public static implicit operator FormatArg(ushort v) => new(FormatArgKind.Int, v, null);
    public static implicit operator FormatArg(uint v) => new(FormatArgKind.Int, v, null);
    public static implicit operator FormatArg(ulong v) => v <= long.MaxValue ? new(FormatArgKind.Int, (long)v, null) : new(FormatArgKind.UInt, (long)v, null);
    public static implicit operator FormatArg(double v) => new(FormatArgKind.Float, BitConverter.DoubleToInt64Bits(v), null);
    public static implicit operator FormatArg(float v) => (double)v;
    public static implicit operator FormatArg(decimal v) => (double)v;
    public static implicit operator FormatArg(char v) => new(FormatArgKind.Char, v, null);
    public static implicit operator FormatArg(bool v) => new(FormatArgKind.Bool, v ? 1 : 0, null);
    public static implicit operator FormatArg(string? v) => v is null ? default : new(FormatArgKind.String, 0, v);
    public static implicit operator FormatArg(Utf8String v) => new(FormatArgKind.Utf8, 0, v.Bytes ?? Array.Empty<byte>());
#pragma warning restore CS1591

    /// <summary>A boxed value: primitives are unboxed to their kind, anything else is kept as an object.</summary>
    public static FormatArg From(object? o) => o switch
    {
        null => default,
        int v => v,
        long v => v,
        double v => v,
        string v => v,
        char v => v,
        bool v => v,
        float v => v,
        short v => v,
        byte v => v,
        sbyte v => v,
        ushort v => v,
        uint v => v,
        ulong v => v,
        decimal v => v,
        Utf8String v => v,
        _ => new FormatArg(FormatArgKind.Object, 0, o),
    };

    /// <summary>Generic, boxing-free conversion for value types the JIT specializes (used by Prelude sugar).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FormatArg Of<T>(T v)
    {
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref v);
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref v);
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref v);
        if (typeof(T) == typeof(char)) return Unsafe.As<T, char>(ref v);
        if (typeof(T) == typeof(bool)) return Unsafe.As<T, bool>(ref v);
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref v);
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref v);
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref v);
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref v);
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref v);
        return From(v);
    }

    /// <summary>Invariant text of the value (diagnostics).</summary>
    public override string ToString() => Kind switch
    {
        FormatArgKind.Null => "",
        FormatArgKind.Int => Int.ToString(CultureInfo.InvariantCulture),
        FormatArgKind.UInt => UInt.ToString(CultureInfo.InvariantCulture),
        FormatArgKind.Float => Float.ToString("R", CultureInfo.InvariantCulture),
        FormatArgKind.Char => Char.ToString(),
        FormatArgKind.Bool => Bool ? "true" : "false",
        FormatArgKind.String => Str!,
        FormatArgKind.Utf8 => System.Text.Encoding.UTF8.GetString(Utf8Bytes!),
        _ => obj?.ToString() ?? "",
    };
}
