using System.Text;

namespace Alice.Core.Text;

// Frozen contract (ADR 0036): engines (regex Tier R, prefilters, Utf8String methods) are written once, generic over
// the input encoding, and the JIT specializes them per encoding. Usage:
//   static bool IsMatch<TInput, TUnit>(scoped TInput input) where TInput : IInput<TUnit>, allows ref struct
//                                                            where TUnit : unmanaged, IBinaryInteger<TUnit>

/// <summary>A span haystack in one encoding: UTF-8 (<see cref="Utf8Input"/>) or UTF-16 (<see cref="Utf16Input"/>).
/// Static members carry the encoding rules, so generic code calls them without virtual dispatch or boxing.</summary>
/// <typeparam name="TUnit">`byte` (UTF-8) or `char` (UTF-16).</typeparam>
public interface IInput<TUnit> where TUnit : unmanaged
{
    /// <summary>The code units searched.</summary>
    ReadOnlySpan<TUnit> Units { get; }

    /// <summary>True for UTF-8 input.</summary>
    static abstract bool IsUtf8 { get; }

    /// <summary>Decodes the scalar starting at <paramref name="index"/> and returns how many units it spans
    /// (&gt;= 1; 0 at the end). Ill-formed sequences decode as U+FFFD covering the maximal invalid subsequence.</summary>
    static abstract int DecodeAt(ReadOnlySpan<TUnit> units, int index, out Rune rune);

    /// <summary>Decodes the scalar ending just before <paramref name="index"/> (`\b`, reverse search); returns its
    /// width in units, 0 at the start.</summary>
    static abstract int DecodeBefore(ReadOnlySpan<TUnit> units, int index, out Rune rune);
}

/// <summary>UTF-8 input (files, sockets, JSON strings) searched without transcoding.</summary>
public readonly ref struct Utf8Input : IInput<byte>
{
    /// <summary>Wraps a UTF-8 span.</summary>
    public Utf8Input(ReadOnlySpan<byte> units) => Units = units;

    /// <inheritdoc/>
    public ReadOnlySpan<byte> Units { get; }

    /// <inheritdoc/>
    public static bool IsUtf8 => true;

    /// <inheritdoc/>
    public static int DecodeAt(ReadOnlySpan<byte> units, int index, out Rune rune)
    {
        if ((uint)index >= (uint)units.Length) { rune = default; return 0; }
        Rune.DecodeFromUtf8(units[index..], out rune, out int n);
        return n;
    }

    /// <inheritdoc/>
    public static int DecodeBefore(ReadOnlySpan<byte> units, int index, out Rune rune)
    {
        if (index <= 0) { rune = default; return 0; }
        Rune.DecodeLastFromUtf8(units[..index], out rune, out int n);
        return n;
    }
}

/// <summary>UTF-16 input (`string`, `ReadOnlySpan&lt;char&gt;`).</summary>
public readonly ref struct Utf16Input : IInput<char>
{
    /// <summary>Wraps a UTF-16 span.</summary>
    public Utf16Input(ReadOnlySpan<char> units) => Units = units;

    /// <inheritdoc/>
    public ReadOnlySpan<char> Units { get; }

    /// <inheritdoc/>
    public static bool IsUtf8 => false;

    /// <inheritdoc/>
    public static int DecodeAt(ReadOnlySpan<char> units, int index, out Rune rune)
    {
        if ((uint)index >= (uint)units.Length) { rune = default; return 0; }
        Rune.DecodeFromUtf16(units[index..], out rune, out int n);
        return n;
    }

    /// <inheritdoc/>
    public static int DecodeBefore(ReadOnlySpan<char> units, int index, out Rune rune)
    {
        if (index <= 0) { rune = default; return 0; }
        Rune.DecodeLastFromUtf16(units[..index], out rune, out int n);
        return n;
    }
}
