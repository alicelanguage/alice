using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Alice.Runtime;

namespace Alice.Core.Crypto;

/// <summary>
/// CRC-32C (Castagnoli, iSCSI/ext4/RocksDB), as in Ruby's `digest-crc` `Digest::CRC32c`. Hardware instruction on x64
/// (SSE4.2 <c>crc32</c>) and ARM64 (<c>crc32c</c>); large inputs run three independent streams to hide the
/// instruction latency and join them with the CRC combine identity. A table fallback elsewhere. 0 bytes allocated.
/// </summary>
public static class Crc32C
{
    const uint Poly = 0x82F63B78;   // reflected Castagnoli polynomial
    const int Stripe = 8192;        // bytes per stream in the three-way loop

    /// <summary>CRC-32C of <paramref name="data"/>.</summary>
    [Pure]
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>Continues a CRC-32C: <c>Append(Compute(a), b) == Compute(a + b)</c>.</summary>
    [Pure]
    public static uint Append(uint crc, ReadOnlySpan<byte> data) => ~Raw(~crc, data);

    /// <summary>CRC-32C of <paramref name="data"/> as 8 lowercase hex digits (Ruby `hexdigest`).</summary>
    [Pure]
    public static string HexDigest(ReadOnlySpan<byte> data) => Compute(data).ToString("x8");

    /// <summary>The CRC of <c>a + b</c> from the CRCs of <c>a</c> and <c>b</c> and the length of <c>b</c>.</summary>
    [Pure]
    public static uint Combine(uint crcA, uint crcB, long lengthB) => MultModP(X8nModP(lengthB), crcA) ^ crcB;

    static uint Raw(uint s, ReadOnlySpan<byte> data)
    {
        ref byte p = ref MemoryMarshal.GetReference(data);
        nuint n = (nuint)data.Length, i = 0;
        if (Sse42.X64.IsSupported)
        {
            if (n >= 3 * Stripe)
            {
                uint k = StripeShift.K;
                for (; i + 3 * Stripe <= n; i += 3 * Stripe)
                {
                    ulong a = s, b = 0, c = 0;
                    for (nuint j = 0; j < Stripe; j += 8)
                    {
                        a = Sse42.X64.Crc32(a, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i + j)));
                        b = Sse42.X64.Crc32(b, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i + Stripe + j)));
                        c = Sse42.X64.Crc32(c, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i + 2 * Stripe + j)));
                    }
                    // raw CRC is linear: state(A|B|C) = ((a * x^8S) ^ b) * x^8S ^ c
                    s = MultModP(k, MultModP(k, (uint)a) ^ (uint)b) ^ (uint)c;
                }
            }
            ulong v = s;
            for (; i + 8 <= n; i += 8) v = Sse42.X64.Crc32(v, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i)));
            s = (uint)v;
            for (; i < n; i++) s = Sse42.Crc32(s, Unsafe.Add(ref p, i));
            return s;
        }
        if (Crc32.Arm64.IsSupported)
        {
            for (; i + 8 <= n; i += 8) s = Crc32.Arm64.ComputeCrc32C(s, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i)));
            for (; i < n; i++) s = Crc32.ComputeCrc32C(s, Unsafe.Add(ref p, i));
            return s;
        }
        var t = Table.Value;
        for (; i < n; i++) s = t[(byte)(s ^ Unsafe.Add(ref p, i))] ^ (s >> 8);
        return s;
    }

    // multiplication modulo P of two reflected polynomials (zlib's multmodp; x^0 is bit 31)
    static uint MultModP(uint a, uint b)
    {
        uint m = 1u << 31, p = 0;
        while (true)
        {
            if ((a & m) != 0)
            {
                p ^= b;
                if ((a & (m - 1)) == 0) break;
            }
            m >>= 1;
            b = (b & 1) != 0 ? (b >> 1) ^ Poly : b >> 1;
        }
        return p;
    }

    // x^(8n) mod P
    static uint X8nModP(long n)
    {
        uint p = 1u << 31, sq = 1u << 30;   // x^0, x^1
        for (int k = 0; k < 3; k++) sq = MultModP(sq, sq);   // x^8
        while (n != 0)
        {
            if ((n & 1) != 0) p = MultModP(sq, p);
            sq = MultModP(sq, sq);
            n >>= 1;
        }
        return p;
    }

    static class StripeShift { internal static readonly uint K = X8nModP(Stripe); }

    static class Table
    {
        internal static readonly uint[] Value = Build();
        static uint[] Build()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ Poly : c >> 1;
                t[i] = c;
            }
            return t;
        }
    }

    /// <summary>Big-endian bytes of a CRC (the digest form).</summary>
    public static void WriteDigest(uint crc, Span<byte> destination) => BinaryPrimitives.WriteUInt32BigEndian(destination, crc);
}
