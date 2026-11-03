using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Alice.Core.Toml;

/// <summary>Kind of a <see cref="TomlAny"/>.</summary>
public enum TomlKind : byte
{
    /// <summary>No value (a missing key: `default(TomlAny)`).</summary>
    None,
    /// <summary>A string.</summary>
    String,
    /// <summary>A 64-bit integer.</summary>
    Integer,
    /// <summary>A float.</summary>
    Float,
    /// <summary>A Boolean.</summary>
    Boolean,
    /// <summary>A date-time with offset (<see cref="DateTimeOffset"/>).</summary>
    OffsetDateTime,
    /// <summary>A date-time without offset (<see cref="DateTime"/>, kind Unspecified).</summary>
    LocalDateTime,
    /// <summary>A date (<see cref="DateOnly"/>).</summary>
    LocalDate,
    /// <summary>A time of day (<see cref="TimeOnly"/>).</summary>
    LocalTime,
    /// <summary>An array (<see cref="TomlArray"/>).</summary>
    Array,
    /// <summary>A table (<see cref="TomlTable"/>).</summary>
    Table,
}

/// <summary>
/// One TOML value: a small tagged struct (`TOML::Any`). Scalars live inline; strings, arrays, tables and date-times
/// are references. Indexing a missing key or index gives <see cref="TomlKind.None"/> (Ruby `nil`), so lookups chain:
/// <c>doc["package"]["name"].AsString()</c>.
/// </summary>
public readonly struct TomlAny : IEquatable<TomlAny>
{
    readonly long bits;
    readonly object? obj;

    /// <summary>The kind.</summary>
    public TomlKind Kind { get; }

    TomlAny(TomlKind k, long bits, object? obj) { Kind = k; this.bits = bits; this.obj = obj; }

    /// <summary>No value.</summary>
    public static TomlAny None => default;

    /// <summary>True unless <see cref="TomlKind.None"/>.</summary>
    public bool Exists => Kind != TomlKind.None;

#pragma warning disable CS1591 // constructors and conversions are self-describing
    public static TomlAny From(string value) => new(TomlKind.String, 0, value ?? throw new ArgumentNullException(nameof(value)));
    public static TomlAny From(long value) => new(TomlKind.Integer, value, null);
    public static TomlAny From(double value) => new(TomlKind.Float, BitConverter.DoubleToInt64Bits(value), null);
    public static TomlAny From(bool value) => new(TomlKind.Boolean, value ? 1 : 0, null);
    public static TomlAny From(DateTimeOffset value) => new(TomlKind.OffsetDateTime, 0, value);
    public static TomlAny From(DateTime value) => new(TomlKind.LocalDateTime, 0, DateTime.SpecifyKind(value, DateTimeKind.Unspecified));
    public static TomlAny From(DateOnly value) => new(TomlKind.LocalDate, 0, value);
    public static TomlAny From(TimeOnly value) => new(TomlKind.LocalTime, 0, value);
    public static TomlAny From(TomlArray value) => new(TomlKind.Array, 0, value);
    public static TomlAny From(TomlTable value) => new(TomlKind.Table, 0, value);
    public static implicit operator TomlAny(string value) => From(value);
    public static implicit operator TomlAny(long value) => From(value);
    public static implicit operator TomlAny(int value) => From(value);
    public static implicit operator TomlAny(double value) => From(value);
    public static implicit operator TomlAny(bool value) => From(value);
    public static implicit operator TomlAny(TomlArray value) => From(value);
    public static implicit operator TomlAny(TomlTable value) => From(value);
#pragma warning restore CS1591

    /// <summary>`t[key]`: the value, or None when this is not a table or has no such key.</summary>
    public TomlAny this[string key] => obj is TomlTable t && t.TryGetValue(key, out var v) ? v : default;

    /// <summary>`a[i]`: the element, or None when this is not an array or out of range.</summary>
    public TomlAny this[int index] => obj is TomlArray a && (uint)index < (uint)a.Count ? a[index] : default;

    /// <summary>Ruby `dig(*keys)`.</summary>
    public TomlAny Dig(params ReadOnlySpan<string> path)
    {
        var v = this;
        foreach (var k in path) v = v[k];
        return v;
    }

    /// <summary>The string, or null when not a string.</summary>
    public string? AsString() => Kind == TomlKind.String ? (string)obj! : null;

    /// <summary>The table, or null.</summary>
    public TomlTable? AsTable() => obj as TomlTable;

    /// <summary>The array, or null.</summary>
    public TomlArray? AsArray() => obj as TomlArray;

    /// <summary>The string; throws <see cref="InvalidCastException"/> when not a string.</summary>
    public string GetString() => Kind == TomlKind.String ? (string)obj! : throw Wrong("string");

    /// <summary>The integer; throws when not an integer.</summary>
    public long GetInt64() => Kind == TomlKind.Integer ? bits : throw Wrong("integer");

    /// <summary>The float (an integer converts); throws otherwise.</summary>
    public double GetDouble() => Kind == TomlKind.Float ? BitConverter.Int64BitsToDouble(bits) : Kind == TomlKind.Integer ? bits : throw Wrong("float");

    /// <summary>The Boolean; throws when not a Boolean.</summary>
    public bool GetBoolean() => Kind == TomlKind.Boolean ? bits != 0 : throw Wrong("boolean");

    /// <summary>An offset date-time; throws otherwise.</summary>
    public DateTimeOffset GetDateTimeOffset() => Kind == TomlKind.OffsetDateTime ? (DateTimeOffset)obj! : throw Wrong("offset date-time");

    /// <summary>A local date-time; throws otherwise.</summary>
    public DateTime GetDateTime() => Kind == TomlKind.LocalDateTime ? (DateTime)obj! : throw Wrong("local date-time");

    /// <summary>A local date; throws otherwise.</summary>
    public DateOnly GetDate() => Kind == TomlKind.LocalDate ? (DateOnly)obj! : throw Wrong("local date");

    /// <summary>A local time; throws otherwise.</summary>
    public TimeOnly GetTime() => Kind == TomlKind.LocalTime ? (TimeOnly)obj! : throw Wrong("local time");

    /// <summary>The value as a plain .NET object graph: string, long, double, bool, DateTimeOffset, DateTime,
    /// DateOnly, TimeOnly, <c>List&lt;object&gt;</c>, <c>Dictionary&lt;string, object&gt;</c> (insertion order).</summary>
    public object? ToObject() => Kind switch
    {
        TomlKind.None => null,
        TomlKind.Integer => bits,
        TomlKind.Float => BitConverter.Int64BitsToDouble(bits),
        TomlKind.Boolean => bits != 0,
        TomlKind.Array => ((TomlArray)obj!).Select(x => x.ToObject()!).ToList(),
        TomlKind.Table => ((TomlTable)obj!).ToDictionary(),
        _ => obj,
    };

    InvalidCastException Wrong(string want) => new($"TOML value is {Kind}, not {want}");

    /// <inheritdoc/>
    public bool Equals(TomlAny other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            TomlKind.None => true,
            TomlKind.Integer or TomlKind.Boolean => bits == other.bits,
            TomlKind.Float => GetDouble().Equals(other.GetDouble()),
            TomlKind.Array => ((TomlArray)obj!).SequenceEqual((TomlArray)other.obj!),
            TomlKind.Table => ((TomlTable)obj!).ContentEquals((TomlTable)other.obj!),
            _ => Equals(obj, other.obj),
        };
    }

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? o) => o is TomlAny a && Equals(a);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, bits, obj is string or ValueType ? obj : null);

    /// <summary>The value as TOML text (an inline form for arrays and tables).</summary>
    public override string ToString() => Kind switch
    {
        TomlKind.None => "",
        TomlKind.String => (string)obj!,
        _ => TomlWriter.Inline(this),
    };

    internal long Bits => bits;
    internal object? Obj => obj;
}

