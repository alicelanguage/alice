using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Alice.Runtime;

namespace Alice.Core.Text;

/// <summary>
/// Ruby string methods over UTF-8 bytes: span in, span out, 0 bytes allocated. Character sets are
/// <see cref="Utf8ByteSet"/> (ASCII specifications, Ruby semantics on UTF-8 input); positions are byte offsets.
/// The hot loops classify 64 bytes per step with AVX-512BW (32 with AVX2) and fall back to scalar code.
/// <see cref="Utf8String"/> is the immutable string type built on these.
/// </summary>
public static class Utf8Text
{
    /// <summary>Ruby `count(set)`: number of characters of <paramref name="s"/> in <paramref name="set"/>.</summary>
    [Pure]
    public static int Count(ReadOnlySpan<byte> s, Utf8ByteSet set)
    {
        int n = 0, i = 0;
        ref byte p = ref MemoryMarshal.GetReference(s);
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
            for (; i + 64 <= s.Length; i += 64)
                n += BitOperations.PopCount(set.Classify(Vector512.LoadUnsafe(ref p, (nuint)i)).ExtractMostSignificantBits());
        else if (Avx2.IsSupported)
            for (; i + 32 <= s.Length; i += 32)
                n += BitOperations.PopCount(set.Classify(Vector256.LoadUnsafe(ref p, (nuint)i)).ExtractMostSignificantBits());
        for (; i < s.Length; i++) if (set.Contains(s[i])) n++;
        return n;
    }

    /// <summary>Ruby `count(spec)` with a specification such as `"a-z"` or `"^ "`.</summary>
    [Pure]
    public static int Count(ReadOnlySpan<byte> s, string spec) => Count(s, Utf8ByteSet.Parse(spec));

    /// <summary>Number of UTF-8 characters (non-continuation bytes) in <paramref name="s"/>.</summary>
    [Pure]
    public static int CharCount(ReadOnlySpan<byte> s)
    {
        int cont = 0, i = 0;
        ref byte p = ref MemoryMarshal.GetReference(s);
        if (Vector512.IsHardwareAccelerated)
            for (; i + 64 <= s.Length; i += 64)
                cont += BitOperations.PopCount(Vector512.LessThan(Vector512.LoadUnsafe(ref p, (nuint)i).AsSByte(), Vector512.Create((sbyte)-64)).ExtractMostSignificantBits());
        else if (Vector256.IsHardwareAccelerated)
            for (; i + 32 <= s.Length; i += 32)
                cont += BitOperations.PopCount(Vector256.LessThan(Vector256.LoadUnsafe(ref p, (nuint)i).AsSByte(), Vector256.Create((sbyte)-64)).ExtractMostSignificantBits());
        for (; i < s.Length; i++) if ((sbyte)s[i] < -64) cont++;
        return s.Length - cont;
    }

    /// <summary>Ruby `delete(set)` into <paramref name="dst"/> (at least <c>s.Length</c> bytes); returns the bytes
    /// written. Each 64-byte block is classified with SIMD: a block without members is stored whole, otherwise it is
    /// compacted without branches.</summary>
    public static int Delete(ReadOnlySpan<byte> s, Utf8ByteSet set, Span<byte> dst)
    {
        if (dst.Length < s.Length) throw new ArgumentException("destination is shorter than the source", nameof(dst));
        set = set.WithContinuations();
        int i = 0, w = 0;
        ref byte p = ref MemoryMarshal.GetReference(s);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        if (Avx2.IsSupported)
            for (; i + 64 <= s.Length; i += 64)
            {
                ulong drop = MemberMask64(set, ref Unsafe.Add(ref p, i));
                if (drop == 0) { Copy64(ref Unsafe.Add(ref p, i), ref Unsafe.Add(ref d, w)); w += 64; }
                else w += Compact64(ref Unsafe.Add(ref p, i), drop, ref Unsafe.Add(ref d, w));
            }
        for (; i < s.Length; i++) if (!set.Contains(s[i])) dst[w++] = s[i];
        return w;
    }

