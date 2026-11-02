using System.Buffers;

namespace Alice.Core.Json;

// Frozen contract (ADR 0036). Declarations only: every non-trivial member is `partial`, implemented for now by
// Abstractions/Stubs/Json.Stub.cs (throws NotImplementedException). The implementing tasks (C2.3 JsonDocument and
// JsonAny, C2.4 JsonIterator and JsonWriter) delete their part of the stub and add the real part under Core/Json/,
// so the public surface below changes only through an ADR amendment.

/// <summary>Kind of a JSON value. Integers parse as <see cref="Int64"/>, then <see cref="UInt64"/>, then
/// <see cref="Float64"/> on overflow (Ruby's order); any number with `.`, `e` or `E` is <see cref="Float64"/>.</summary>
public enum JsonKind : byte
{
    /// <summary>No value: `default(JsonAny)`, a missing key or an index out of range (Ruby `nil` from `h["x"]`).</summary>
    Undefined = 0,
    /// <summary>`null`.</summary>
    Null,
    /// <summary>`true` or `false`.</summary>
    Boolean,
    /// <summary>An integer that fits Int64.</summary>
    Int64,
    /// <summary>An integer above Int64.MaxValue that fits UInt64.</summary>
    UInt64,
    /// <summary>Any other number.</summary>
    Float64,
    /// <summary>A string.</summary>
    String,
    /// <summary>An array.</summary>
    Array,
    /// <summary>An object.</summary>
    Object,
}

/// <summary>Parse options. Default: RFC 8259 strict, depth limit 1024.</summary>
/// <param name="AllowComments">Accept `//` and `/* */` comments.</param>
/// <param name="MaxDepth">Nesting limit; 0 means 1024.</param>
public readonly record struct JsonParseOptions(bool AllowComments = false, int MaxDepth = 0);

/// <summary>Invalid JSON or a type mismatch in typed reading, with the byte offset where it was detected.</summary>
public sealed class JsonParseException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public JsonParseException(string message, long offset) : base($"{message} at byte {offset}") => Offset = offset;

    /// <summary>Byte offset in the UTF-8 input.</summary>
    public long Offset { get; }
}

/// <summary>
/// A parsed document (stage 1 structural index + stage 2 tape) that owns a copy of its input and the tape.
/// `JSON.parse` returns its <see cref="Root"/>. Dispose returns the pooled buffers; <see cref="JsonAny"/> views
/// must not be used afterwards.
/// </summary>
public sealed partial class JsonDocument : IDisposable
{
    /// <summary>Parses UTF-8 JSON (the input is copied).</summary>
    public static partial JsonDocument Parse(ReadOnlySpan<byte> utf8, JsonParseOptions options = default);

    /// <summary>Parses JSON text (transcoded to UTF-8 once).</summary>
    public static partial JsonDocument Parse(string json, JsonParseOptions options = default);

    /// <summary>The root value.</summary>
    public partial JsonAny Root { get; }

    /// <summary>Returns the pooled buffers.</summary>
    public partial void Dispose();
}

