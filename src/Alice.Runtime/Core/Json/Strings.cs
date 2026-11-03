using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Alice.Core.Json;

/// <summary>
/// String contents: zero-copy spans when there is no escape, unescaping on demand, and the writer's escaping.
/// Escape scans are vectorized through <see cref="SearchValues{T}"/> (`IndexOfAny`).
/// </summary>
static class Strings
{
    /// <summary>Bytes the writer must escape: `"`, `\` and the C0 controls.</summary>
    public static readonly SearchValues<byte> NeedsEscape = SearchValues.Create(
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
         (byte)'"', (byte)'\\']);

    /// <summary>True when a validated raw string contains an escape.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasEscapes(ReadOnlySpan<byte> raw) => raw.IndexOf((byte)'\\') >= 0;

    /// <summary>
    /// Unescapes <paramref name="raw"/> (the bytes between the quotes) into <paramref name="dest"/>, which needs
    /// <c>raw.Length</c> bytes (unescaping never grows). Lone surrogates become U+FFFD. Returns the bytes written, or
    /// -1 on an invalid escape.
    /// </summary>
    public static int Unescape(ReadOnlySpan<byte> raw, Span<byte> dest)
    {
        int r = 0, w = 0;
        while (true)
        {
            int k = raw.Slice(r).IndexOf((byte)'\\');
            if (k < 0)
            {
                raw.Slice(r).CopyTo(dest.Slice(w));
                return w + raw.Length - r;
            }
            raw.Slice(r, k).CopyTo(dest.Slice(w));
            w += k;
            r += k + 1;
            if (r >= raw.Length) return -1;
            byte e = raw[r++];
            switch (e)
            {
                case (byte)'"': dest[w++] = (byte)'"'; break;
                case (byte)'\\': dest[w++] = (byte)'\\'; break;
                case (byte)'/': dest[w++] = (byte)'/'; break;
                case (byte)'b': dest[w++] = 8; break;
                case (byte)'f': dest[w++] = 12; break;
                case (byte)'n': dest[w++] = 10; break;
                case (byte)'r': dest[w++] = 13; break;
                case (byte)'t': dest[w++] = 9; break;
                case (byte)'u':
                    {
                        int cp = Hex4(raw, r);
                        if (cp < 0) return -1;
                        r += 4;
                        if (cp is >= 0xD800 and <= 0xDBFF)
                        {
                            int lo = r + 6 <= raw.Length && raw[r] == '\\' && raw[r + 1] == 'u' ? Hex4(raw, r + 2) : -1;
                            if (lo is >= 0xDC00 and <= 0xDFFF)
                            {
                                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                r += 6;
                            }
                            else cp = 0xFFFD;
                        }
                        else if (cp is >= 0xDC00 and <= 0xDFFF) cp = 0xFFFD;
                        w += EncodeUtf8(cp, dest.Slice(w));
                        break;
                    }
                default: return -1;
            }
        }
    }

    /// <summary>The value of 4 hex digits at <paramref name="at"/>, or -1.</summary>
    public static int Hex4(ReadOnlySpan<byte> s, int at)
    {
        if (at + 4 > s.Length) return -1;
        int a = Chars.Hex(s[at]), b = Chars.Hex(s[at + 1]), c = Chars.Hex(s[at + 2]), d = Chars.Hex(s[at + 3]);
        if ((a | b | c | d) < 0) return -1;
        return (a << 12) | (b << 8) | (c << 4) | d;
    }

    static int EncodeUtf8(int cp, Span<byte> d)
    {
        if (cp < 0x80) { d[0] = (byte)cp; return 1; }
        if (cp < 0x800) { d[0] = (byte)(0xC0 | (cp >> 6)); d[1] = (byte)(0x80 | (cp & 0x3F)); return 2; }
        if (cp < 0x10000)
        {
            d[0] = (byte)(0xE0 | (cp >> 12)); d[1] = (byte)(0x80 | ((cp >> 6) & 0x3F)); d[2] = (byte)(0x80 | (cp & 0x3F));
            return 3;
        }
        d[0] = (byte)(0xF0 | (cp >> 18)); d[1] = (byte)(0x80 | ((cp >> 12) & 0x3F));
        d[2] = (byte)(0x80 | ((cp >> 6) & 0x3F)); d[3] = (byte)(0x80 | (cp & 0x3F));
        return 4;
    }

    /// <summary>The raw string (escapes possible) as a .NET string.</summary>
    public static string Materialize(ReadOnlySpan<byte> raw)
    {
        if (!HasEscapes(raw)) return Encoding.UTF8.GetString(raw);
        byte[]? rented = null;
        Span<byte> tmp = raw.Length <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(raw.Length));
        try
        {
            int n = Unescape(raw, tmp);
            if (n < 0) throw new JsonParseException("invalid escape", 0);
            return Encoding.UTF8.GetString(tmp.Slice(0, n));
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Compares a raw (possibly escaped) string with an unescaped UTF-8 name.</summary>
    public static bool RawEquals(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> name)
    {
        // Unescaping only shrinks: equal lengths can only match byte for byte, a shorter raw never matches.
        if (raw.Length <= name.Length) return raw.SequenceEqual(name);
        if (!HasEscapes(raw)) return false;
        byte[]? rented = null;
        Span<byte> tmp = raw.Length <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(raw.Length));
        try
        {
            int n = Unescape(raw, tmp);
            return n >= 0 && tmp.Slice(0, n).SequenceEqual(name);
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Compares a raw (possibly escaped) string with a UTF-16 name.</summary>
    public static bool RawEquals(ReadOnlySpan<byte> raw, ReadOnlySpan<char> name)
    {
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        byte[]? rented = null;
        Span<byte> tmp = max <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            int n = Encoding.UTF8.GetBytes(name, tmp);
            return RawEquals(raw, tmp.Slice(0, n));
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Exact escaped size of <paramref name="src"/>.</summary>
    public static int EscapedLength(ReadOnlySpan<byte> src)
    {
        int n = src.Length;
        while (true)
        {
            int k = src.IndexOfAny(NeedsEscape);
            if (k < 0) return n;
            byte b = src[k];
            n += b is (byte)'"' or (byte)'\\' or 8 or 9 or 10 or 12 or 13 ? 1 : 5;
            src = src.Slice(k + 1);
        }
    }

    /// <summary>Worst-case escaped size of <paramref name="n"/> UTF-8 bytes (`\u00XX` per byte).</summary>
    public static int MaxEscaped(int n) => n * 6;

    /// <summary>
    /// Writes <paramref name="src"/> escaped (no quotes) into <paramref name="dest"/>, which needs
    /// <see cref="MaxEscaped"/> bytes in the worst case; returns the bytes written. Only `"`, `\` and C0 controls
    /// are escaped (Ruby's `JSON.generate`; non-ASCII stays UTF-8).
    /// </summary>
    public static int Escape(ReadOnlySpan<byte> src, Span<byte> dest)
    {
        int w = 0;
        while (true)
        {
            int k = src.IndexOfAny(NeedsEscape);
            if (k < 0)
            {
                src.CopyTo(dest.Slice(w));
                return w + src.Length;
            }
            src.Slice(0, k).CopyTo(dest.Slice(w));
            w += k;
            byte b = src[k];
            dest[w++] = (byte)'\\';
            switch (b)
            {
                case (byte)'"': dest[w++] = (byte)'"'; break;
                case (byte)'\\': dest[w++] = (byte)'\\'; break;
                case 10: dest[w++] = (byte)'n'; break;
                case 13: dest[w++] = (byte)'r'; break;
                case 9: dest[w++] = (byte)'t'; break;
                case 8: dest[w++] = (byte)'b'; break;
                case 12: dest[w++] = (byte)'f'; break;
                default:
                    dest[w++] = (byte)'u'; dest[w++] = (byte)'0'; dest[w++] = (byte)'0';
                    dest[w++] = (byte)"0123456789abcdef"[b >> 4]; dest[w++] = (byte)"0123456789abcdef"[b & 15];
                    break;
            }
            src = src.Slice(k + 1);
        }
    }
}