    /// <summary>Ruby `squeeze(set)`: runs of the same character that is in <paramref name="set"/> collapse to one;
    /// with <paramref name="set"/> null every character is squeezed. Returns the bytes written into
    /// <paramref name="dst"/> (at least <c>s.Length</c> bytes). Blocks are compared with their one-byte-shifted
    /// selves in SIMD and compacted without branches; blocks where multi-byte characters can repeat (no set or a
    /// negated set, non-ASCII bytes present) go through the character-level loop.</summary>
    public static int Squeeze(ReadOnlySpan<byte> s, Utf8ByteSet? set, Span<byte> dst)
    {
        if (dst.Length < s.Length) throw new ArgumentException("destination is shorter than the source", nameof(dst));
        bool all = set is null;
        var bs = set.GetValueOrDefault();
        // byte-exact: a positive ASCII set never matches any byte of a multi-byte character
        bool bytewise = !all && !bs.Negated;
        int i = 0, w = 0, prevLen = 0;
        uint prev = 0;
        ref byte p = ref MemoryMarshal.GetReference(s);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        bool simd = Avx2.IsSupported;
        while (i < s.Length)
        {
            if (simd && prevLen >= 1 && i >= 4 && i + 64 <= s.Length)
            {
                ref byte b = ref Unsafe.Add(ref p, i);
                ulong nonAscii = bytewise ? 0 : NonAsciiMask64(ref b);
                int m = 64;
                if (nonAscii != 0)
                {
                    // stop before a character that crosses the block end; give up on a possible repeat of a
                    // multi-byte character (a lead byte equal to the byte 2, 3 or 4 before it: rare)
                    m = BlockCharEnd(ref b);
                    ulong leads = LeadMask64(ref b) & (m == 64 ? ulong.MaxValue : (1UL << m) - 1);
                    if (m == 0 || (leads & RepeatCandidates64(ref b)) != 0) goto scalar;
                }
                ulong drop = EqualPrevMask64(ref b) & ~nonAscii;
                if (!all) drop &= MemberMask64(bs, ref b);
                if (m == 64)
                {
                    if (drop == 0) Copy64(ref b, ref Unsafe.Add(ref d, w));
                    w += drop == 0 ? 64 : Compact64(ref b, drop, ref Unsafe.Add(ref d, w));
                }
                else w += CompactN(ref b, drop, m, ref Unsafe.Add(ref d, w));
                i += m;
                // the last character of the block becomes the previous one
                int q = i - 1;
                while (q > i - 4 && (s[q] & 0xC0) == 0x80) q--;
                prevLen = Math.Min(CharLen(s, q), i - q);
                prev = 0;
                for (int k = 0; k < prevLen; k++) prev |= (uint)s[q + k] << (8 * k);
                continue;
            }
        scalar:
            byte lead = s[i];
            int len = bytewise || lead < 0x80 ? 1 : CharLen(s, i);
            uint ch = lead;
            for (int k = 1; k < len; k++) ch |= (uint)s[i + k] << (8 * k);
            bool member = all || bs.Contains(lead);
            if (member && len == prevLen && ch == prev) { i += len; continue; }
            for (int k = 0; k < len; k++) dst[w + k] = s[i + k];
            prev = ch; prevLen = len; i += len; w += len;
        }
        return w;
    }

