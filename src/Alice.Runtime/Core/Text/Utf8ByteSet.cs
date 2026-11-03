using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Alice.Core.Text;

/// <summary>
/// A set of bytes parsed from a Ruby character-set specification (`"a-z"`, `"^aeiou"`, `"\\-x"`), as used by
/// `count`, `delete`, `squeeze` and `tr`. The specification must be ASCII: then a UTF-8 multi-byte character in the
/// input never matches a positive set and always matches a negated one, so byte-level classification gives Ruby's
/// character-level answer. Classification is vectorized with the two-nibble shuffle lookup (AVX-512BW, AVX2) and
/// falls back to a 256-bit bitmap.
/// </summary>
public readonly struct Utf8ByteSet
{
    // bitmap of members: bit b of word b >> 6
    readonly ulong w0, w1, w2, w3;
    // nibble tables: member(b) == ((LoA[b & 15] & HiA[b >> 4]) | (LoB[b & 15] & HiB[b >> 4])) != 0
    readonly Vector128<byte> loA, loB;

    /// <summary>True when the specification started with `^` (the set is the complement of the listed bytes).</summary>
    public bool Negated { get; }

    /// <summary>Number of member bytes (0..256).</summary>
    public int Size => BitOperations.PopCount(w0) + BitOperations.PopCount(w1) + BitOperations.PopCount(w2) + BitOperations.PopCount(w3);

    Utf8ByteSet(ulong a, ulong b, ulong c, ulong d, bool negated)
    {
        w0 = a; w1 = b; w2 = c; w3 = d; Negated = negated;
        Span<byte> la = stackalloc byte[16], lb = stackalloc byte[16];
        for (int v = 0; v < 256; v++)
            if (Has(v))
            {
                if (v < 128) la[v & 15] |= (byte)(1 << (v >> 4));
                else lb[v & 15] |= (byte)(1 << ((v >> 4) - 8));
            }
        loA = Vector128.Create((ReadOnlySpan<byte>)la);
        loB = Vector128.Create((ReadOnlySpan<byte>)lb);
    }

    bool Has(int v) => ((v >> 6) switch { 0 => w0, 1 => w1, 2 => w2, _ => w3 } >> (v & 63) & 1) != 0;

    /// <summary>True when <paramref name="b"/> is a member.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(byte b) => Has(b);

    /// <summary>The set of exactly the given bytes.</summary>
    public static Utf8ByteSet Of(ReadOnlySpan<byte> bytes)
    {
        ulong a = 0, b = 0, c = 0, d = 0;
        foreach (byte x in bytes) Set(ref a, ref b, ref c, ref d, x);
        return new Utf8ByteSet(a, b, c, d, false);
    }

    /// <summary>Parses a Ruby character-set specification given as a string (must be ASCII).</summary>
    public static Utf8ByteSet Parse(string spec)
    {
        Span<byte> tmp = spec.Length <= 256 ? stackalloc byte[spec.Length] : new byte[spec.Length];
        for (int i = 0; i < spec.Length; i++)
        {
            if (spec[i] > 0x7F) throw new ArgumentException("character sets must be ASCII", nameof(spec));
            tmp[i] = (byte)spec[i];
        }
        return Parse(tmp);
    }

    /// <summary>Parses a Ruby character-set specification: a leading `^` negates (when more follows), `a-z` is a
    /// range, `\` escapes the next byte. ASCII only; a negated set excludes UTF-8 continuation bytes (0x80-0xBF)
    /// so that <see cref="Utf8Text.Count"/> counts characters.</summary>
    public static Utf8ByteSet Parse(ReadOnlySpan<byte> spec)
    {
        bool neg = spec.Length > 1 && spec[0] == (byte)'^';
        if (neg) spec = spec[1..];
        ulong a = 0, b = 0, c = 0, d = 0;
        for (int i = 0; i < spec.Length; i++)
        {
            byte x = spec[i];
            if (x > 0x7F) throw new ArgumentException("character sets must be ASCII", nameof(spec));
            if (x == (byte)'\\' && i + 1 < spec.Length) x = spec[++i];
            else if (i + 2 < spec.Length && spec[i + 1] == (byte)'-')
            {
                byte hi = spec[i + 2];
                if (hi > 0x7F) throw new ArgumentException("character sets must be ASCII", nameof(spec));
                if (hi < x) throw new ArgumentException($"invalid range \"{(char)x}-{(char)hi}\" in string transliteration", nameof(spec));
                for (int v = x; v <= hi; v++) Set(ref a, ref b, ref c, ref d, (byte)v);
                i += 2;
                continue;
            }
            Set(ref a, ref b, ref c, ref d, x);
        }
        // 0x80..0xBF (word 2) are continuation bytes: a negated set counts each non-ASCII character once, by its lead byte
        if (neg) { a = ~a; b = ~b; c = 0; d = ~d; }
        return new Utf8ByteSet(a, b, c, d, neg);
    }

    /// <summary>This set plus UTF-8 continuation bytes when negated: deleting a member lead byte also deletes the
    /// rest of its character.</summary>
    internal Utf8ByteSet WithContinuations() => Negated ? new Utf8ByteSet(w0, w1, ulong.MaxValue, w3, true) : this;

    static void Set(ref ulong a, ref ulong b, ref ulong c, ref ulong d, byte x)
    {
        ulong bit = 1UL << (x & 63);
        switch (x >> 6) { case 0: a |= bit; break; case 1: b |= bit; break; case 2: c |= bit; break; default: d |= bit; break; }
    }

    /// <summary>Member mask of 32 bytes (0xFF = member).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector256<byte> Classify(Vector256<byte> v)
    {
        var hiA = Vector256.Create(Vector128.Create(1, 2, 4, 8, 16, 32, 64, 128, 0, 0, 0, 0, 0, 0, 0, (byte)0));
        var hiB = Vector256.Create(Vector128.Create(0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 16, 32, 64, (byte)128));
        var lo = v & Vector256.Create((byte)15);
        var hi = Vector256.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & Vector256.Create((byte)15);
        var la = Avx2.Shuffle(Vector256.Create(loA), lo);
        var lb = Avx2.Shuffle(Vector256.Create(loB), lo);
        var r = (la & Avx2.Shuffle(hiA, hi)) | (lb & Avx2.Shuffle(hiB, hi));
        return ~Vector256.Equals(r, Vector256<byte>.Zero);
    }

    /// <summary>Member mask of 64 bytes (0xFF = member).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector512<byte> Classify(Vector512<byte> v)
    {
        var hiA = Vector512.Create(Vector128.Create(1, 2, 4, 8, 16, 32, 64, 128, 0, 0, 0, 0, 0, 0, 0, (byte)0));
        var hiB = Vector512.Create(Vector128.Create(0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 16, 32, 64, (byte)128));
        var lo = v & Vector512.Create((byte)15);
        var hi = Vector512.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & Vector512.Create((byte)15);
        var la = Avx512BW.Shuffle(Vector512.Create(loA), lo);
        var lb = Avx512BW.Shuffle(Vector512.Create(loB), lo);
        var r = (la & Avx512BW.Shuffle(hiA, hi)) | (lb & Avx512BW.Shuffle(hiB, hi));
        return ~Vector512.Equals(r, Vector512<byte>.Zero);
    }
}
