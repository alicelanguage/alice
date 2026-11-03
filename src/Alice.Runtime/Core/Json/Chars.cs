using System.Runtime.CompilerServices;

namespace Alice.Core.Json;

/// <summary>Byte classes shared by stage 1, stage 2, the iterator and the writer.</summary>
static class Chars
{
    /// <summary>1 for bytes that may follow a scalar (number or literal): JSON whitespace, `,:[]{}` and `"`.</summary>
    static ReadOnlySpan<byte> TerminatorTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 0, 0, // \t \n \r
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, // ' ' '"' ','
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, // ':'
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, // '[' ']'
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, // '{' '}'
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>True when <paramref name="b"/> can end a number or literal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTerminator(byte b) => TerminatorTable[b] != 0;

    /// <summary>True when the scalar ending at <paramref name="end"/> is followed by a terminator or the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EndsScalar(ReadOnlySpan<byte> buf, int end) => (uint)end >= (uint)buf.Length || TerminatorTable[buf[end]] != 0;

    /// <summary>JSON whitespace (RFC 8259: space, tab, LF, CR).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';

    /// <summary>Hex digit value or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hex(byte b)
    {
        uint d = (uint)(b - '0');
        if (d <= 9) return (int)d;
        uint l = (uint)((b | 0x20) - 'a');
        return l <= 5 ? (int)l + 10 : -1;
    }
}