/// <summary>
/// `JSON::Any`: a value inside a <see cref="JsonDocument"/> (document + tape index, no copy). Shaped like
/// `System.Text.Json.JsonElement` for C#; Alice spellings (`as_i`, `as_s`, `nil?`, `dig`, `to_h`, `to_a`) are
/// Prelude sugar over these members. Missing keys and out-of-range indexes give <see cref="JsonKind.Undefined"/>
/// instead of throwing (Ruby `nil`).
/// </summary>
public readonly partial struct JsonAny
{
    readonly JsonDocument? document;
    readonly int index;

    /// <summary>Used by <see cref="JsonDocument"/>: the value at <paramref name="tapeIndex"/>.</summary>
    internal JsonAny(JsonDocument document, int tapeIndex)
    {
        this.document = document;
        index = tapeIndex;
    }

    /// <summary>The owning document; null for <see cref="Undefined"/>.</summary>
    internal JsonDocument? Document => document;

    /// <summary>Position in the document's tape.</summary>
    internal int TapeIndex => index;

    /// <summary>The absent value (`default`).</summary>
    public static JsonAny Undefined => default;

    /// <summary>Kind of this value.</summary>
    public partial JsonKind Kind { get; }

    /// <summary>True for `default`, missing keys and out-of-range indexes.</summary>
    public bool IsUndefined => Kind == JsonKind.Undefined;

    /// <summary>True for JSON `null`.</summary>
    public bool IsNull => Kind == JsonKind.Null;

    /// <summary>Property by name (UTF-16); Undefined when absent or not an object.</summary>
    public partial JsonAny this[string name] { get; }

    /// <summary>Property by UTF-8 name (`"id"u8`): the form generated code uses.</summary>
    public partial JsonAny this[ReadOnlySpan<byte> utf8Name] { get; }

    /// <summary>Array element; Undefined when out of range or not an array.</summary>
    public partial JsonAny this[int index] { get; }

    /// <summary>Property lookup by UTF-8 name without allocation.</summary>
    public partial bool TryGetProperty(ReadOnlySpan<byte> utf8Name, out JsonAny value);

    /// <summary>Property lookup by UTF-16 name.</summary>
    public partial bool TryGetProperty(ReadOnlySpan<char> name, out JsonAny value);

    /// <summary>Number of elements of an array.</summary>
    public partial int GetArrayLength();

    /// <summary>Number of properties of an object.</summary>
    public partial int GetPropertyCount();

    /// <summary>The value as Int64 (Int64 kind, or UInt64/Float64 that fit exactly); throws otherwise.</summary>
    public partial long GetInt64();

    /// <summary>The value as UInt64; throws when negative or not an integer.</summary>
    public partial ulong GetUInt64();

    /// <summary>The value as Float64 (any number kind).</summary>
    public partial double GetDouble();

    /// <summary>The value of a Boolean.</summary>
    public partial bool GetBoolean();

    /// <summary>A string value, unescaped and materialized.</summary>
    public partial string GetString();

    /// <summary>A string value as UTF-8 without copying when it has no escapes (otherwise unescaped once into the
    /// document's arena). Valid until the document is disposed.</summary>
    public partial ReadOnlySpan<byte> GetUtf8();

    /// <summary>Int64 when the value is an integer that fits.</summary>
    public partial bool TryGetInt64(out long value);

    /// <summary>Float64 when the value is a number.</summary>
    public partial bool TryGetDouble(out double value);

    /// <summary>Elements of an array (struct enumerator, no allocation).</summary>
    public partial ArrayEnumerator EnumerateArray();

    /// <summary>Properties of an object in document order (struct enumerator, no allocation).</summary>
    public partial ObjectEnumerator EnumerateObject();

    /// <summary>Writes this value (re-serialized, minified).</summary>
    public partial void WriteTo(ref JsonWriter writer);

    /// <summary>This value as minified JSON text.</summary>
    public partial string ToJson();

    /// <summary>The Ruby shape of an object (Alice `to_h`): nested values become Dictionary / object?[] / Int64 /
    /// UInt64 / Double / String / Boolean / null.</summary>
    public partial Dictionary<string, object?> ToDictionary();

    /// <summary>The Ruby shape of an array (Alice `to_a`).</summary>
    public partial object?[] ToArray();

    /// <summary>Interop with System.Text.Json (slow path: parses the value's bytes with `JsonDocument.Parse`).</summary>
    public partial System.Text.Json.JsonElement ToJsonElement();

    /// <summary>Struct enumerator over array elements.</summary>
    public partial struct ArrayEnumerator
    {
        /// <summary>The current element.</summary>
        public partial JsonAny Current { get; }

        /// <summary>Advances to the next element.</summary>
        public partial bool MoveNext();

        /// <summary>For `foreach`.</summary>
        public readonly ArrayEnumerator GetEnumerator() => this;
    }

    /// <summary>Struct enumerator over object properties.</summary>
    public partial struct ObjectEnumerator
    {
        /// <summary>The current property.</summary>
        public partial JsonProperty Current { get; }

        /// <summary>Advances to the next property.</summary>
        public partial bool MoveNext();

        /// <summary>For `foreach`.</summary>
        public readonly ObjectEnumerator GetEnumerator() => this;
    }
}

/// <summary>One object property: name and value views into the document.</summary>
public readonly partial struct JsonProperty
{
    /// <summary>The raw UTF-8 name (unescaped when it had escapes).</summary>
    public partial ReadOnlySpan<byte> Utf8Name { get; }

    /// <summary>The name, materialized.</summary>
    public partial string Name { get; }

    /// <summary>The value.</summary>
    public partial JsonAny Value { get; }

    /// <summary>Compares the name with a UTF-8 literal without allocation.</summary>
    public bool NameEquals(ReadOnlySpan<byte> utf8) => Utf8Name.SequenceEqual(utf8);
}

