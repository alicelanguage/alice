using System.Buffers;
using System.Text.Json.Nodes;
using StjDocument = System.Text.Json.JsonDocument;
using StjElement = System.Text.Json.JsonElement;

namespace Alice.Core.Json;

/// <summary>
/// Interop with System.Text.Json (the slow path, ADR 0036): conversions re-serialize through UTF-8 bytes so both
/// sides keep their own validation.
/// </summary>
static class Stj
{
    /// <summary>A detached <see cref="StjElement"/> equal to <paramref name="value"/>.</summary>
    public static StjElement ToJsonElement(JsonAny value)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        Write(value, buf);
        using var d = StjDocument.Parse(buf.WrittenMemory);
        return d.RootElement.Clone();
    }

    /// <summary>A mutable <see cref="JsonNode"/> tree (null for JSON null) built straight from the tape.</summary>
    public static JsonNode? ToJsonNode(JsonAny value)
    {
        switch (value.Kind)
        {
            case JsonKind.Object:
                {
                    var o = new JsonObject();
                    foreach (var p in value.EnumerateObject())
                    {
                        // duplicates: the last value wins at the first position (Ruby Hash)
                        string name = p.Name;
                        if (o.ContainsKey(name)) o[name] = ToJsonNode(p.Value); else o.Add(name, ToJsonNode(p.Value));
                    }
                    return o;
                }
            case JsonKind.Array:
                {
                    var a = new JsonArray();
                    foreach (var e in value.EnumerateArray()) a.Add(ToJsonNode(e));
                    return a;
                }
            case JsonKind.String: return JsonValue.Create(value.GetString());
            case JsonKind.Int64: return JsonValue.Create(value.GetInt64());
            case JsonKind.UInt64: return JsonValue.Create(value.GetUInt64());
            case JsonKind.Float64: return JsonValue.Create(value.GetDouble());
            case JsonKind.Boolean: return JsonValue.Create(value.GetBoolean());
            default: return null;
        }
    }

    /// <summary>Parses the JSON text of a <see cref="JsonNode"/> into our document.</summary>
    public static JsonDocument FromJsonNode(JsonNode? node)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new System.Text.Json.Utf8JsonWriter(buf))
        {
            if (node is null) w.WriteNullValue(); else node.WriteTo(w);
        }
        return JsonDocument.Parse(buf.WrittenSpan);
    }

    /// <summary>Parses the JSON text of a <see cref="StjElement"/> into our document.</summary>
    public static JsonDocument FromJsonElement(StjElement element)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new System.Text.Json.Utf8JsonWriter(buf)) element.WriteTo(w);
        return JsonDocument.Parse(buf.WrittenSpan);
    }

    static void Write(JsonAny value, IBufferWriter<byte> buf)
    {
        var w = new JsonWriter(buf);
        value.WriteTo(ref w);
        w.Flush();
    }
}
