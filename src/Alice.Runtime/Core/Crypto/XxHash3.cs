using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Alice.Runtime;

namespace Alice.Core.Crypto;

/// <summary>
/// XXH3-64 (xxHash 0.8, Yann Collet): a fast non-cryptographic hash, bit-exact with the reference
/// <c>XXH3_64bits_withSeed</c>. The long-input loop uses AVX2 when present. 0 bytes allocated. Not for security:
/// use <see cref="Digester"/> or <see cref="Hmac"/> there.
/// </summary>
public static class XxHash3
{
    const ulong P32_1 = 0x9E3779B1, P32_2 = 0x85EBCA77, P32_3 = 0xC2B2AE3D;
    const ulong P64_1 = 0x9E3779B185EBCA87, P64_2 = 0xC2B2AE3D27D4EB4F, P64_3 = 0x165667B19E3779F9, P64_4 = 0x85EBCA77C2B2AE63, P64_5 = 0x27D4EB2F165667C5;
    const ulong PMX1 = 0x165667919E3779F9, PMX2 = 0x9FB21C651E98DF25;
    const int SecretSize = 192, StripeLen = 64, StripesPerBlock = (SecretSize - StripeLen) / 8, BlockLen = StripeLen * StripesPerBlock;

    static ReadOnlySpan<byte> KSecret =>
    [
        0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
        0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
        0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
        0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
        0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
        0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
        0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
        0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
        0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
        0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
        0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
        0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
    ];

    /// <summary>XXH3-64 of <paramref name="data"/>.</summary>
    [Pure]
    public static ulong Hash64(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        int len = data.Length;
        var sec = KSecret;
        if (len <= 16) return Len0To16(data, sec, seed);
        if (len <= 128) return Len17To128(data, sec, seed);
        if (len <= 240) return Len129To240(data, sec, seed);
        if (seed == 0) return Long(data, sec);
        Span<byte> custom = stackalloc byte[SecretSize];
        for (int i = 0; i < SecretSize / 16; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(custom[(16 * i)..], R64(sec, 16 * i) + seed);
            BinaryPrimitives.WriteUInt64LittleEndian(custom[(16 * i + 8)..], R64(sec, 16 * i + 8) - seed);
        }
        return Long(data, custom);
    }

