using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;
using Alice.Runtime;

namespace Alice.Core.Text;

/// <summary>
/// An immutable UTF-8 string backed by a <c>byte[]</c> (a struct: copying it copies the reference). Ruby methods run
/// over the bytes with the SIMD kernels of <see cref="Utf8Text"/>; methods that return a string allocate exactly the
/// result array; enumerators (<see cref="EachLine"/>, <see cref="Split"/>) are struct enumerators of spans, 0 bytes per
/// item. Positions are byte offsets. <c>default</c> is the empty string. Construction validates UTF-8.
/// </summary>
public readonly struct Utf8String : IEquatable<Utf8String>, IComparable<Utf8String>, ISpanFormattable, IUtf8SpanFormattable
{
    readonly byte[]? bytes;

    Utf8String(byte[] b) => bytes = b;

    internal byte[]? Bytes => bytes;

    /// <summary>The empty string.</summary>
    public static Utf8String Empty => default;

    /// <summary>Encodes a .NET string.</summary>
    public static Utf8String From(string s) => s.Length == 0 ? default : new(Encoding.UTF8.GetBytes(s));

    /// <summary>Copies UTF-8 bytes; throws <see cref="ArgumentException"/> on invalid UTF-8.</summary>
    public static Utf8String FromUtf8(ReadOnlySpan<byte> utf8)
    {
        if (!Utf8.IsValid(utf8)) throw new ArgumentException("invalid UTF-8", nameof(utf8));
        return utf8.IsEmpty ? default : new(utf8.ToArray());
    }

    /// <summary>Takes ownership of <paramref name="utf8"/> (no copy; the caller must not mutate it afterwards).</summary>
    public static Utf8String Wrap(byte[] utf8)
    {
        if (!Utf8.IsValid(utf8)) throw new ArgumentException("invalid UTF-8", nameof(utf8));
        return utf8.Length == 0 ? default : new(utf8);
    }

    /// <summary>Implicit conversion from a .NET string.</summary>
    public static implicit operator Utf8String(string s) => From(s);

    /// <summary>The bytes.</summary>
    public ReadOnlySpan<byte> AsSpan() => bytes;

    /// <summary>The bytes.</summary>
    public static implicit operator ReadOnlySpan<byte>(Utf8String s) => s.bytes;

    /// <summary>Length in bytes (Ruby `bytesize`).</summary>
    public int ByteSize => bytes?.Length ?? 0;

    /// <summary>Length in characters (Ruby `length`/`size`), counted with SIMD.</summary>
    public int Length => Utf8Text.CharCount(bytes);

    /// <summary>Ruby `empty?`.</summary>
    public bool IsEmpty => bytes is null || bytes.Length == 0;

    /// <summary>The byte at <paramref name="index"/>.</summary>
    public byte this[int index] => (bytes ?? [])[index];

    /// <summary>Ruby `count(spec)`.</summary>
    [Pure] public int Count(string spec) => Utf8Text.Count(bytes, Utf8ByteSet.Parse(spec));

    /// <summary>Ruby `count(set)` with a precompiled set.</summary>
    [Pure] public int Count(Utf8ByteSet set) => Utf8Text.Count(bytes, set);

    /// <summary>Ruby `delete(spec)`.</summary>
    public Utf8String Delete(string spec) => Map(bytes, Utf8ByteSet.Parse(spec), static (s, set, d) => Utf8Text.Delete(s, set, d));

    /// <summary>Ruby `squeeze` (every character).</summary>
    public Utf8String Squeeze() => Map<Utf8ByteSet?>(bytes, null, static (s, set, d) => Utf8Text.Squeeze(s, set, d));

    /// <summary>Ruby `squeeze(spec)`.</summary>
    public Utf8String Squeeze(string spec) => Map<Utf8ByteSet?>(bytes, Utf8ByteSet.Parse(spec), static (s, set, d) => Utf8Text.Squeeze(s, set, d));

    /// <summary>Ruby `tr(from, to)`.</summary>
    public Utf8String Tr(string from, string to) => Tr(new Utf8Translation(from, to));

    /// <summary>Ruby `tr` with a precompiled translation.</summary>
    public Utf8String Tr(Utf8Translation t) => Map(bytes, t, static (s, t, d) => Utf8Text.Tr(s, t, d));

    /// <summary>Ruby `upcase` (ASCII letters; other characters are kept).</summary>
    public Utf8String Upcase() => Map(bytes, 0, static (s, _, d) => Utf8Text.UpcaseAscii(s, d));

    /// <summary>Ruby `downcase` (ASCII letters; other characters are kept).</summary>
    public Utf8String Downcase() => Map(bytes, 0, static (s, _, d) => Utf8Text.DowncaseAscii(s, d));

    /// <summary>Ruby `strip`.</summary>
    public Utf8String Strip() => Slice(Utf8Text.Strip(bytes));

    /// <summary>Ruby `index(needle)`: byte offset of the first occurrence at or after <paramref name="start"/>, or -1.</summary>
    [Pure]
    public int Index(ReadOnlySpan<byte> needle, int start = 0)
    {
        var s = AsSpan();
        if ((uint)start > (uint)s.Length) return -1;
        int k = s[start..].IndexOf(needle);
        return k < 0 ? -1 : start + k;
    }

    /// <summary>Ruby `index(str)`.</summary>
    [Pure] public int Index(Utf8String needle, int start = 0) => Index(needle.AsSpan(), start);

    /// <summary>Ruby `rindex(needle)`: byte offset of the last occurrence, or -1.</summary>
    [Pure] public int RIndex(ReadOnlySpan<byte> needle) => AsSpan().LastIndexOf(needle);

    /// <summary>Ruby `include?`.</summary>
    [Pure] public bool Include(ReadOnlySpan<byte> needle) => AsSpan().IndexOf(needle) >= 0;

    /// <summary>Ruby `start_with?`.</summary>
    [Pure] public bool StartsWith(ReadOnlySpan<byte> prefix) => AsSpan().StartsWith(prefix);

    /// <summary>Ruby `end_with?`.</summary>
    [Pure] public bool EndsWith(ReadOnlySpan<byte> suffix) => AsSpan().EndsWith(suffix);

    /// <summary>Ruby `each_line` as a struct enumerator of spans.</summary>
    public Utf8LineEnumerator EachLine(bool chomp = false) => Utf8Text.EnumerateLines(bytes, chomp);

    /// <summary>Ruby `split(sep)` on one ASCII byte as a struct enumerator of spans.</summary>
    public Utf8SplitEnumerator Split(byte separator) => Utf8Text.Split(bytes, separator);

    /// <summary>Ruby `split` on whitespace as a struct enumerator of spans.</summary>
    public Utf8WordEnumerator Split() => Utf8Text.SplitWhitespace(bytes);

    /// <summary>Byte slice as a new string (the slice must fall on character boundaries).</summary>
    public Utf8String Substring(int start, int length) => FromUtf8(AsSpan().Slice(start, length));

    Utf8String Slice(ReadOnlySpan<byte> part) => part.Length == ByteSize ? this : FromUtf8(part);

    /// <summary>Concatenation.</summary>
    public static Utf8String operator +(Utf8String a, Utf8String b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var r = GC.AllocateUninitializedArray<byte>(a.ByteSize + b.ByteSize);
        a.AsSpan().CopyTo(r);
        b.AsSpan().CopyTo(r.AsSpan(a.ByteSize));
        return new(r);
    }

    delegate int Kernel<TArg>(ReadOnlySpan<byte> s, TArg arg, Span<byte> dst);

    static Utf8String Map<TArg>(byte[]? src, TArg arg, Kernel<TArg> k)
    {
        if (src is null || src.Length == 0) return default;
        byte[]? rented = null;
        Span<byte> tmp = src.Length <= 512 ? stackalloc byte[512] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(src.Length));
        int n = k(src, arg, tmp);
        var r = n == 0 ? default : new Utf8String(tmp[..n].ToArray());
        if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        return r;
    }

    /// <summary>Decodes to a .NET string.</summary>
    public override string ToString() => bytes is null ? "" : Encoding.UTF8.GetString(bytes);

    /// <summary>Formats as UTF-16 (no allocation).</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => Encoding.UTF8.TryGetChars(AsSpan(), destination, out charsWritten);

    /// <summary>Copies the bytes (no allocation).</summary>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        bool ok = AsSpan().TryCopyTo(utf8Destination);
        bytesWritten = ok ? ByteSize : 0;
        return ok;
    }

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Byte-wise equality.</summary>
    public bool Equals(Utf8String other) => AsSpan().SequenceEqual(other.AsSpan());

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Utf8String u && Equals(u);

    /// <summary>Hash of the bytes.</summary>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(AsSpan());
        return h.ToHashCode();
    }

    /// <summary>Ordinal (byte-wise) comparison, Ruby `&lt;=&gt;`.</summary>
    public int CompareTo(Utf8String other) => AsSpan().SequenceCompareTo(other.AsSpan());

    /// <summary>Byte-wise equality.</summary>
    public static bool operator ==(Utf8String a, Utf8String b) => a.Equals(b);

    /// <summary>Byte-wise inequality.</summary>
    public static bool operator !=(Utf8String a, Utf8String b) => !a.Equals(b);
}