/// <summary>A TOML array (insertion order).</summary>
public sealed class TomlArray : IReadOnlyList<TomlAny>
{
    readonly List<TomlAny> items = [];

    /// <summary>Created by `[[header]]` (an array of tables), as opposed to an inline array.</summary>
    public bool IsTableArray { get; internal set; }

    /// <summary>An empty array.</summary>
    public TomlArray() { }

    /// <summary>An array of <paramref name="values"/>.</summary>
    public TomlArray(IEnumerable<TomlAny> values) => items.AddRange(values);

    /// <summary>Number of elements.</summary>
    public int Count => items.Count;

    /// <summary>Element <paramref name="index"/>.</summary>
    public TomlAny this[int index] => items[index];

    /// <summary>Appends a value.</summary>
    public void Add(TomlAny value) => items.Add(value);

    /// <summary>Struct enumerator.</summary>
    public List<TomlAny>.Enumerator GetEnumerator() => items.GetEnumerator();
    IEnumerator<TomlAny> IEnumerable<TomlAny>.GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
}

/// <summary>A TOML table: keys in insertion order.</summary>
public sealed class TomlTable : IReadOnlyDictionary<string, TomlAny>
{
    readonly Dictionary<string, TomlAny> map = new(StringComparer.Ordinal);
    readonly List<string> order = [];

