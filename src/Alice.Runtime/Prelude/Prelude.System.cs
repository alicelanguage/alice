using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Alice.Core.Json;

namespace Alice.Runtime;

/// <summary>Files, time, environment, JSON as free functions (no `File` module: it would clash with System.IO.File).</summary>
public static partial class Prelude
{
    [Ambient] public static string read_file(string path) => System.IO.File.ReadAllText(path);
    [Ambient] public static void write_file(string path, string text) => System.IO.File.WriteAllText(path, text);
    [Ambient] public static bool file_exists_q(string path) => System.IO.File.Exists(path);
    [Ambient] public static string[] read_lines(string path) => System.IO.File.ReadAllLines(path);
    [Ambient] public static void sleep(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));
    [Ambient] public static DateTime now() => DateTime.Now;
    [Ambient] public static string? env(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// AOT-safe JSON through Alice.Core.Json (ADR 0036): primitives, strings, `JSON::Any`, IDictionary, IEnumerable,
    /// JsonNode/JsonElement; other objects serialize via `str`. Ruby `JSON.generate` shape: floats keep `.0`, only
    /// `"`, `\` and control characters are escaped.
    /// </summary>
    public static string to_json<T>(T value) => JSON.generate(value);

    /// <summary>Parses with Alice.Core.Json (RFC 8259 strict) and returns a mutable JsonNode tree.</summary>
    public static JsonNode parse_json(string text)
    {
        using var doc = Alice.Core.Json.JsonDocument.Parse(text);
        return Stj.ToJsonNode(doc.Root) ?? throw new FormatException("JSON null is not a document");
    }

    /// <summary>Writes the Ruby shape of <paramref name="v"/> (to_json / JSON.generate).</summary>
    internal static void WriteJsonValue(ref JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNull(); break;
            case string s: w.WriteString(s.AsSpan()); break;
            case char c: w.WriteString(c.ToString().AsSpan()); break;
            case bool b: w.WriteBoolean(b); break;
            case int or long or short or sbyte or byte or ushort or uint: w.WriteNumber(Convert.ToInt64(v, Inv)); break;
            case ulong u: w.WriteNumber(u); break;
            case double d: w.WriteNumber(d); break;
            case float f: w.WriteNumber((double)f); break;
            case decimal m: w.WriteRawValue(Encoding.UTF8.GetBytes(m.ToString(Inv))); break;
            case JsonAny a:
                if (a.IsUndefined) w.WriteNull(); else a.WriteTo(ref w);
                break;
            case JsonNode n: w.WriteRawValue(Encoding.UTF8.GetBytes(n.ToJsonString())); break;
            case System.Text.Json.JsonElement e: w.WriteRawValue(Encoding.UTF8.GetBytes(e.GetRawText())); break;
            case System.Collections.IDictionary d:
                w.WriteStartObject();
                foreach (System.Collections.DictionaryEntry kv in d)
                {
                    w.WritePropertyName(str(kv.Key).AsSpan());
                    WriteJsonValue(ref w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable e:
                w.WriteStartArray();
                foreach (var x in e) WriteJsonValue(ref w, x);
                w.WriteEndArray();
                break;
            default: w.WriteString(str(v).AsSpan()); break;
        }
    }
}

/// <summary>
/// Ruby's `JSON` module over Alice.Core.Json: `JSON.parse(text)` returns `JSON::Any` (<see cref="JsonAny"/>),
/// `JSON.generate(obj)` / `JSON.pretty_generate(obj)` write the Ruby shape.
/// </summary>
public static class JSON
{
    /// <summary>Parses RFC 8259 JSON; the document is owned by the returned value (no dispose needed).</summary>
    public static JsonAny parse(string text) => Alice.Core.Json.JsonDocument.Parse(text).Root;

    /// <summary>Parses JSON, optionally accepting `//` and `/* */` comments.</summary>
    public static JsonAny parse(string text, bool allow_comments) =>
        Alice.Core.Json.JsonDocument.Parse(text, new JsonParseOptions(AllowComments: allow_comments)).Root;

    /// <summary>Minified JSON text of <paramref name="value"/>.</summary>
    public static string generate(object? value) => Generate(value, false);

    /// <summary>Indented JSON text of <paramref name="value"/> (two spaces).</summary>
    public static string pretty_generate(object? value) => Generate(value, true);

    static string Generate(object? value, bool indented)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(buf, indented);
        Prelude.WriteJsonValue(ref w, value);
        w.Flush();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }
}

