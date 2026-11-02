using System.Buffers;

namespace Alice.Core.Json;

// Placeholder implementations of the frozen JSON contract (Abstractions/Json.cs). Each implementing task deletes its
// part of this file: JsonDocument + JsonAny + JsonProperty (C2.3), JsonIterator + JsonWriter (C2.4).

public sealed partial class JsonDocument
{
    JsonDocument() { }

    public static partial JsonDocument Parse(ReadOnlySpan<byte> utf8, JsonParseOptions options) => throw new NotImplementedException("Alice.Core.Json: C2.3");
    public static partial JsonDocument Parse(string json, JsonParseOptions options) => throw new NotImplementedException("Alice.Core.Json: C2.3");
    public partial JsonAny Root { get => throw new NotImplementedException("Alice.Core.Json: C2.3"); }
    public partial void Dispose() { }
}

public readonly partial struct JsonAny
{
    public partial JsonKind Kind { get => document is null ? JsonKind.Undefined : throw new NotImplementedException("Alice.Core.Json: C2.3"); }
    public partial JsonAny this[string name] { get => throw new NotImplementedException(); }
    public partial JsonAny this[ReadOnlySpan<byte> utf8Name] { get => throw new NotImplementedException(); }
    public partial JsonAny this[int index] { get => throw new NotImplementedException(); }
    public partial bool TryGetProperty(ReadOnlySpan<byte> utf8Name, out JsonAny value) => throw new NotImplementedException();
    public partial bool TryGetProperty(ReadOnlySpan<char> name, out JsonAny value) => throw new NotImplementedException();
    public partial int GetArrayLength() => throw new NotImplementedException();
    public partial int GetPropertyCount() => throw new NotImplementedException();
    public partial long GetInt64() => throw new NotImplementedException();
    public partial ulong GetUInt64() => throw new NotImplementedException();
    public partial double GetDouble() => throw new NotImplementedException();
    public partial bool GetBoolean() => throw new NotImplementedException();
    public partial string GetString() => throw new NotImplementedException();
    public partial ReadOnlySpan<byte> GetUtf8() => throw new NotImplementedException();
    public partial bool TryGetInt64(out long value) => throw new NotImplementedException();
    public partial bool TryGetDouble(out double value) => throw new NotImplementedException();
    public partial ArrayEnumerator EnumerateArray() => throw new NotImplementedException();
    public partial ObjectEnumerator EnumerateObject() => throw new NotImplementedException();
    public partial void WriteTo(ref JsonWriter writer) => throw new NotImplementedException();
    public partial string ToJson() => throw new NotImplementedException();
    public partial Dictionary<string, object?> ToDictionary() => throw new NotImplementedException();
    public partial object?[] ToArray() => throw new NotImplementedException();
    public partial System.Text.Json.JsonElement ToJsonElement() => throw new NotImplementedException();

    public partial struct ArrayEnumerator
    {
        public partial JsonAny Current { get => throw new NotImplementedException(); }
        public partial bool MoveNext() => throw new NotImplementedException();
    }

    public partial struct ObjectEnumerator
    {
        public partial JsonProperty Current { get => throw new NotImplementedException(); }
        public partial bool MoveNext() => throw new NotImplementedException();
    }
}

public readonly partial struct JsonProperty
{
    public partial ReadOnlySpan<byte> Utf8Name { get => throw new NotImplementedException(); }
    public partial string Name { get => throw new NotImplementedException(); }
    public partial JsonAny Value { get => throw new NotImplementedException(); }
}

public ref partial struct JsonIterator
{
    public partial JsonIterator(ReadOnlySpan<byte> utf8, JsonParseOptions options) => throw new NotImplementedException("Alice.Core.Json: C2.4");
    public partial JsonKind PeekKind() => throw new NotImplementedException();
    public partial bool TryReadNull() => throw new NotImplementedException();
    public partial bool ReadBoolean() => throw new NotImplementedException();
    public partial long ReadInt64() => throw new NotImplementedException();
    public partial ulong ReadUInt64() => throw new NotImplementedException();
    public partial double ReadDouble() => throw new NotImplementedException();
    public partial string ReadString() => throw new NotImplementedException();
    public partial ReadOnlySpan<byte> ReadRawString(out bool hasEscapes) => throw new NotImplementedException();
    public partial void ReadStartObject() => throw new NotImplementedException();
    public partial bool NextProperty(out ReadOnlySpan<byte> rawName) => throw new NotImplementedException();
    public partial void ReadStartArray() => throw new NotImplementedException();
    public partial bool NextElement() => throw new NotImplementedException();
    public partial void Skip() => throw new NotImplementedException();
    public partial int Depth { get => throw new NotImplementedException(); }
    public partial long Offset { get => throw new NotImplementedException(); }
    public partial void Dispose() { }
}

public ref partial struct JsonWriter
{
    public partial JsonWriter(IBufferWriter<byte> output, bool indented) => throw new NotImplementedException("Alice.Core.Json: C2.4");
    public partial JsonWriter(Span<byte> destination, bool indented) => throw new NotImplementedException("Alice.Core.Json: C2.4");
    public partial long BytesWritten { get => throw new NotImplementedException(); }
    public partial bool Overflowed { get => throw new NotImplementedException(); }
    public partial void WriteStartObject() => throw new NotImplementedException();
    public partial void WriteEndObject() => throw new NotImplementedException();
    public partial void WriteStartArray() => throw new NotImplementedException();
    public partial void WriteEndArray() => throw new NotImplementedException();
    public partial void WritePropertyName(ReadOnlySpan<byte> utf8Name) => throw new NotImplementedException();
    public partial void WritePropertyName(ReadOnlySpan<char> name) => throw new NotImplementedException();
    public partial void WriteNull() => throw new NotImplementedException();
    public partial void WriteBoolean(bool value) => throw new NotImplementedException();
    public partial void WriteNumber(long value) => throw new NotImplementedException();
    public partial void WriteNumber(ulong value) => throw new NotImplementedException();
    public partial void WriteNumber(double value) => throw new NotImplementedException();
    public partial void WriteString(ReadOnlySpan<char> value) => throw new NotImplementedException();
    public partial void WriteString(ReadOnlySpan<byte> utf8Value) => throw new NotImplementedException();
    public partial void WriteRawValue(ReadOnlySpan<byte> utf8Json) => throw new NotImplementedException();
    public partial void Flush() => throw new NotImplementedException();
}
