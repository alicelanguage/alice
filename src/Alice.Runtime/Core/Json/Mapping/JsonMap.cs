using System.Buffers;
using System.Text;

namespace Alice.Core.Json;

/// <summary>Reads one value of <typeparamref name="T"/> at the iterator (a generated `__json_read_T`).</summary>
public delegate T JsonRead<T>(ref JsonIterator reader);

/// <summary>Writes one value of <typeparamref name="T"/> (a generated `__json_write_T`).</summary>
public delegate void JsonWrite<T>(ref JsonWriter writer, T value);

/// <summary>
/// Hand-written mapping pieces that compiler-generated readers and writers (C2.5) compose: primitives, nullable,
/// arrays/lists, string-keyed hashes and sum-type tags (`{"type":"Circle",...}`). Element readers/writers are passed
/// as delegates; static method groups are cached by the C# compiler, so steady-state reads allocate only the result
/// and writes into a pooled buffer allocate nothing.
/// </summary>
public static class JsonMap
{
    /// <summary>Reads a whole document with <paramref name="read"/>; trailing content is an error.</summary>
    public static T Parse<T>(ReadOnlySpan<byte> utf8, JsonRead<T> read, JsonParseOptions options = default)
    {
        var it = new JsonIterator(utf8, options);
        try
        {
            var v = read(ref it);
            it.EnsureEnd();
            return v;
        }
        finally
        {
            it.Dispose();
        }
    }

