using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Core.Json;

/// <summary>
/// Stage 2: the simdjson tape. One 64-bit word per value (two for numbers), tag in the top byte:
/// <list type="bullet">
/// <item>`r` root (first word: payload = tape length; last word: 0).</item>
/// <item>`{` / `[`: bits 0-31 = index after the matching close, bits 32-55 = element count (saturates at 0xFFFFFF);
/// `}` / `]`: index of the open word.</item>
/// <item>`"` string with unknown escapes, `s` string known clean, `S` string unescaped into the document arena:
/// bits 0-31 = byte offset of the first content byte (input or arena), bits 32-55 = length (0xFFFFFF = longer:
/// recomputed by scanning).</item>
/// <item>`l` Int64, `u` UInt64, `d` Float64: bits 0-31 = offset of the number text; the next word is the value.</item>
/// <item>`t`, `f`, `n`: literals.</item>
/// </list>
/// Strings are zero-copy (offsets into the document's input); escapes are decoded on first access.
/// </summary>
static class Tape
{
    public const byte Root = (byte)'r', StartObject = (byte)'{', EndObject = (byte)'}', StartArray = (byte)'[', EndArray = (byte)']';
    public const byte String = (byte)'"', CleanString = (byte)'s', ArenaString = (byte)'S';
    public const byte Int64 = (byte)'l', UInt64 = (byte)'u', Double = (byte)'d', True = (byte)'t', False = (byte)'f', Null = (byte)'n';
    public const int LongMark = 0xFFFFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Tag(ulong w) => (byte)(w >> 56);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Low(ulong w) => (int)(uint)w;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mid(ulong w) => (int)((w >> 32) & 0xFFFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Word(byte tag, int mid, int low) => ((ulong)tag << 56) | ((ulong)(uint)mid << 32) | (uint)low;

    /// <summary>Kind of a tag.</summary>
    public static JsonKind KindOf(byte tag) => tag switch
    {
        StartObject => JsonKind.Object,
        StartArray => JsonKind.Array,
        String or CleanString or ArenaString => JsonKind.String,
        Int64 => JsonKind.Int64,
        UInt64 => JsonKind.UInt64,
        Double => JsonKind.Float64,
        True or False => JsonKind.Boolean,
        Null => JsonKind.Null,
        _ => JsonKind.Undefined,
    };

    /// <summary>Capacity of the tape for <paramref name="structurals"/> structural characters.</summary>
    public static int Capacity(int structurals) => 2 * structurals + 4;

    /// <summary>
    /// Builds the tape from the structural index. <paramref name="padded"/> is the input followed by at least one
    /// 0 byte at <paramref name="len"/> (the sentinel position <c>idx[count]</c>). Returns the tape length.
    /// </summary>
    public static int Build(ReadOnlySpan<byte> padded, int len, ReadOnlySpan<uint> idx, int count, Span<ulong> tape, int maxDepth)
    {
        if (count == 0) throw new JsonParseException("empty document", len);
        // Bounds: idx has count + 1 entries (sentinel), padded has len + 1 bytes, tape has Capacity(count) words
        // (at most 2 per structural + 2), open/counts maxDepth + 1 slots (checked on push): the loop below indexes
        // through refs without per-access checks.
        if (padded.Length <= len || idx.Length <= count || tape.Length < Capacity(count)) throw new ArgumentException("buffers too small");
        var input = padded.Slice(0, len);
        int[]? rented = null;
        int slots = maxDepth + 1;
        Span<int> stack = slots <= 1025 ? stackalloc int[2 * 1025] : (rented = ArrayPool<int>.Shared.Rent(2 * slots));
        try
        {
            ref byte buf = ref MemoryMarshal.GetReference(padded);
            ref uint ix = ref MemoryMarshal.GetReference(idx);
            ref ulong tp = ref MemoryMarshal.GetReference(tape);
            ref int open = ref MemoryMarshal.GetReference(stack);
            ref int counts = ref Unsafe.Add(ref open, slots);
            int i = 0, t = 1, depth = 0, p, o;
            bool inObject = false; // kind of the innermost open container
            byte c;

        Value:
            p = (int)Unsafe.Add(ref ix, i);
            c = Unsafe.Add(ref buf, p);
            switch (c)
            {
                case (byte)'{':
                    if (++depth > maxDepth) throw new JsonParseException("nesting deeper than " + maxDepth, p);
                    Unsafe.Add(ref open, depth) = t;
                    Unsafe.Add(ref counts, depth) = 0;
                    Unsafe.Add(ref tp, t++) = (ulong)StartObject << 56;
                    inObject = true;
                    p = (int)Unsafe.Add(ref ix, ++i);
                    c = Unsafe.Add(ref buf, p);
                    if (c == '"') goto Key;
                    if (c == '}') { i++; goto CloseObject; }
                    throw new JsonParseException("expected a property name or '}'", p);
                case (byte)'[':
                    if (++depth > maxDepth) throw new JsonParseException("nesting deeper than " + maxDepth, p);
                    Unsafe.Add(ref open, depth) = t;
                    Unsafe.Add(ref tp, t++) = (ulong)StartArray << 56;
                    inObject = false;
                    p = (int)Unsafe.Add(ref ix, ++i);
                    if (Unsafe.Add(ref buf, p) == ']') { Unsafe.Add(ref counts, depth) = 0; i++; goto CloseArray; }
                    Unsafe.Add(ref counts, depth) = 1;
                    goto Value;
                case (byte)'"':
                    Unsafe.Add(ref tp, t++) = StringWord(ref buf, p, (int)Unsafe.Add(ref ix, i + 1));
                    i++;
                    goto After;
                case (byte)'t':
                    if (!Literal(input, p, 0x65757274u, 4)) throw new JsonParseException("invalid literal", p); // "true"
                    Unsafe.Add(ref tp, t++) = (ulong)True << 56;
                    i++;
                    goto After;
                case (byte)'f':
                    if (!Literal(input, p + 1, 0x65736C61u, 4)) throw new JsonParseException("invalid literal", p); // "alse"
                    Unsafe.Add(ref tp, t++) = (ulong)False << 56;
                    i++;
                    goto After;
                case (byte)'n':
                    if (!Literal(input, p, 0x6C6C756Eu, 4)) throw new JsonParseException("invalid literal", p); // "null"
                    Unsafe.Add(ref tp, t++) = (ulong)Null << 56;
                    i++;
                    goto After;
                default:
                    {
                        if (Numbers.Parse(input, p, out var kind, out ulong raw) < 0)
                            throw new JsonParseException(i == count ? "unexpected end of input" : "unexpected character", p);
                        byte tag = kind == JsonKind.Int64 ? Int64 : kind == JsonKind.UInt64 ? UInt64 : Double;
                        Unsafe.Add(ref tp, t) = ((ulong)tag << 56) | (uint)p;
                        Unsafe.Add(ref tp, t + 1) = raw;
                        t += 2;
                        i++;
                        goto After;
                    }
            }

        Key:
            Unsafe.Add(ref tp, t++) = StringWord(ref buf, p, (int)Unsafe.Add(ref ix, i + 1));
            p = (int)Unsafe.Add(ref ix, ++i);
            if (Unsafe.Add(ref buf, p) != ':') throw new JsonParseException("expected ':'", p);
            i++;
            Unsafe.Add(ref counts, depth)++;
            goto Value;

        After:
            if (depth == 0) goto End;
            p = (int)Unsafe.Add(ref ix, i);
            c = Unsafe.Add(ref buf, p);
            if (inObject)
            {
                if (c == ',')
                {
                    p = (int)Unsafe.Add(ref ix, ++i);
                    if (Unsafe.Add(ref buf, p) != '"') throw new JsonParseException("expected a property name", p);
                    goto Key;
                }
                if (c == '}') { i++; goto CloseObject; }
                throw new JsonParseException("expected ',' or '}'", p);
            }
            if (c == ',') { i++; Unsafe.Add(ref counts, depth)++; goto Value; }
            if (c == ']') { i++; goto CloseArray; }
            throw new JsonParseException("expected ',' or ']'", p);

        CloseObject:
            o = Unsafe.Add(ref open, depth);
            Unsafe.Add(ref tp, o) = Word(StartObject, Math.Min(Unsafe.Add(ref counts, depth), LongMark), t + 1);
            Unsafe.Add(ref tp, t++) = Word(EndObject, 0, o);
            depth--;
            inObject = depth > 0 && Tag(Unsafe.Add(ref tp, Unsafe.Add(ref open, depth))) == StartObject;
            goto After;

        CloseArray:
            o = Unsafe.Add(ref open, depth);
            Unsafe.Add(ref tp, o) = Word(StartArray, Math.Min(Unsafe.Add(ref counts, depth), LongMark), t + 1);
            Unsafe.Add(ref tp, t++) = Word(EndArray, 0, o);
            depth--;
            inObject = depth > 0 && Tag(Unsafe.Add(ref tp, Unsafe.Add(ref open, depth))) == StartObject;
            goto After;

        End:
            if (i != count) throw new JsonParseException("trailing content after the root value", (int)idx[i]);
            tape[0] = Word(Root, 0, t + 1);
            tape[t++] = (ulong)Root << 56;
            return t;
        }
        finally
        {
            if (rented != null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong StringWord(ref byte buf, int q, int next)
    {
        int close = next - 1;
        while (Unsafe.Add(ref buf, close) != '"') close--;
        return Word(String, Math.Min(close - q - 1, LongMark), q + 1);
    }

    /// <summary>The 4 bytes at <paramref name="p"/> equal <paramref name="word"/> (little-endian) and the scalar
    /// ends right after them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Literal(ReadOnlySpan<byte> input, int p, uint word, int length)
        => p + length <= input.Length
           && Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(input), p)) == word
           && Chars.EndsScalar(input, p + length);

    /// <summary>Content length of the string whose content starts at <paramref name="start"/> (scan for the
    /// closing quote, skipping escapes).</summary>
    public static int ScanStringLength(ReadOnlySpan<byte> input, int start)
    {
        int p = start;
        while (true)
        {
            int k = input.Slice(p).IndexOfAny((byte)'"', (byte)'\\');
            if (k < 0) return input.Length - start;
            p += k;
            if (input[p] == '"') return p - start;
            p += 2;
        }
    }
}
