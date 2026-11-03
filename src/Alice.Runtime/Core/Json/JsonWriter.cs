using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace Alice.Core.Json;

/// <remarks>
/// Commas, `:` and indentation come from three flags (no nesting stack): after a value a comma is due, after `{`/`[`
/// or a property name none is. Doubles: shortest round-trip (Ryu, same text as `double.ToString("R")`) and, like Ruby, a `.0` suffix when
/// the text would read back as an integer (`1.0` stays `1.0`). Strings: only `"`, `\` and C0 controls are escaped
/// (Ruby's `JSON.generate`); non-ASCII stays UTF-8. Indented output uses two spaces and `": "`.
/// </remarks>
public ref partial struct JsonWriter
{
    readonly IBufferWriter<byte>? output;
    Span<byte> buf;
    int pos;
    long committed;
    int depth;
    readonly bool indented;
    bool needComma, afterName, overflowed;

    public partial JsonWriter(IBufferWriter<byte> output, bool indented)
    {
        ArgumentNullException.ThrowIfNull(output);
        this.output = output;
        this.indented = indented;
    }

    public partial JsonWriter(Span<byte> destination, bool indented)
    {
        buf = destination;
        this.indented = indented;
    }

    public partial long BytesWritten { get => committed + pos; }

    public partial bool Overflowed { get => overflowed; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool Reserve(int n) => (buf.Length - pos >= n && !overflowed) || Grow(n);

    [MethodImpl(MethodImplOptions.NoInlining)]
    bool Grow(int n)
    {
        if (overflowed) return false;
        if (output is null)
        {
            overflowed = true;
            return false;
        }
        if (pos > 0)
        {
            output.Advance(pos);
            committed += pos;
            pos = 0;
        }
        buf = output.GetSpan(Math.Max(n, 4096));
        return true;
    }

    /// <summary>Reserves the separator plus <paramref name="n"/> bytes and writes the separator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool Begin(int n)
    {
        if (afterName)
        {
            afterName = false;
            return Reserve(n);
        }
        if (!indented)
        {
            if (!needComma) return Reserve(n);
            if (!Reserve(n + 1)) return false;
            buf[pos++] = (byte)',';
            return true;
        }
        return BeginIndented(n);
    }

    bool BeginIndented(int n)
    {
        if (!Reserve(n + (needComma ? 1 : 0) + (depth > 0 ? 1 + 2 * depth : 0))) return false;
        if (needComma) buf[pos++] = (byte)',';
        if (depth > 0) NewLine();
        return true;
    }

    void NewLine()
    {
        buf[pos++] = (byte)'\n';
        buf.Slice(pos, 2 * depth).Fill((byte)' ');
        pos += 2 * depth;
    }

    public partial void WriteStartObject() => Open((byte)'{');

    public partial void WriteEndObject() => Close((byte)'}');

    public partial void WriteStartArray() => Open((byte)'[');

    public partial void WriteEndArray() => Close((byte)']');

    void Open(byte c)
    {
        if (Begin(1)) buf[pos++] = c;
        depth++;
        needComma = false;
    }

    void Close(byte c)
    {
        depth--;
        bool newline = indented && needComma;
        if (Reserve(newline ? 2 + 2 * depth : 1))
        {
            if (newline) NewLine();
            buf[pos++] = c;
        }
        needComma = true;
        afterName = false;
    }

    public partial void WritePropertyName(scoped ReadOnlySpan<byte> utf8Name) => WriteRawPropertyName(utf8Name);

    public partial void WritePropertyName(scoped ReadOnlySpan<char> name)
    {
        WriteString(name);
        EndName();
    }

    /// <summary>A property name given as unescaped UTF-8 (escaped as needed).</summary>
    internal void WritePropertyNameEscaped(ReadOnlySpan<byte> utf8Name)
    {
        WriteString(utf8Name);
        EndName();
    }

    /// <summary>A property name whose bytes are valid JSON string contents (copied between quotes).</summary>
    internal void WriteRawPropertyName(scoped ReadOnlySpan<byte> raw)
    {
        if (Begin(raw.Length + (indented ? 4 : 3)))
        {
            buf[pos++] = (byte)'"';
            raw.CopyTo(buf.Slice(pos));
            pos += raw.Length;
            buf[pos++] = (byte)'"';
            buf[pos++] = (byte)':';
            if (indented) buf[pos++] = (byte)' ';
        }
        needComma = false;
        afterName = true;
    }

    void EndName()
    {
        if (Reserve(indented ? 2 : 1))
        {
            buf[pos++] = (byte)':';
            if (indented) buf[pos++] = (byte)' ';
        }
        needComma = false;
        afterName = true;
    }

    public partial void WriteNull() => Literal("null"u8);

    public partial void WriteBoolean(bool value) => Literal(value ? "true"u8 : "false"u8);

    void Literal(ReadOnlySpan<byte> s)
    {
        if (Begin(s.Length))
        {
            s.CopyTo(buf.Slice(pos));
            pos += s.Length;
        }
        needComma = true;
    }

    public partial void WriteNumber(long value)
    {
        Span<byte> tmp = stackalloc byte[24];
        value.TryFormat(tmp, out int n, default, CultureInfo.InvariantCulture);
        Number(tmp.Slice(0, n));
    }

    public partial void WriteNumber(ulong value)
    {
        Span<byte> tmp = stackalloc byte[24];
        value.TryFormat(tmp, out int n, default, CultureInfo.InvariantCulture);
        Number(tmp.Slice(0, n));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Number(scoped ReadOnlySpan<byte> text)
    {
        if (Begin(text.Length))
        {
            text.CopyTo(buf.Slice(pos));
            pos += text.Length;
        }
        needComma = true;
    }

    public partial void WriteNumber(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), value, "JSON has no NaN or infinity (RFC 8259)");
        Span<byte> tmp = stackalloc byte[40];
        int n = DoubleFormat.Format(value, tmp);
        if (tmp.Slice(0, n).IndexOfAny((byte)'.', (byte)'E') < 0)
        {
            tmp[n++] = (byte)'.';
            tmp[n++] = (byte)'0';
        }
        Number(tmp.Slice(0, n));
    }

    public partial void WriteString(scoped ReadOnlySpan<char> value)
    {
        int max = 3 * value.Length;
        if (output is null && buf.Length - pos < max + 3)
        {
            // span mode near the end: exact sizes instead of the worst case
            byte[] utf8 = ArrayPool<byte>.Shared.Rent(max);
            int len = System.Text.Encoding.UTF8.GetBytes(value, utf8);
            WriteString(utf8.AsSpan(0, len));
            ArrayPool<byte>.Shared.Return(utf8);
            return;
        }
        if (Begin(max + 2))
        {
            buf[pos] = (byte)'"';
            var dst = buf.Slice(pos + 1);
            Utf8.FromUtf16(value, dst, out _, out int n);
            var written = dst.Slice(0, n);
            int k = written.IndexOfAny(Strings.NeedsEscape);
            if (k < 0)
            {
                pos += 1 + n;
                buf[pos++] = (byte)'"';
            }
            else
            {
                pos += 1 + k;
                byte[] tmp = ArrayPool<byte>.Shared.Rent(n - k);
                written.Slice(k).CopyTo(tmp);
                EscapedTail(tmp.AsSpan(0, n - k));
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }
        needComma = true;
    }

    public partial void WriteString(scoped ReadOnlySpan<byte> utf8Value)
    {
        int k = utf8Value.IndexOfAny(Strings.NeedsEscape);
        if (k < 0)
        {
            WriteRawString(utf8Value);
            return;
        }
        if (Begin(k + 1))
        {
            buf[pos++] = (byte)'"';
            utf8Value.Slice(0, k).CopyTo(buf.Slice(pos));
            pos += k;
            EscapedTail(utf8Value.Slice(k));
        }
        needComma = true;
    }

    /// <summary>Escapes <paramref name="rest"/> and closes the string.</summary>
    void EscapedTail(scoped ReadOnlySpan<byte> rest)
    {
        int need = output is null && buf.Length - pos < Strings.MaxEscaped(rest.Length) + 1 ? Strings.EscapedLength(rest) : Strings.MaxEscaped(rest.Length);
        if (!Reserve(need + 1)) return;
        pos += Strings.Escape(rest, buf.Slice(pos));
        buf[pos++] = (byte)'"';
    }

    /// <summary>A string whose bytes are valid JSON string contents (copied between quotes).</summary>
    internal void WriteRawString(scoped ReadOnlySpan<byte> raw)
    {
        if (Begin(raw.Length + 2))
        {
            buf[pos++] = (byte)'"';
            raw.CopyTo(buf.Slice(pos));
            pos += raw.Length;
            buf[pos++] = (byte)'"';
        }
        needComma = true;
    }

    /// <summary>A number token copied as is.</summary>
    internal void WriteRawNumber(ReadOnlySpan<byte> text) => WriteRawValue(text);

    public partial void WriteRawValue(scoped ReadOnlySpan<byte> utf8Json)
    {
        if (Begin(utf8Json.Length))
        {
            utf8Json.CopyTo(buf.Slice(pos));
            pos += utf8Json.Length;
        }
        needComma = true;
    }

    public partial void Flush()
    {
        if (output != null && pos > 0)
        {
            output.Advance(pos);
            committed += pos;
            pos = 0;
            buf = default;
        }
    }

    /// <summary>The bytes written so far in span mode.</summary>
    internal ReadOnlySpan<byte> WrittenSpan => output is null ? buf.Slice(0, pos) : default;
}