    // bit k set: byte k of the 64-byte block is in the set
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MemberMask64(in Utf8ByteSet set, ref byte b)
    {
        if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported) return set.Classify(Vector512.LoadUnsafe(ref b)).ExtractMostSignificantBits();
        return set.Classify(Vector256.LoadUnsafe(ref b)).ExtractMostSignificantBits() | (ulong)set.Classify(Vector256.LoadUnsafe(ref b, 32)).ExtractMostSignificantBits() << 32;
    }

    // bit k set: byte k equals byte k - 1 (byte -1 is the one before the block)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong EqualPrevMask64(ref byte b)
    {
        ref byte before = ref Unsafe.Subtract(ref b, 1);
        if (Vector512.IsHardwareAccelerated) return Vector512.Equals(Vector512.LoadUnsafe(ref b), Vector512.LoadUnsafe(ref before)).ExtractMostSignificantBits();
        return Vector256.Equals(Vector256.LoadUnsafe(ref b), Vector256.LoadUnsafe(ref before)).ExtractMostSignificantBits()
            | (ulong)Vector256.Equals(Vector256.LoadUnsafe(ref b, 32), Vector256.LoadUnsafe(ref before, 32)).ExtractMostSignificantBits() << 32;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong NonAsciiMask64(ref byte b)
    {
        if (Vector512.IsHardwareAccelerated) return Vector512.LoadUnsafe(ref b).ExtractMostSignificantBits();
        return Vector256.LoadUnsafe(ref b).ExtractMostSignificantBits() | (ulong)Vector256.LoadUnsafe(ref b, 32).ExtractMostSignificantBits() << 32;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Copy64(ref byte src, ref byte dst)
    {
        if (Vector512.IsHardwareAccelerated) { Vector512.LoadUnsafe(ref src).StoreUnsafe(ref dst); return; }
        Vector256.LoadUnsafe(ref src).StoreUnsafe(ref dst);
        Vector256.LoadUnsafe(ref src, 32).StoreUnsafe(ref dst, 32);
    }

    // bit k set: byte k is a UTF-8 lead byte of a multi-byte character (0xC0..0xFF)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong LeadMask64(ref byte b)
    {
        if (Vector512.IsHardwareAccelerated) return Vector512.GreaterThanOrEqual(Vector512.LoadUnsafe(ref b), Vector512.Create((byte)0xC0)).ExtractMostSignificantBits();
        var c = Vector256.Create((byte)0xC0);
        return Vector256.GreaterThanOrEqual(Vector256.LoadUnsafe(ref b), c).ExtractMostSignificantBits()
            | (ulong)Vector256.GreaterThanOrEqual(Vector256.LoadUnsafe(ref b, 32), c).ExtractMostSignificantBits() << 32;
    }

    // bit k set: byte k equals byte k-2, k-3 or k-4 (reads up to 4 bytes before the block)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong RepeatCandidates64(ref byte b)
    {
        ulong r = 0;
        for (int back = 2; back <= 4; back++)
        {
            ref byte before = ref Unsafe.Subtract(ref b, back);
            if (Vector512.IsHardwareAccelerated) r |= Vector512.Equals(Vector512.LoadUnsafe(ref b), Vector512.LoadUnsafe(ref before)).ExtractMostSignificantBits();
            else r |= Vector256.Equals(Vector256.LoadUnsafe(ref b), Vector256.LoadUnsafe(ref before)).ExtractMostSignificantBits()
                | (ulong)Vector256.Equals(Vector256.LoadUnsafe(ref b, 32), Vector256.LoadUnsafe(ref before, 32)).ExtractMostSignificantBits() << 32;
        }
        return r;
    }

    // offset just past the last character that ends inside the 64-byte block (64 when the block ends on a boundary)
    static int BlockCharEnd(ref byte b)
    {
        int q = 63;
        while (q > 60 && (Unsafe.Add(ref b, q) & 0xC0) == 0x80) q--;
        byte lead = Unsafe.Add(ref b, q);
        int len = lead < 0xC0 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
        return q + len <= 64 ? 64 : q;
    }

    // Compact64 over the first n bytes only
    static int CompactN(ref byte src, ulong drop, int n, ref byte dst)
    {
        int w = 0;
        for (int k = 0; k < n; k++)
        {
            Unsafe.Add(ref dst, w) = Unsafe.Add(ref src, k);
            w += (int)(~(drop >> k) & 1);
        }
        return w;
    }

    // keeps the bytes whose drop bit is clear, in order; writes at most 64 bytes at dst; returns the kept count
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Compact64(ref byte src, ulong drop, ref byte dst)
    {
        int w = 0;
        for (int k = 0; k < 64; k++)
        {
            Unsafe.Add(ref dst, w) = Unsafe.Add(ref src, k);
            w += (int)(~(drop >> k) & 1);
        }
        return w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int CharLen(ReadOnlySpan<byte> s, int i)
    {
        byte b = s[i];
        int n = b < 0xC0 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
        return Math.Min(n, s.Length - i);
    }

    /// <summary>Ruby `tr(from, to)` into <paramref name="dst"/> (at least <c>s.Length</c> bytes); returns the bytes
    /// written. With AVX-512 VBMI 64 bytes are translated per step by one 128-entry permute.</summary>
    public static int Tr(ReadOnlySpan<byte> s, in Utf8Translation t, Span<byte> dst)
    {
        if (dst.Length < s.Length) throw new ArgumentException("destination is shorter than the source", nameof(dst));
        if (t.DeletesAll) return Delete(s, t.From, dst);
        int i = 0, w = 0;
        ref byte p = ref MemoryMarshal.GetReference(s);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        var map = t.Map;
        if (Avx512Vbmi.IsSupported && s.Length >= 64)
        {
            var lo = Vector512.Create(map[..64]);
            var hi = Vector512.Create(map[64..]);
            for (; i + 64 <= s.Length; i += 64, w += 64)
            {
                var v = Vector512.LoadUnsafe(ref p, (nuint)i);
                var ascii = Vector512.GreaterThanOrEqual(v.AsSByte(), Vector512<sbyte>.Zero).AsByte();
                // a negated set needs character-level handling of non-ASCII input: leave the block to the scalar loop
                if (t.Negated && ascii != Vector512<byte>.AllBitsSet) break;
                Vector512.ConditionalSelect(ascii, Avx512Vbmi.PermuteVar64x8x2(lo, v, hi), v).StoreUnsafe(ref d, (nuint)w);
            }
        }
        while (i < s.Length)
        {
            byte b = s[i];
            if (b < 0x80) { dst[w++] = map[b]; i++; continue; }
            if (!t.Negated) { dst[w++] = b; i++; continue; }
            // a negated set maps every non-ASCII character (all of its bytes) to the replacement byte
            dst[w++] = t.Replacement;
            i += CharLen(s, i);
        }
        return w;
    }

    /// <summary>Ruby `upcase` for ASCII letters into <paramref name="dst"/>; non-ASCII bytes are copied unchanged.</summary>
    public static int UpcaseAscii(ReadOnlySpan<byte> s, Span<byte> dst) => CaseAscii(s, dst, upper: true);

    /// <summary>Ruby `downcase` for ASCII letters into <paramref name="dst"/>; non-ASCII bytes are copied unchanged.</summary>
    public static int DowncaseAscii(ReadOnlySpan<byte> s, Span<byte> dst) => CaseAscii(s, dst, upper: false);

    static int CaseAscii(ReadOnlySpan<byte> s, Span<byte> dst, bool upper)
    {
        if (dst.Length < s.Length) throw new ArgumentException("destination is shorter than the source", nameof(dst));
        int i = 0;
        while (i < s.Length)
        {
            int run = s[i..].IndexOfAnyExceptInRange((byte)0, (byte)0x7F);
            int end = run < 0 ? s.Length : i + run;
            if (end > i)
            {
                if (upper) Ascii.ToUpper(s[i..end], dst[i..end], out _);
                else Ascii.ToLower(s[i..end], dst[i..end], out _);
            }
            if (end == s.Length) break;
            dst[end] = s[end];
            i = end + 1;
        }
        return s.Length;
    }

    /// <summary>Ruby `strip`: <paramref name="s"/> without leading and trailing whitespace (and trailing NUL).</summary>
    [Pure]
    public static ReadOnlySpan<byte> Strip(ReadOnlySpan<byte> s) => LStrip(RStrip(s));

    /// <summary>Ruby `lstrip`.</summary>
    [Pure]
    public static ReadOnlySpan<byte> LStrip(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length && IsSpace(s[i])) i++;
        return s[i..];
    }

    /// <summary>Ruby `rstrip` (also removes trailing NUL).</summary>
    [Pure]
    public static ReadOnlySpan<byte> RStrip(ReadOnlySpan<byte> s)
    {
        int e = s.Length;
        while (e > 0 && (IsSpace(s[e - 1]) || s[e - 1] == 0)) e--;
        return s[..e];
    }

    static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

    /// <summary>Ruby `each_line`: a struct enumerator over the lines of <paramref name="s"/> (each line keeps its
    /// `\n` unless <paramref name="chomp"/>, which also drops a `\r` before it). 0 bytes per line.</summary>
    public static Utf8LineEnumerator EnumerateLines(ReadOnlySpan<byte> s, bool chomp = false) => new(s, chomp);

    /// <summary>Ruby `split(sep)` on one byte: a struct enumerator over the fields (empty fields included; Ruby's
    /// removal of trailing empty fields is left to the caller).</summary>
    public static Utf8SplitEnumerator Split(ReadOnlySpan<byte> s, byte separator) => new(s, separator);

    /// <summary>Ruby `split` without arguments: words separated by runs of ASCII whitespace.</summary>
    public static Utf8WordEnumerator SplitWhitespace(ReadOnlySpan<byte> s) => new(s);
}

/// <summary>A compiled `tr(from, to)` translation of ASCII specifications: a 128-entry byte map.</summary>
public readonly struct Utf8Translation
{
    readonly byte[] map;

    /// <summary>The source set.</summary>
    public Utf8ByteSet From { get; }

    /// <summary>`to` was empty: `tr` deletes the members of <see cref="From"/>.</summary>
    public bool DeletesAll { get; }

    /// <summary>`from` started with `^`.</summary>
    public bool Negated => From.Negated;

    /// <summary>For a negated set: the byte every non-member character becomes.</summary>
    public byte Replacement { get; }

    internal ReadOnlySpan<byte> Map => map;

    /// <summary>Compiles Ruby `tr` specifications: ranges, `^` negation, `\` escapes; a shorter `to` repeats its last
    /// byte.</summary>
    public Utf8Translation(string from, string to)
    {
        From = Utf8ByteSet.Parse(from);
        map = new byte[128];
        for (int b = 0; b < 128; b++) map[b] = (byte)b;
        Span<byte> f = stackalloc byte[256], tt = stackalloc byte[256];
        int nf = Expand(from, f, out bool neg), nt = Expand(to, tt, out _);
        if (nt == 0) { DeletesAll = true; return; }
        if (neg)
        {
            Replacement = tt[nt - 1];
            for (int b = 0; b < 128; b++) if (From.Contains((byte)b)) map[b] = Replacement;
            return;
        }
        for (int k = nf - 1; k >= 0; k--) map[f[k]] = tt[Math.Min(k, nt - 1)];   // the first mapping of a byte wins
    }

    static int Expand(string spec, Span<byte> into, out bool neg)
    {
        int n = 0, i = 0;
        neg = spec.Length > 1 && spec[0] == '^';
        if (neg) i = 1;
        for (; i < spec.Length && n < into.Length; i++)
        {
            char c = spec[i];
            if (c > 0x7F) throw new ArgumentException("tr specifications must be ASCII");
            if (c == '\\' && i + 1 < spec.Length) { into[n++] = (byte)spec[++i]; continue; }
            if (i + 2 < spec.Length && spec[i + 1] == '-')
            {
                char hi = spec[i + 2];
                if (hi > 0x7F) throw new ArgumentException("tr specifications must be ASCII");
                if (hi < c) throw new ArgumentException($"invalid range \"{c}-{hi}\" in string transliteration");
                for (int v = c; v <= hi && n < into.Length; v++) into[n++] = (byte)v;
                i += 2;
                continue;
            }
            into[n++] = (byte)c;
        }
        return n;
    }
}

/// <summary>Struct enumerator over lines (see <see cref="Utf8Text.EnumerateLines"/>).</summary>
public ref struct Utf8LineEnumerator
{
    ReadOnlySpan<byte> rest;
    readonly bool chomp;

    internal Utf8LineEnumerator(ReadOnlySpan<byte> s, bool chomp) { rest = s; this.chomp = chomp; Current = default; }

    /// <summary>The current line.</summary>
    public ReadOnlySpan<byte> Current { get; private set; }

    /// <summary>Returns this enumerator (foreach support).</summary>
    public readonly Utf8LineEnumerator GetEnumerator() => this;

    /// <summary>Advances to the next line.</summary>
    public bool MoveNext()
    {
        if (rest.IsEmpty) return false;
        int nl = rest.IndexOf((byte)'\n');
        if (nl < 0) { Current = rest; rest = default; return true; }
        var line = rest[..(nl + 1)];
        rest = rest[(nl + 1)..];
        if (chomp) { line = line[..^1]; if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1]; }
        Current = line;
        return true;
    }
}