/// <summary>Alice spellings on `JSON::Any` (Prelude sugar over the .NET-named members of <see cref="JsonAny"/>).</summary>
public static class JsonAnyRuby
{
    /// <summary>`as_i`: Int64 (exact integers only).</summary>
    public static long as_i(this JsonAny v) => v.GetInt64();

    /// <summary>`as_f`: any number as Float64.</summary>
    public static double as_f(this JsonAny v) => v.GetDouble();

    /// <summary>`as_s`: a string value.</summary>
    public static string as_s(this JsonAny v) => v.GetString();

    /// <summary>`as_bool`.</summary>
    public static bool as_bool(this JsonAny v) => v.GetBoolean();

    /// <summary>`as_a`: the elements of an array.</summary>
    public static JsonAny[] as_a(this JsonAny v)
    {
        var a = new JsonAny[v.GetArrayLength()];
        int i = 0;
        foreach (var e in v.EnumerateArray()) a[i++] = e;
        return a;
    }

    /// <summary>`as_h`: the properties of an object (later duplicates win).</summary>
    public static Dictionary<string, JsonAny> as_h(this JsonAny v)
    {
        var d = new Dictionary<string, JsonAny>(v.GetPropertyCount());
        foreach (var p in v.EnumerateObject()) d[p.Name] = p.Value;
        return d;
    }

    /// <summary>`nil?`: JSON null, a missing key or an index out of range.</summary>
    public static bool nil_q(this JsonAny v) => v.Kind is JsonKind.Null or JsonKind.Undefined;

    /// <summary>`dig(k, ...)`: string keys and integer indexes; Undefined as soon as a step is missing.</summary>
    public static JsonAny dig(this JsonAny v, params object[] path)
    {
        foreach (var k in path)
        {
            v = k switch
            {
                string s => v[s],
                int i => v[i],
                long l => l is >= int.MinValue and <= int.MaxValue ? v[(int)l] : default,
                _ => default,
            };
            if (v.IsUndefined) break;
        }
        return v;
    }

    /// <summary>`size`: elements of an array, properties of an object, bytes of a string.</summary>
    public static int size(this JsonAny v) => v.Kind switch
    {
        JsonKind.Array => v.GetArrayLength(),
        JsonKind.Object => v.GetPropertyCount(),
        JsonKind.String => v.GetString().Length,
        _ => 0,
    };

    /// <summary>`keys` of an object.</summary>
    public static string[] keys(this JsonAny v)
    {
        var k = new string[v.GetPropertyCount()];
        int i = 0;
        foreach (var p in v.EnumerateObject()) k[i++] = p.Name;
        return k;
    }

    /// <summary>`each { |e| }` over an array.</summary>
    public static void each(this JsonAny v, Action<JsonAny> block)
    {
        foreach (var e in v.EnumerateArray()) block(e);
    }

    /// <summary>`each_pair { |k, v| }` over an object.</summary>
    public static void each_pair(this JsonAny v, Action<string, JsonAny> block)
    {
        foreach (var p in v.EnumerateObject()) block(p.Name, p.Value);
    }

    /// <summary>`to_h`: the boxed Ruby shape of an object.</summary>
    public static Dictionary<string, object?> to_h(this JsonAny v) => v.ToDictionary();

    /// <summary>`to_a`: the boxed Ruby shape of an array.</summary>
    public static object?[] to_a(this JsonAny v) => v.ToArray();

    /// <summary>`to_json`: minified JSON text.</summary>
    public static string to_json(this JsonAny v) => v.ToJson();
}
