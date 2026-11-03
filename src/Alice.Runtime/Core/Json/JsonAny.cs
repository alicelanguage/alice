using System.Buffers;
using System.Text;

namespace Alice.Core.Json;

public readonly partial struct JsonAny
{
    ulong WordAt => document!.Word(index);

    byte TagAt => document is null ? (byte)0 : Tape.Tag(document.Word(index));

    public partial JsonKind Kind { get => document is null ? JsonKind.Undefined : Tape.KindOf(Tape.Tag(document.Word(index))); }

    public partial JsonAny this[string name] { get => TryGetProperty(name.AsSpan(), out var v) ? v : default; }

    public partial JsonAny this[ReadOnlySpan<byte> utf8Name] { get => TryGetProperty(utf8Name, out var v) ? v : default; }

    /// <remarks>Negative indexes count from the end (Ruby).</remarks>
    public partial JsonAny this[int index]
    {
        get
        {
            if (TagAt != Tape.StartArray) return default;
            var doc = document!;
            if (index < 0)
            {
                index += GetArrayLength();
                if (index < 0) return default;
            }
            int k = this.index + 1;
            for (int j = 0; j < index; j++)
            {
                if (Tape.Tag(doc.Word(k)) == Tape.EndArray) return default;
                k = doc.Next(k);
            }
            return Tape.Tag(doc.Word(k)) == Tape.EndArray ? default : new JsonAny(doc, k);
        }
    }

    /// <remarks>With duplicate names the last one wins (as System.Text.Json and Ruby's Hash).</remarks>
    public partial bool TryGetProperty(ReadOnlySpan<byte> utf8Name, out JsonAny value)
    {
        value = default;
        if (TagAt != Tape.StartObject) return false;
        var doc = document!;
        int k = index + 1, found = -1;
        while (Tape.Tag(doc.Word(k)) != Tape.EndObject)
        {
            var raw = doc.RawString(k, out bool clean);
            if (clean ? raw.SequenceEqual(utf8Name) : Strings.RawEquals(raw, utf8Name)) found = k + 1;
            k = doc.Next(k + 1);
        }
        if (found < 0) return false;
        value = new JsonAny(doc, found);
        return true;
    }

    public partial bool TryGetProperty(ReadOnlySpan<char> name, out JsonAny value)
    {
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        byte[]? rented = null;
        Span<byte> tmp = max <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            int n = Encoding.UTF8.GetBytes(name, tmp);
            return TryGetProperty(tmp.Slice(0, n), out value);
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public partial int GetArrayLength() => Count(Tape.StartArray, "array");

    public partial int GetPropertyCount() => Count(Tape.StartObject, "object");

    int Count(byte tag, string what)
    {
        if (TagAt != tag) throw Mismatch(what);
        int c = Tape.Mid(WordAt);
        if (c != Tape.LongMark) return c;
        var doc = document!;
        byte close = tag == Tape.StartArray ? Tape.EndArray : Tape.EndObject;
        c = 0;
        for (int k = index + 1; Tape.Tag(doc.Word(k)) != close; k = doc.Next(tag == Tape.StartArray ? k : k + 1)) c++;
        return c;
    }

    ulong Payload => document!.Word(index + 1);

    public partial long GetInt64() => TryGetInt64(out long v) ? v : throw Mismatch("Int64");

    public partial ulong GetUInt64()
    {
        switch (TagAt)
        {
            case Tape.Int64:
                long l = (long)Payload;
                if (l >= 0) return (ulong)l;
                break;
            case Tape.UInt64:
                return Payload;
            case Tape.Double:
                double d = BitConverter.UInt64BitsToDouble(Payload);
                if (d >= 0 && d < 18446744073709551616.0 && Math.Floor(d) == d) return (ulong)d;
                break;
        }
        throw Mismatch("UInt64");
    }

    public partial double GetDouble() => TryGetDouble(out double v) ? v : throw Mismatch("number");

    public partial bool GetBoolean() => TagAt switch
    {
        Tape.True => true,
        Tape.False => false,
        _ => throw Mismatch("Boolean"),
    };

    public partial string GetString()
    {
        if (Kind != JsonKind.String) throw Mismatch("String");
        return Encoding.UTF8.GetString(document!.Utf8String(index));
    }

    public partial ReadOnlySpan<byte> GetUtf8()
    {
        if (Kind != JsonKind.String) throw Mismatch("String");
        return document!.Utf8String(index);
    }

    public partial bool TryGetInt64(out long value)
    {
        switch (TagAt)
        {
            case Tape.Int64:
                value = (long)Payload;
                return true;
            case Tape.Double:
                double d = BitConverter.UInt64BitsToDouble(Payload);
                if (d >= -9223372036854775808.0 && d < 9223372036854775808.0 && Math.Floor(d) == d)
                {
                    value = (long)d;
                    return true;
                }
                break;
        }
        value = 0;
        return false;
    }

    public partial bool TryGetDouble(out double value)
    {
        switch (TagAt)
        {
            case Tape.Int64: value = (long)Payload; return true;
            case Tape.UInt64: value = Payload; return true;
            case Tape.Double: value = BitConverter.UInt64BitsToDouble(Payload); return true;
        }
        value = 0;
        return false;
    }

    public partial ArrayEnumerator EnumerateArray()
    {
        if (TagAt != Tape.StartArray) throw Mismatch("array");
        return new ArrayEnumerator(document!, index);
    }

    public partial ObjectEnumerator EnumerateObject()
    {
        if (TagAt != Tape.StartObject) throw Mismatch("object");
        return new ObjectEnumerator(document!, index);
    }

    public partial void WriteTo(ref JsonWriter writer)
    {
        if (document is null) throw new InvalidOperationException("cannot write an undefined JSON value");
        Write(document, index, ref writer);
    }

    static void Write(JsonDocument doc, int k, ref JsonWriter w)
    {
        ulong word = doc.Word(k);
        switch (Tape.Tag(word))
        {
            case Tape.StartObject:
                w.WriteStartObject();
                for (int p = k + 1; Tape.Tag(doc.Word(p)) != Tape.EndObject; p = doc.Next(p + 1))
                {
                    WriteStringAt(doc, p, ref w, name: true);
                    Write(doc, p + 1, ref w);
                }
                w.WriteEndObject();
                break;
            case Tape.StartArray:
                w.WriteStartArray();
                for (int p = k + 1; Tape.Tag(doc.Word(p)) != Tape.EndArray; p = doc.Next(p)) Write(doc, p, ref w);
                w.WriteEndArray();
                break;
            case Tape.String or Tape.CleanString or Tape.ArenaString:
                WriteStringAt(doc, k, ref w, name: false);
                break;
            case Tape.Int64 or Tape.UInt64 or Tape.Double:
                w.WriteRawNumber(doc.NumberText(k));
                break;
            case Tape.True: w.WriteBoolean(true); break;
            case Tape.False: w.WriteBoolean(false); break;
            default: w.WriteNull(); break;
        }
    }

    static void WriteStringAt(JsonDocument doc, int k, ref JsonWriter w, bool name)
    {
        // input strings are already valid JSON string contents: copied as is; arena copies are unescaped
        if (Tape.Tag(doc.Word(k)) == Tape.ArenaString)
        {
            var s = doc.RawString(k, out _);
            if (name) w.WritePropertyNameEscaped(s); else w.WriteString(s);
        }
        else
        {
            var raw = doc.RawString(k, out _);
            if (name) w.WriteRawPropertyName(raw); else w.WriteRawString(raw);
        }
    }

    public partial string ToJson()
    {
        if (document is null) return "";
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(buf);
        WriteTo(ref w);
        w.Flush();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    public partial Dictionary<string, object?> ToDictionary()
    {
        if (TagAt != Tape.StartObject) throw Mismatch("object");
        return (Dictionary<string, object?>)ToObject(document!, index)!;
    }

    public partial object?[] ToArray()
    {
        if (TagAt != Tape.StartArray) throw Mismatch("array");
        return (object?[])ToObject(document!, index)!;
    }

    /// <summary>The Ruby shape of the value at <paramref name="k"/> (boxed scalars, Dictionary, object?[]).</summary>
    internal static object? ToObject(JsonDocument doc, int k)
    {
        ulong w = doc.Word(k);
        switch (Tape.Tag(w))
        {
            case Tape.StartObject:
                {
                    var d = new Dictionary<string, object?>(Math.Min(Tape.Mid(w), 1024));
                    for (int p = k + 1; Tape.Tag(doc.Word(p)) != Tape.EndObject; p = doc.Next(p + 1))
                        d[Encoding.UTF8.GetString(doc.Utf8String(p))] = ToObject(doc, p + 1);
                    return d;
                }
            case Tape.StartArray:
                {
                    int n = Tape.Mid(w);
                    if (n == Tape.LongMark) n = new JsonAny(doc, k).GetArrayLength();
                    var a = new object?[n];
                    int i = 0;
                    for (int p = k + 1; Tape.Tag(doc.Word(p)) != Tape.EndArray; p = doc.Next(p)) a[i++] = ToObject(doc, p);
                    return a;
                }
            case Tape.String or Tape.CleanString or Tape.ArenaString: return Encoding.UTF8.GetString(doc.Utf8String(k));
            case Tape.Int64: return (long)doc.Word(k + 1);
            case Tape.UInt64: return doc.Word(k + 1);
            case Tape.Double: return BitConverter.UInt64BitsToDouble(doc.Word(k + 1));
            case Tape.True: return true;
            case Tape.False: return false;
            default: return null;
        }
    }

    public partial System.Text.Json.JsonElement ToJsonElement() => Stj.ToJsonElement(this);

    /// <summary>A string value as is, any other value as minified JSON, Undefined as "".</summary>
    public override string ToString() => Kind == JsonKind.String ? GetString() : ToJson();

    InvalidOperationException Mismatch(string expected) => new($"the JSON value is {Kind}, not {expected}");

    public partial struct ArrayEnumerator
    {
        readonly JsonDocument? doc;
        readonly int start;
        int current;

        internal ArrayEnumerator(JsonDocument doc, int start)
        {
            this.doc = doc;
            this.start = start;
            current = 0;
        }

        public partial JsonAny Current { get => current == 0 || doc is null ? default : new JsonAny(doc, current); }

        public partial bool MoveNext()
        {
            if (doc is null) return false;
            int k = current == 0 ? start + 1 : doc.Next(current);
            if (Tape.Tag(doc.Word(k)) == Tape.EndArray) return false;
            current = k;
            return true;
        }
    }

    public partial struct ObjectEnumerator
    {
        readonly JsonDocument? doc;
        readonly int start;
        int current;

        internal ObjectEnumerator(JsonDocument doc, int start)
        {
            this.doc = doc;
            this.start = start;
            current = 0;
        }

        public partial JsonProperty Current { get => current == 0 || doc is null ? default : new JsonProperty(doc, current); }

        public partial bool MoveNext()
        {
            if (doc is null) return false;
            int k = current == 0 ? start + 1 : doc.Next(current + 1);
            if (Tape.Tag(doc.Word(k)) == Tape.EndObject) return false;
            current = k;
            return true;
        }
    }
}

public readonly partial struct JsonProperty
{
    readonly JsonDocument? doc;
    readonly int key;

    internal JsonProperty(JsonDocument doc, int key)
    {
        this.doc = doc;
        this.key = key;
    }

    public partial ReadOnlySpan<byte> Utf8Name { get => doc is null ? default : doc.Utf8String(key); }

    public partial string Name { get => doc is null ? "" : Encoding.UTF8.GetString(doc.Utf8String(key)); }

    public partial JsonAny Value { get => doc is null ? default : new JsonAny(doc, key + 1); }

    /// <summary>`name: value` (debugging).</summary>
    public override string ToString() => $"{Name}: {Value.ToJson()}";
}