/// <summary>Struct enumerator over fields separated by one byte (see <see cref="Utf8Text.Split"/>).</summary>
public ref struct Utf8SplitEnumerator
{
    ReadOnlySpan<byte> rest;
    readonly byte sep;
    bool done;

    internal Utf8SplitEnumerator(ReadOnlySpan<byte> s, byte sep) { rest = s; this.sep = sep; done = false; Current = default; }

    /// <summary>The current field.</summary>
    public ReadOnlySpan<byte> Current { get; private set; }

    /// <summary>Returns this enumerator (foreach support).</summary>
    public readonly Utf8SplitEnumerator GetEnumerator() => this;

    /// <summary>Advances to the next field.</summary>
    public bool MoveNext()
    {
        if (done) return false;
        int k = rest.IndexOf(sep);
        if (k < 0) { Current = rest; done = true; return true; }
        Current = rest[..k];
        rest = rest[(k + 1)..];
        return true;
    }
}

/// <summary>Struct enumerator over whitespace-separated words (see <see cref="Utf8Text.SplitWhitespace"/>).</summary>
public ref struct Utf8WordEnumerator
{
    ReadOnlySpan<byte> rest;
    static ReadOnlySpan<byte> Ws => " \t\n\v\f\r"u8;

    internal Utf8WordEnumerator(ReadOnlySpan<byte> s) { rest = s; Current = default; }

    /// <summary>The current word.</summary>
    public ReadOnlySpan<byte> Current { get; private set; }

    /// <summary>Returns this enumerator (foreach support).</summary>
    public readonly Utf8WordEnumerator GetEnumerator() => this;

    /// <summary>Advances to the next word.</summary>
    public bool MoveNext()
    {
        int st = rest.IndexOfAnyExcept(Ws);
        if (st < 0) { rest = default; return false; }
        rest = rest[st..];
        int e = rest.IndexOfAny(Ws);
        if (e < 0) e = rest.Length;
        Current = rest[..e];
        rest = rest[e..];
        return true;
    }
}
