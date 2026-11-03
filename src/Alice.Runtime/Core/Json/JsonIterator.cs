using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace Alice.Core.Json;

/// <remarks>
/// Stage 1 runs in the constructor (UTF-8, strings, escapes and control characters are validated there); the
/// grammar is checked lazily as values are read, as in simdjson On-Demand: a skipped value is only checked for
/// balanced brackets. The cursor sits on a structural index; after a value it is on the following `,`, `]` or
/// `}`, which <see cref="NextElement"/> / <see cref="NextProperty"/> consume. 0 bytes allocated once the pool is warm.
/// </remarks>
public ref partial struct JsonIterator
{
    ReadOnlySpan<byte> input;
    uint[]? index;
    byte[]? owned;
    int count, pos, depth;
    readonly int maxDepth;

    public partial JsonIterator(ReadOnlySpan<byte> utf8, JsonParseOptions options)
    {
        utf8 = Input.SkipBom(utf8);
        if (!Utf8.IsValid(utf8)) Input.Validate(utf8.ToArray(), default); // throws with the offset
        maxDepth = options.MaxDepth > 0 ? options.MaxDepth : 1024;
        if (options.AllowComments && utf8.IndexOf((byte)'/') >= 0)
        {
            owned = ArrayPool<byte>.Shared.Rent(utf8.Length);
            utf8.CopyTo(owned);
            try { Input.StripComments(owned.AsSpan(0, utf8.Length)); }
            catch { ArrayPool<byte>.Shared.Return(owned); owned = null; throw; }
            utf8 = owned.AsSpan(0, utf8.Length);
        }
        input = utf8;
        index = ArrayPool<uint>.Shared.Rent(StructuralIndex.Capacity(utf8.Length));
        try
        {
            count = StructuralIndex.Build(utf8, index);
            if (count == 0) throw new JsonParseException("empty document", utf8.Length);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    readonly byte At(int i) => (uint)i < (uint)count ? input[(int)index![i]] : (byte)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    readonly int OffsetOf(int i) => (uint)i < (uint)count ? (int)index![i] : input.Length;

    readonly JsonParseException Error(string message) => new(message, OffsetOf(pos));

    public partial JsonKind PeekKind()
    {
        switch (At(pos))
        {
            case (byte)'{': return JsonKind.Object;
            case (byte)'[': return JsonKind.Array;
            case (byte)'"': return JsonKind.String;
            case (byte)'t' or (byte)'f': return JsonKind.Boolean;
            case (byte)'n': return JsonKind.Null;
            case (byte)'-' or (>= (byte)'0' and <= (byte)'9'):
                return Numbers.QuickKind(input, OffsetOf(pos));
            default: return JsonKind.Undefined;
        }
    }

    public partial bool TryReadNull()
    {
        if (At(pos) != 'n') return false;
        Literal("null"u8);
        return true;
    }

    public partial bool ReadBoolean()
    {
        byte c = At(pos);
        if (c == 't') { Literal("true"u8); return true; }
        if (c == 'f') { Literal("false"u8); return false; }
        throw Error("expected true or false");
    }

    void Literal(ReadOnlySpan<byte> lit)
    {
        int p = OffsetOf(pos);
        if (p + lit.Length > input.Length || !input.Slice(p, lit.Length).SequenceEqual(lit) || !Chars.EndsScalar(input, p + lit.Length))
            throw Error("invalid literal");
        pos++;
    }

    JsonKind Number(out ulong raw)
    {
        byte c = At(pos);
        if (c != '-' && (uint)(c - '0') > 9) throw Error("expected a number");
        if (Numbers.Parse(input, OffsetOf(pos), out var kind, out raw) < 0) throw Error("invalid number");
        pos++;
        return kind;
    }

    public partial long ReadInt64()
    {
        int at = pos;
        var kind = Number(out ulong raw);
        if (kind == JsonKind.Int64) return (long)raw;
        double d = BitConverter.UInt64BitsToDouble(raw);
        if (kind == JsonKind.Float64 && d >= -9223372036854775808.0 && d < 9223372036854775808.0 && Math.Floor(d) == d) return (long)d;
        throw new JsonParseException("number does not fit Int64", OffsetOf(at));
    }

    public partial ulong ReadUInt64()
    {
        int at = pos;
        var kind = Number(out ulong raw);
        if (kind == JsonKind.UInt64 || (kind == JsonKind.Int64 && (long)raw >= 0)) return raw;
        double d = BitConverter.UInt64BitsToDouble(raw);
        if (kind == JsonKind.Float64 && d >= 0 && d < 18446744073709551616.0 && Math.Floor(d) == d) return (ulong)d;
        throw new JsonParseException("number does not fit UInt64", OffsetOf(at));
    }

    public partial double ReadDouble() => Number(out ulong raw) switch
    {
        JsonKind.Int64 => (long)raw,
        JsonKind.UInt64 => raw,
        _ => BitConverter.UInt64BitsToDouble(raw),
    };

    public partial string ReadString() => Strings.Materialize(ReadRawString(out _));

    public partial ReadOnlySpan<byte> ReadRawString(out bool hasEscapes)
    {
        if (At(pos) != '"') throw Error("expected a string");
        var raw = RawStringAt(pos);
        pos++;
        hasEscapes = Strings.HasEscapes(raw);
        return raw;
    }

    /// <summary>Content of the string whose opening quote is structural <paramref name="i"/>.</summary>
    readonly ReadOnlySpan<byte> RawStringAt(int i)
    {
        int q = (int)index![i];
        int close = OffsetOf(i + 1) - 1;
        while (input[close] != '"') close--;
        return input.Slice(q + 1, close - q - 1);
    }

    public partial void ReadStartObject() => Open((byte)'{', "expected an object");

    public partial void ReadStartArray() => Open((byte)'[', "expected an array");

    void Open(byte c, string message)
    {
        if (At(pos) != c) throw Error(message);
        if (++depth > maxDepth) throw Error("nesting deeper than " + maxDepth);
        pos++;
    }

    public partial bool NextProperty(out ReadOnlySpan<byte> rawName)
    {
        rawName = default;
        byte c = At(pos);
        byte prev = At(pos - 1);
        if (c == '}')
        {
            if (prev == ',') throw Error("trailing comma");
            pos++;
            depth--;
            return false;
        }
        if (c == ',')
        {
            if (prev == '{') throw Error("expected a property name");
            pos++;
        }
        else if (prev != '{') throw Error("expected ',' or '}'");
        if (At(pos) != '"') throw Error("expected a property name");
        rawName = RawStringAt(pos);
        if (At(pos + 1) != ':') { pos++; throw Error("expected ':'"); }
        pos += 2;
        return true;
    }

    public partial bool NextElement()
    {
        byte c = At(pos);
        byte prev = At(pos - 1);
        if (c == ']')
        {
            if (prev == ',') throw Error("trailing comma");
            pos++;
            depth--;
            return false;
        }
        if (c == ',')
        {
            if (prev == '[') throw Error("expected a value");
            pos++;
            return true;
        }
        if (prev != '[') throw Error("expected ',' or ']'");
        return true;
    }

    public partial void Skip()
    {
        byte c = At(pos);
        switch (c)
        {
            case (byte)'{' or (byte)'[':
                {
                    int d = 0;
                    do
                    {
                        if (pos >= count) throw Error("unexpected end of input");
                        byte b = At(pos++);
                        if (b is (byte)'{' or (byte)'[') d++;
                        else if (b is (byte)'}' or (byte)']') d--;
                    } while (d > 0);
                    return;
                }
            case (byte)'"': pos++; return;
            case (byte)'t': Literal("true"u8); return;
            case (byte)'f': Literal("false"u8); return;
            case (byte)'n': Literal("null"u8); return;
            default: Number(out _); return;
        }
    }

    public partial int Depth { get => depth; }

    public partial long Offset { get => OffsetOf(pos); }

    public partial void Dispose()
    {
        if (index != null) { ArrayPool<uint>.Shared.Return(index); index = null; }
        if (owned != null) { ArrayPool<byte>.Shared.Return(owned); owned = null; }
        count = 0;
        input = default;
    }

    /// <summary>Throws unless every structural was consumed (whole-document typed reads).</summary>
    internal readonly void EnsureEnd()
    {
        if (pos != count) throw Error("trailing content after the root value");
    }

    /// <summary>Saves the cursor (look-ahead, e.g. a sum-type tag that is not the first property).</summary>
    internal readonly (int Pos, int Depth) Mark() => (pos, depth);

    /// <summary>Restores a <see cref="Mark"/>.</summary>
    internal void Reset((int Pos, int Depth) mark) => (pos, depth) = mark;
}
