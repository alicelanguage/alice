using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Crystal Enum methods. `to_i`/`value` live outside Prelude: Prelude's `to_i&lt;T&gt;` is constrained to numbers and C#
/// cannot overload on constraints within one class (resolution drops the candidate whose constraint fails).
/// The integer is read with <see cref="Unsafe"/> by the enum's size: no boxing, no reflection, folded per instantiation.
/// </summary>
public static class EnumValues
{
    /// <summary>The enum's underlying integer (Crystal `Enum#value` / `to_i`). An Int64 enum must fit Int32.</summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int to_i<T>(this T e) where T : struct, Enum => checked((int)Prelude.EnumBits(e));

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int to_i32<T>(this T e) where T : struct, Enum => checked((int)Prelude.EnumBits(e));

    /// <summary>Crystal `Enum#value`: the underlying integer. (Not in Prelude: a bare-T receiver there would claim `value`
    /// on every .NET type and hide `.Value`.)</summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int value<T>(this T e) where T : struct, Enum => checked((int)Prelude.EnumBits(e));

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long to_i64<T>(this T e) where T : struct, Enum => Prelude.EnumBits(e);
}

public static partial class Prelude
{
    /// <summary>The enum's value sign-extended to Int64 (signed underlying types; UInt64 enums wrap).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long EnumBits<T>(T e) where T : struct, Enum
    {
        if (Unsafe.SizeOf<T>() == 4) return Unsafe.As<T, int>(ref e);
        if (Unsafe.SizeOf<T>() == 8) return Unsafe.As<T, long>(ref e);
        if (Unsafe.SizeOf<T>() == 2) return Unsafe.As<T, short>(ref e);
        return Unsafe.As<T, byte>(ref e);
    }

    /// <summary>`@[Flags]`: every bit of <paramref name="flag"/> is set (`includes?` in Crystal, `has_flag` in .NET).</summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool includes_q<T>(this T e, T flag) where T : struct, Enum { long f = EnumBits(flag); return (EnumBits(e) & f) == f; }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool has_flag<T>(this T e, T flag) where T : struct, Enum { long f = EnumBits(flag); return (EnumBits(e) & f) == f; }

    /// <summary>`Color.values`: every member in declaration (value) order.</summary>
    [Pure] public static T[] enum_values<T>() where T : struct, Enum => Enum.GetValues<T>();

    /// <summary>`Color.names`: the member names.</summary>
    [Pure] public static string[] enum_names<T>() where T : struct, Enum => Enum.GetNames<T>();

    /// <summary>`Color.parse("Red")`: by name, case-insensitive (Crystal); ArgumentException when unknown.</summary>
    [Pure]
    public static T enum_parse<T>(string name) where T : struct, Enum =>
        Enum.TryParse<T>(name, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : throw new ArgumentException($"unknown enum {typeof(T).Name} constant: {name}");

    /// <summary>`Color.parse?("x")`: nil when unknown.</summary>
    [Pure]
    public static T? enum_parse_q<T>(string name) where T : struct, Enum => Enum.TryParse<T>(name, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : null;

    /// <summary>`Color.from_value(2)`: the member with that value; ArgumentException when none.</summary>
    [Pure]
    public static T enum_from_value<T>(long value) where T : struct, Enum
    {
        foreach (var v in Enum.GetValues<T>()) if (EnumBits(v) == value) return v;
        throw new ArgumentException($"unknown enum {typeof(T).Name} value: {value}");
    }

    /// <summary>`Color.from_value?(9)`: nil when no member has that value.</summary>
    [Pure]
    public static T? enum_from_value_q<T>(long value) where T : struct, Enum
    {
        foreach (var v in Enum.GetValues<T>()) if (EnumBits(v) == value) return v;
        return null;
    }
}