    /// <summary>Reads a whole document given as text.</summary>
    public static T Parse<T>(string json, JsonRead<T> read, JsonParseOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] buf = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(json.Length));
        try
        {
            int n = Encoding.UTF8.GetBytes(json, buf);
            return Parse(buf.AsSpan(0, n), read, options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Writes <paramref name="value"/> into <paramref name="output"/> and flushes.</summary>
    public static void Serialize<T>(IBufferWriter<byte> output, T value, JsonWrite<T> write, bool indented = false)
    {
        var w = new JsonWriter(output, indented);
        write(ref w, value);
        w.Flush();
    }

    /// <summary><paramref name="value"/> as JSON text.</summary>
    public static string ToJson<T>(T value, JsonWrite<T> write, bool indented = false)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        Serialize(buf, value, write, indented);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // ---- primitives ----

    /// <summary>Int64.</summary>
    public static long ReadInt64(ref JsonIterator r) => r.ReadInt64();

    /// <summary>Int32 (range-checked).</summary>
    public static int ReadInt32(ref JsonIterator r)
    {
        long off = r.Offset;
        long v = r.ReadInt64();
        return v is >= int.MinValue and <= int.MaxValue ? (int)v : throw new JsonParseException("number does not fit Int32", off);
    }

    /// <summary>UInt64.</summary>
    public static ulong ReadUInt64(ref JsonIterator r) => r.ReadUInt64();

    /// <summary>UInt32 (range-checked).</summary>
    public static uint ReadUInt32(ref JsonIterator r)
    {
        long off = r.Offset;
        ulong v = r.ReadUInt64();
        return v <= uint.MaxValue ? (uint)v : throw new JsonParseException("number does not fit UInt32", off);
    }

    /// <summary>Float64.</summary>
    public static double ReadDouble(ref JsonIterator r) => r.ReadDouble();

    /// <summary>Boolean.</summary>
    public static bool ReadBoolean(ref JsonIterator r) => r.ReadBoolean();

    /// <summary>String (null is an error; use <see cref="ReadOrNull{T}"/>).</summary>
    public static string ReadString(ref JsonIterator r) => r.ReadString();

    /// <summary>Int64.</summary>
    public static void WriteInt64(ref JsonWriter w, long v) => w.WriteNumber(v);

    /// <summary>Int32.</summary>
    public static void WriteInt32(ref JsonWriter w, int v) => w.WriteNumber(v);

    /// <summary>UInt64.</summary>
    public static void WriteUInt64(ref JsonWriter w, ulong v) => w.WriteNumber(v);

    /// <summary>UInt32.</summary>
    public static void WriteUInt32(ref JsonWriter w, uint v) => w.WriteNumber((ulong)v);

    /// <summary>Float64.</summary>
    public static void WriteDouble(ref JsonWriter w, double v) => w.WriteNumber(v);

    /// <summary>Boolean.</summary>
    public static void WriteBoolean(ref JsonWriter w, bool v) => w.WriteBoolean(v);

    /// <summary>String; null writes `null`.</summary>
    public static void WriteString(ref JsonWriter w, string? v)
    {
        if (v is null) w.WriteNull(); else w.WriteString(v.AsSpan());
    }

    // ---- nullable ----

    /// <summary>`null` or a value type.</summary>
    public static T? ReadNullable<T>(ref JsonIterator r, JsonRead<T> read) where T : struct => r.TryReadNull() ? null : read(ref r);

    /// <summary>`null` or a reference type.</summary>
    public static T? ReadOrNull<T>(ref JsonIterator r, JsonRead<T> read) where T : class => r.TryReadNull() ? null : read(ref r);

    /// <summary>`null` or a value type.</summary>
    public static void WriteNullable<T>(ref JsonWriter w, T? value, JsonWrite<T> write) where T : struct
    {
        if (value.HasValue) write(ref w, value.GetValueOrDefault()); else w.WriteNull();
    }

    /// <summary>`null` or a reference type.</summary>
    public static void WriteOrNull<T>(ref JsonWriter w, T? value, JsonWrite<T> write) where T : class
    {
        if (value is null) w.WriteNull(); else write(ref w, value);
    }

    // ---- collections ----

    /// <summary>`[...]` into a list.</summary>
    public static List<T> ReadList<T>(ref JsonIterator r, JsonRead<T> read)
    {
        var list = new List<T>();
        r.ReadStartArray();
        while (r.NextElement()) list.Add(read(ref r));
        return list;
    }

    /// <summary>`[...]` into an array.</summary>
    public static T[] ReadArray<T>(ref JsonIterator r, JsonRead<T> read)
    {
        r.ReadStartArray();
        if (!r.NextElement()) return [];
        T[] items = new T[4];
        int n = 0;
        do
        {
            if (n == items.Length) Array.Resize(ref items, n * 2);
            items[n++] = read(ref r);
        } while (r.NextElement());
        if (n != items.Length) Array.Resize(ref items, n);
        return items;
    }

    /// <summary>`{...}` into a string-keyed dictionary (later duplicates win).</summary>
    public static Dictionary<string, T> ReadHash<T>(ref JsonIterator r, JsonRead<T> read)
    {
        var map = new Dictionary<string, T>();
        r.ReadStartObject();
        while (r.NextProperty(out var name))
        {
            string key = Strings.Materialize(name);
            map[key] = read(ref r);
        }
        return map;
    }

    /// <summary>A list (null writes `null`).</summary>
    public static void WriteList<T>(ref JsonWriter w, IReadOnlyList<T>? items, JsonWrite<T> write)
    {
        if (items is null) { w.WriteNull(); return; }
        w.WriteStartArray();
        for (int i = 0; i < items.Count; i++) write(ref w, items[i]);
        w.WriteEndArray();
    }

    /// <summary>A span of items.</summary>
    public static void WriteArray<T>(ref JsonWriter w, ReadOnlySpan<T> items, JsonWrite<T> write)
    {
        w.WriteStartArray();
        foreach (var x in items) write(ref w, x);
        w.WriteEndArray();
    }

    /// <summary>A string-keyed dictionary (null writes `null`).</summary>
    public static void WriteHash<T>(ref JsonWriter w, Dictionary<string, T>? map, JsonWrite<T> write)
    {
        if (map is null) { w.WriteNull(); return; }
        w.WriteStartObject();
        foreach (var kv in map)
        {
            w.WritePropertyName(kv.Key.AsSpan());
            write(ref w, kv.Value);
        }
        w.WriteEndObject();
    }

    // ---- plain enums: by member name ----

    /// <summary>Writes the member name of <paramref name="value"/> (`"Green"`); 0 bytes allocated (names cached per type).</summary>
    public static void WriteEnum<T>(ref JsonWriter w, T value) where T : struct, Enum
    {
        var names = EnumNames<T>.Utf8;
        var values = EnumNames<T>.Values;
        for (int i = 0; i < values.Length; i++)
            if (EqualityComparer<T>.Default.Equals(values[i], value)) { w.WriteString(names[i]); return; }
        throw new ArgumentException($"{value} is not a member of {typeof(T).Name}");
    }

    /// <summary>Reads a member name (exact, case-sensitive).</summary>
    public static T ReadEnum<T>(ref JsonIterator r) where T : struct, Enum
    {
        long at = r.Offset;
        var raw = r.ReadRawString(out bool esc);
        var names = EnumNames<T>.Utf8;
        if (!esc)
            for (int i = 0; i < names.Length; i++)
                if (raw.SequenceEqual(names[i])) return EnumNames<T>.Values[i];
        throw new JsonParseException($"not a member of {typeof(T).Name}", at);
    }

    static class EnumNames<T> where T : struct, Enum
    {
        public static readonly T[] Values = Enum.GetValues<T>();
        public static readonly byte[][] Utf8 = Values.Select(v => System.Text.Encoding.UTF8.GetBytes(v.ToString())).ToArray();
    }

    /// <summary>The string of a raw property name from <see cref="JsonIterator.NextProperty"/> (escapes decoded).</summary>
    public static string Key(ReadOnlySpan<byte> rawName) => Strings.Materialize(rawName);

    /// <summary>The error for a required field absent from the object (generated readers).</summary>
    public static JsonParseException MissingField(string field, string type, long offset) => new($"missing field \"{field}\" of {type}", offset);

    // ---- sum types: {"<tag>":"Variant", ...fields} ----

    /// <summary>
    /// The raw value of the <paramref name="tagName"/> property of the object at the iterator, without consuming
    /// anything (the tag may come after other fields). Empty when absent or not a string.
    /// </summary>
    public static ReadOnlySpan<byte> PeekTag(ref JsonIterator r, ReadOnlySpan<byte> tagName)
    {
        var mark = r.Mark();
        ReadOnlySpan<byte> tag = default;
        try
        {
            r.ReadStartObject();
            while (r.NextProperty(out var name))
            {
                if (Strings.RawEquals(name, tagName) && r.PeekKind() == JsonKind.String)
                {
                    tag = r.ReadRawString(out _);
                    break;
                }
                r.Skip();
            }
        }
        finally
        {
            r.Reset(mark);
        }
        return tag;
    }

    /// <summary>The tag property of a sum-type value: `"type":"Circle"`.</summary>
    public static void WriteTag(ref JsonWriter w, ReadOnlySpan<byte> tagName, ReadOnlySpan<byte> variant)
    {
        w.WritePropertyName(tagName);
        w.WriteString(variant);
    }
}