/// <summary>
/// On-demand reader (simdjson On-Demand style): walks the structural index of its input without building a tape.
/// Generated `T.from_json` readers use it: `ReadStartObject`, then `NextProperty` + `SequenceEqual` against
/// `"name"u8`, `Skip` for unknown fields. Forward-only; a value is consumed by exactly one Read*/Skip call.
/// </summary>
public ref partial struct JsonIterator
{
    /// <summary>Indexes <paramref name="utf8"/> (stage 1) and positions before the root value. The input must
    /// outlive the iterator. Dispose returns the pooled index.</summary>
    public partial JsonIterator(ReadOnlySpan<byte> utf8, JsonParseOptions options = default);

    /// <summary>Kind of the next value without consuming it (number kinds are resolved when read).</summary>
    public partial JsonKind PeekKind();

    /// <summary>Consumes `null` and returns true, or returns false without consuming.</summary>
    public partial bool TryReadNull();

    /// <summary>Reads `true`/`false`.</summary>
    public partial bool ReadBoolean();

    /// <summary>Reads an integer that fits Int64.</summary>
    public partial long ReadInt64();

    /// <summary>Reads an integer that fits UInt64.</summary>
    public partial ulong ReadUInt64();

    /// <summary>Reads any number as Float64 (shortest round-trip exact).</summary>
    public partial double ReadDouble();

    /// <summary>Reads a string, unescaped and materialized.</summary>
    public partial string ReadString();

    /// <summary>Reads a string as its raw UTF-8 bytes between the quotes (escapes not decoded; check
    /// <paramref name="hasEscapes"/>). No copy.</summary>
    public partial ReadOnlySpan<byte> ReadRawString(out bool hasEscapes);

    /// <summary>Consumes `{`; throws when the next value is not an object.</summary>
    public partial void ReadStartObject();

    /// <summary>Moves to the next property: returns its raw UTF-8 name (escapes not decoded) and positions on its
    /// value; returns an empty span and false after consuming `}`.</summary>
    public partial bool NextProperty(out ReadOnlySpan<byte> rawName);

    /// <summary>Consumes `[`; throws when the next value is not an array.</summary>
    public partial void ReadStartArray();

    /// <summary>Positions on the next element; false after consuming `]`.</summary>
    public partial bool NextElement();

    /// <summary>Skips the next value (an index jump for containers).</summary>
    public partial void Skip();

    /// <summary>Nesting depth of the current position.</summary>
    public partial int Depth { get; }

    /// <summary>Byte offset of the next token.</summary>
    public partial long Offset { get; }

    /// <summary>Returns the pooled structural index.</summary>
    public partial void Dispose();
}

/// <summary>
/// UTF-8 JSON writer over an <see cref="IBufferWriter{T}"/> or a caller <see cref="Span{T}"/>: commas and nesting
/// tracked internally, numbers through `IUtf8SpanFormattable` (shortest round-trip doubles), SIMD escape scan.
/// 0 bytes allocated when the target is pooled. In span mode a write that does not fit sets
/// <see cref="Overflowed"/> and later writes are ignored.
/// </summary>
public ref partial struct JsonWriter
{
    /// <summary>Writes into <paramref name="output"/>; <see cref="Flush"/> commits.</summary>
    public partial JsonWriter(IBufferWriter<byte> output, bool indented = false);

    /// <summary>Writes into <paramref name="destination"/>.</summary>
    public partial JsonWriter(Span<byte> destination, bool indented = false);

    /// <summary>Bytes written so far (committed and pending).</summary>
    public partial long BytesWritten { get; }

    /// <summary>Span mode: true once a write did not fit.</summary>
    public partial bool Overflowed { get; }

    /// <summary>`{`.</summary>
    public partial void WriteStartObject();

    /// <summary>`}`.</summary>
    public partial void WriteEndObject();

    /// <summary>`[`.</summary>
    public partial void WriteStartArray();

    /// <summary>`]`.</summary>
    public partial void WriteEndArray();

    /// <summary>A property name given as UTF-8 that needs no escaping (generated code: `"id"u8`).</summary>
    public partial void WritePropertyName(ReadOnlySpan<byte> utf8Name);

    /// <summary>A property name (escaped as needed).</summary>
    public partial void WritePropertyName(ReadOnlySpan<char> name);

    /// <summary>`null`.</summary>
    public partial void WriteNull();

    /// <summary>`true` / `false`.</summary>
    public partial void WriteBoolean(bool value);

    /// <summary>An integer.</summary>
    public partial void WriteNumber(long value);

    /// <summary>An unsigned integer.</summary>
    public partial void WriteNumber(ulong value);

    /// <summary>A double, shortest round-trip; NaN and infinities are an error (RFC 8259).</summary>
    public partial void WriteNumber(double value);

    /// <summary>A string (escaped as needed).</summary>
    public partial void WriteString(ReadOnlySpan<char> value);

    /// <summary>A UTF-8 string (escaped as needed).</summary>
    public partial void WriteString(ReadOnlySpan<byte> utf8Value);

    /// <summary>Pre-serialized JSON, copied as is.</summary>
    public partial void WriteRawValue(ReadOnlySpan<byte> utf8Json);

    /// <summary>Commits pending bytes to the buffer writer.</summary>
    public partial void Flush();
}