    // parser bookkeeping (TOML 1.0 table-definition rules)
    internal bool Defined;      // [header] seen for this table
    internal bool Dotted;       // created by a dotted key
    internal bool Inline;       // an inline table: immutable once closed

    /// <summary>Number of keys.</summary>
    public int Count => order.Count;

    /// <summary>The value of <paramref name="key"/>; throws <see cref="KeyNotFoundException"/> when absent.</summary>
    public TomlAny this[string key] => map[key];

    /// <summary>Keys in insertion order.</summary>
    public IEnumerable<string> Keys => order;

    /// <summary>Values in insertion order.</summary>
    public IEnumerable<TomlAny> Values => order.Select(k => map[k]);

    /// <inheritdoc/>
    public bool ContainsKey(string key) => map.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out TomlAny value) => map.TryGetValue(key, out value);

    /// <summary>Sets <paramref name="key"/> (adds it at the end when new).</summary>
    public void Set(string key, TomlAny value)
    {
        if (!map.ContainsKey(key)) order.Add(key);
        map[key] = value;
    }

    internal bool TryAdd(string key, TomlAny value)
    {
        if (!map.TryAdd(key, value)) return false;
        order.Add(key);
        return true;
    }

    /// <summary>Removes <paramref name="key"/>.</summary>
    public bool Remove(string key) => map.Remove(key) && order.Remove(key);

    /// <summary>The table as <c>Dictionary&lt;string, object&gt;</c> (see <see cref="TomlAny.ToObject"/>).</summary>
    public Dictionary<string, object> ToDictionary()
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var k in order) d[k] = map[k].ToObject()!;
        return d;
    }

    internal bool ContentEquals(TomlTable o) => Count == o.Count && order.All(k => o.TryGetValue(k, out var v) && v.Equals(map[k]));

    /// <summary>Pairs in insertion order.</summary>
    public IEnumerator<KeyValuePair<string, TomlAny>> GetEnumerator()
    {
        foreach (var k in order) yield return new(k, map[k]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The table as a TOML document.</summary>
    public override string ToString() => TomlWriter.Write(this);
}

/// <summary>A TOML syntax or semantics error with its 1-based line and column.</summary>
public sealed class TomlParseException(string message, int line, int column) : FormatException($"TOML line {line}, column {column}: {message}")
{
    /// <summary>1-based line.</summary>
    public int Line { get; } = line;

    /// <summary>1-based column.</summary>
    public int Column { get; } = column;

    /// <summary>The message without the position.</summary>
    public string Reason { get; } = message;
}

internal static class TomlText
{
    internal static string Rfc3339(TomlAny v) => v.Kind switch
    {
        TomlKind.OffsetDateTime => FormatOffset((DateTimeOffset)v.Obj!),
        TomlKind.LocalDateTime => ((DateTime)v.Obj!).ToString(Frac(((DateTime)v.Obj!).Ticks) ? "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        TomlKind.LocalDate => ((DateOnly)v.Obj!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => ((TimeOnly)v.Obj!).ToString(Frac(((TimeOnly)v.Obj!).Ticks) ? "HH:mm:ss.FFFFFFF" : "HH:mm:ss", CultureInfo.InvariantCulture),
    };

    static bool Frac(long ticks) => ticks % TimeSpan.TicksPerSecond != 0;

    static string FormatOffset(DateTimeOffset d)
    {
        string main = d.ToString(Frac(d.Ticks) ? "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (d.Offset == TimeSpan.Zero) return main + "Z";
        var o = d.Offset;
        return main + (o < TimeSpan.Zero ? "-" : "+") + o.ToString("hh\\:mm", CultureInfo.InvariantCulture);
    }
}