    /// <summary>XXH3-64 of the UTF-8 bytes of <paramref name="text"/> (no allocation up to 1 KB of text).</summary>
    [Pure]
    public static ulong Hash64(string text, ulong seed = 0)
    {
        int max = System.Text.Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        Span<byte> buf = max <= 1024 ? stackalloc byte[1024] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(max));
        int n = System.Text.Encoding.UTF8.GetBytes(text, buf);
        ulong h = Hash64(buf[..n], seed);
        if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong R64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s[o..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint R32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Fold(ulong a, ulong b)
    {
        ulong hi = Math.BigMul(a, b, out ulong lo);
        return lo ^ hi;
    }

    static ulong Avalanche(ulong h) { h ^= h >> 37; h *= PMX1; return h ^ (h >> 32); }

    static ulong Avalanche64(ulong h) { h ^= h >> 33; h *= P64_2; h ^= h >> 29; h *= P64_3; return h ^ (h >> 32); }

    static ulong Rrmxmx(ulong h, ulong len)
    {
        h ^= ulong.RotateLeft(h, 49) ^ ulong.RotateLeft(h, 24);
        h *= PMX2;
        h ^= (h >> 35) + len;
        h *= PMX2;
        return h ^ (h >> 28);
    }

    static ulong Len0To16(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, ulong seed)
    {
        int len = d.Length;
        if (len > 8)
        {
            ulong f1 = (R64(s, 24) ^ R64(s, 32)) + seed, f2 = (R64(s, 40) ^ R64(s, 48)) - seed;
            ulong lo = R64(d, 0) ^ f1, hi = R64(d, len - 8) ^ f2;
            ulong acc = (ulong)len + BinaryPrimitives.ReverseEndianness(lo) + hi + Fold(lo, hi);
            return Avalanche(acc);
        }
        if (len >= 4)
        {
            seed ^= (ulong)BinaryPrimitives.ReverseEndianness((uint)seed) << 32;
            ulong in1 = R32(d, 0), in2 = R32(d, len - 4);
            ulong flip = (R64(s, 8) ^ R64(s, 16)) - seed;
            return Rrmxmx((in2 + (in1 << 32)) ^ flip, (ulong)len);
        }
        if (len > 0)
        {
            uint c1 = d[0], c2 = d[len >> 1], c3 = d[len - 1];
            uint combined = (c1 << 16) | (c2 << 24) | c3 | ((uint)len << 8);
            ulong flip = (R32(s, 0) ^ R32(s, 4)) + seed;
            return Avalanche64(combined ^ flip);
        }
        return Avalanche64(seed ^ R64(s, 56) ^ R64(s, 64));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Mix16(ReadOnlySpan<byte> d, int o, ReadOnlySpan<byte> s, int so, ulong seed)
        => Fold(R64(d, o) ^ (R64(s, so) + seed), R64(d, o + 8) ^ (R64(s, so + 8) - seed));

    static ulong Len17To128(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, ulong seed)
    {
        int len = d.Length;
        ulong acc = (ulong)len * P64_1;
        if (len > 32)
        {
            if (len > 64)
            {
                if (len > 96) { acc += Mix16(d, 48, s, 96, seed); acc += Mix16(d, len - 64, s, 112, seed); }
                acc += Mix16(d, 32, s, 64, seed); acc += Mix16(d, len - 48, s, 80, seed);
            }
            acc += Mix16(d, 16, s, 32, seed); acc += Mix16(d, len - 32, s, 48, seed);
        }
        acc += Mix16(d, 0, s, 0, seed); acc += Mix16(d, len - 16, s, 16, seed);
        return Avalanche(acc);
    }

    static ulong Len129To240(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, ulong seed)
    {
        int len = d.Length, rounds = len / 16;
        ulong acc = (ulong)len * P64_1;
        for (int i = 0; i < 8; i++) acc += Mix16(d, 16 * i, s, 16 * i, seed);
        ulong end = Mix16(d, len - 16, s, 136 - 17, seed);
        acc = Avalanche(acc);
        for (int i = 8; i < rounds; i++) end += Mix16(d, 16 * i, s, 16 * (i - 8) + 3, seed);
        return Avalanche(acc + end);
    }

    static ulong Long(ReadOnlySpan<byte> d, ReadOnlySpan<byte> s)
    {
        Span<ulong> acc = stackalloc ulong[8] { P32_3, P64_1, P64_2, P64_3, P64_4, P32_2, P64_5, P32_1 };
        int len = d.Length, blocks = (len - 1) / BlockLen;
        for (int b = 0; b < blocks; b++)
        {
            Accumulate(acc, d[(b * BlockLen)..], s, StripesPerBlock);
            Scramble(acc, s[(SecretSize - StripeLen)..]);
        }
        int stripes = (len - 1 - BlockLen * blocks) / StripeLen;
        Accumulate(acc, d[(blocks * BlockLen)..], s, stripes);
        Accumulate512(acc, d[(len - StripeLen)..], s[(SecretSize - StripeLen - 7)..]);
        ulong r = (ulong)len * P64_1;
        for (int i = 0; i < 4; i++) r += Fold(acc[2 * i] ^ R64(s, 11 + 16 * i), acc[2 * i + 1] ^ R64(s, 11 + 16 * i + 8));
        return Avalanche(r);
    }

    static void Accumulate(Span<ulong> acc, ReadOnlySpan<byte> d, ReadOnlySpan<byte> s, int stripes)
    {
        if (Avx2.IsSupported)
        {
            ref ulong a = ref MemoryMarshal.GetReference(acc);
            var a0 = Vector256.LoadUnsafe(ref a);
            var a1 = Vector256.LoadUnsafe(ref a, 4);
            ref byte dp = ref MemoryMarshal.GetReference(d);
            ref byte sp = ref MemoryMarshal.GetReference(s);
            for (int n = 0; n < stripes; n++)
            {
                var d0 = Vector256.LoadUnsafe(ref dp, (nuint)(n * StripeLen)).AsUInt64();
                var d1 = Vector256.LoadUnsafe(ref dp, (nuint)(n * StripeLen + 32)).AsUInt64();
                var k0 = d0 ^ Vector256.LoadUnsafe(ref sp, (nuint)(n * 8)).AsUInt64();
                var k1 = d1 ^ Vector256.LoadUnsafe(ref sp, (nuint)(n * 8 + 32)).AsUInt64();
                a0 += Avx2.Multiply(k0.AsUInt32(), Avx2.Shuffle(k0.AsUInt32(), 0xB1)) + Avx2.Shuffle(d0.AsUInt32(), 0x4E).AsUInt64();
                a1 += Avx2.Multiply(k1.AsUInt32(), Avx2.Shuffle(k1.AsUInt32(), 0xB1)) + Avx2.Shuffle(d1.AsUInt32(), 0x4E).AsUInt64();
            }
            a0.StoreUnsafe(ref a);
            a1.StoreUnsafe(ref a, 4);
            return;
        }
        for (int n = 0; n < stripes; n++) Accumulate512(acc, d[(n * StripeLen)..], s[(n * 8)..]);
    }

    static void Accumulate512(Span<ulong> acc, ReadOnlySpan<byte> d, ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < 8; i++)
        {
            ulong v = R64(d, 8 * i), k = v ^ R64(s, 8 * i);
            acc[i ^ 1] += v;
            acc[i] += (k & 0xFFFFFFFF) * (k >> 32);
        }
    }

    static void Scramble(Span<ulong> acc, ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < 8; i++)
        {
            ulong a = acc[i];
            a ^= a >> 47;
            a ^= R64(s, 8 * i);
            acc[i] = a * P32_1;
        }
    }
}
