using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Core.Json;

/// <summary>
/// JSON number grammar and conversion. Integers become Int64, then UInt64, then Float64 on overflow (Ruby's order);
/// anything with `.`, `e` or `E` is Float64. Floats: Clinger's exact fast path, then Eisel-Lemire (128-bit table in
/// <c>Numbers.Pow5.cs</c>), then <see cref="double.Parse(ReadOnlySpan{byte}, NumberStyles, IFormatProvider?)"/> for
/// more than 19 significant digits. Every path is correctly rounded (differential test against double.Parse).
/// </summary>
static partial class Numbers
{
    static ReadOnlySpan<double> ExactPow10 =>
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
        1e20, 1e21, 1e22,
    ];

    /// <summary>
    /// Parses the number starting at <paramref name="pos"/>. Returns the end offset (exclusive), or -1 when the bytes
    /// are not a JSON number followed by a terminator (<see cref="Chars.EndsScalar"/>). <paramref name="raw"/> holds
    /// the Int64/UInt64 value or the IEEE bits of the double.
    /// </summary>
    public static int Parse(ReadOnlySpan<byte> buf, int pos, out JsonKind kind, out ulong raw)
    {
        kind = JsonKind.Undefined;
        raw = 0;
        int n = buf.Length, p = pos;
        if ((uint)p >= (uint)n) return -1;
        ref byte b0 = ref MemoryMarshal.GetReference(buf);
        bool neg = Unsafe.Add(ref b0, p) == '-';
        if (neg && ++p >= n) return -1;
        int intStart = p;
        ulong mant = 0;
        uint d = (uint)(Unsafe.Add(ref b0, p) - '0');
        if (d == 0) p++;
        else if (d <= 9)
        {
            mant = d;
            p++;
            while (p + 8 <= n)
            {
                ulong eight = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, p));
                if (!IsEightDigits(eight)) break;
                mant = mant * 100000000 + ParseEight(eight);
                p += 8;
            }
            while (p < n && (d = (uint)(Unsafe.Add(ref b0, p) - '0')) <= 9) { mant = mant * 10 + d; p++; }
        }
        else return -1;
        int digits = p - intStart;
        bool isFloat = false;
        long exp10 = 0;
        if (p < n && Unsafe.Add(ref b0, p) == '.')
        {
            isFloat = true;
            int fracStart = ++p;
            while (p + 8 <= n)
            {
                ulong eight = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, p));
                if (!IsEightDigits(eight)) break;
                mant = mant * 100000000 + ParseEight(eight);
                p += 8;
            }
            while (p < n && (d = (uint)(Unsafe.Add(ref b0, p) - '0')) <= 9) { mant = mant * 10 + d; p++; }
            if (p == fracStart) return -1;
            digits += p - fracStart;
            exp10 = fracStart - p;
        }
        if (p < n && (Unsafe.Add(ref b0, p) | 0x20) == 'e')
        {
            isFloat = true;
            p++;
            bool eneg = false;
            if (p < n && Unsafe.Add(ref b0, p) is (byte)'-' or (byte)'+') { eneg = Unsafe.Add(ref b0, p) == '-'; p++; }
            int expStart = p;
            long e = 0;
            while (p < n && (d = (uint)(Unsafe.Add(ref b0, p) - '0')) <= 9) { if (e < 100_000_000) e = e * 10 + d; p++; }
            if (p == expStart) return -1;
            exp10 += eneg ? -e : e;
        }
        if (p < n && !Chars.IsTerminator(Unsafe.Add(ref b0, p))) return -1;

        if (digits > 19 && SignificantDigits(buf.Slice(intStart, p - intStart)) > 19)
            return Slow(buf.Slice(pos, p - pos), neg, isFloat, out kind, out raw) ? p : -1;

        if (!isFloat)
        {
            if (!neg)
            {
                kind = mant <= long.MaxValue ? JsonKind.Int64 : JsonKind.UInt64;
                raw = mant;
                return p;
            }
            if (mant <= 1UL << 63) { kind = JsonKind.Int64; raw = (ulong)(-(long)mant); return p; }
            kind = JsonKind.Float64;
            raw = BitConverter.DoubleToUInt64Bits(-(double)mant); // < 2^64: exactly rounded by the conversion
            return p;
        }
        kind = JsonKind.Float64;
        raw = BitConverter.DoubleToUInt64Bits(ToDouble(mant, exp10, neg));
        return p;
    }

    /// <summary>True when the 8 little-endian bytes are all ASCII digits (fast_float).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsEightDigits(ulong v) => ((v & 0xF0F0F0F0F0F0F0F0UL) | (((v + 0x0606060606060606UL) & 0xF0F0F0F0F0F0F0F0UL) >> 4)) == 0x3333333333333333UL;

    /// <summary>Value of 8 ASCII digits (SWAR, fast_float's parse_eight_digits_unrolled).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint ParseEight(ulong v)
    {
        const ulong mask = 0x000000FF000000FFUL, mul1 = 0x000F424000000064UL, mul2 = 0x0000271000000001UL;
        v -= 0x3030303030303030UL;
        v = (v * 10) + (v >> 8);
        return (uint)((((v & mask) * mul1) + (((v >> 16) & mask) * mul2)) >> 32);
    }

    /// <summary>Kind of the number token at <paramref name="pos"/> by inspection only (no conversion): Float64 with a
    /// fraction or exponent, Int64 up to 18 digits, otherwise resolved by <see cref="Parse"/>.</summary>
    public static JsonKind QuickKind(ReadOnlySpan<byte> buf, int pos)
    {
        int p = pos, n = buf.Length;
        if (p < n && buf[p] == '-') p++;
        int start = p;
        while (p < n && (uint)(buf[p] - '0') <= 9) p++;
        if (p > start && p - start <= 18 && (p >= n || Chars.IsTerminator(buf[p])) && (buf[start] != '0' || p - start == 1)) return JsonKind.Int64;
        if (p > start && p < n && (buf[p] == '.' || (buf[p] | 0x20) == 'e')) return JsonKind.Float64; // validated when read
        return Parse(buf, pos, out var kind, out _) < 0 ? JsonKind.Undefined : kind;
    }

    /// <summary>Digits of a number token from its first digit, without leading zeros of the integer/fraction part.</summary>
    static int SignificantDigits(ReadOnlySpan<byte> s)
    {
        int count = 0;
        bool started = false;
        foreach (byte b in s)
        {
            if (b == '.') continue;
            if ((uint)(b - '0') > 9) break;
            if (b != '0') started = true;
            if (started) count++;
        }
        return count;
    }

    static bool Slow(ReadOnlySpan<byte> token, bool neg, bool isFloat, out JsonKind kind, out ulong raw)
    {
        if (!isFloat)
        {
            if (!neg && ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u))
            {
                kind = u <= long.MaxValue ? JsonKind.Int64 : JsonKind.UInt64;
                raw = u;
                return true;
            }
            if (neg && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
            {
                kind = JsonKind.Int64;
                raw = (ulong)l;
                return true;
            }
        }
        kind = JsonKind.Float64;
        bool ok = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        raw = BitConverter.DoubleToUInt64Bits(v);
        return ok;
    }

    /// <summary>mant * 10^exp10, correctly rounded; <paramref name="mant"/> holds at most 19 significant digits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDouble(ulong mant, long exp10, bool neg)
    {
        if (mant <= 1UL << 53 && exp10 >= -22 && exp10 <= 22)
        {
            double v = mant;
            v = exp10 < 0 ? v / ExactPow10[(int)-exp10] : v * ExactPow10[(int)exp10];
            return neg ? -v : v;
        }
        return EiselLemire(mant, exp10, neg);
    }

    /// <summary>Eisel-Lemire (Lemire, "Number Parsing at a Gigabyte per Second", 2021; fast_float's compute_float).</summary>
    static double EiselLemire(ulong w, long q, bool neg)
    {
        ulong sign = neg ? 1UL << 63 : 0;
        if (w == 0 || q < -342) return BitConverter.UInt64BitsToDouble(sign);
        if (q > 308) return BitConverter.UInt64BitsToDouble(sign | 0x7FF0000000000000UL);
        int lz = BitOperations.LeadingZeroCount(w);
        w <<= lz;
        int index = 2 * (int)(q + 342);
        var pow5 = Pow5;
        ulong hi = Math.BigMul(w, pow5[index], out ulong lo);
        const ulong precisionMask = ulong.MaxValue >> 55;
        if ((hi & precisionMask) == precisionMask)
        {
            ulong hi2 = Math.BigMul(w, pow5[index + 1], out _);
            lo += hi2;
            if (hi2 > lo) hi++;
        }
        int upperbit = (int)(hi >> 63);
        int shift = upperbit + 64 - 52 - 3;
        ulong mantissa = hi >> shift;
        int power2 = (int)((((152170 + 65536) * q) >> 16) + 63) + upperbit - lz + 1023;
        if (power2 <= 0)
        {
            if (-power2 + 1 >= 64) return BitConverter.UInt64BitsToDouble(sign);
            mantissa >>= -power2 + 1;
            mantissa += mantissa & 1;
            mantissa >>= 1;
            power2 = mantissa < 1UL << 52 ? 0 : 1;
            return BitConverter.UInt64BitsToDouble(sign | mantissa | ((ulong)power2 << 52));
        }
        if (lo <= 1 && q >= -4 && q <= 23 && (mantissa & 3) == 1 && (mantissa << shift) == hi)
            mantissa &= ~1UL;
        mantissa += mantissa & 1;
        mantissa >>= 1;
        if (mantissa >= 2UL << 52)
        {
            mantissa = 1UL << 52;
            power2++;
        }
        mantissa &= ~(1UL << 52);
        if (power2 >= 0x7FF) return BitConverter.UInt64BitsToDouble(sign | 0x7FF0000000000000UL);
        return BitConverter.UInt64BitsToDouble(sign | mantissa | ((ulong)power2 << 52));
    }

    /// <summary>End offset of the number token at <paramref name="pos"/> (no validation: the tape already did it).</summary>
    public static int TokenEnd(ReadOnlySpan<byte> buf, int pos)
    {
        int p = pos;
        while (p < buf.Length && !Chars.IsTerminator(buf[p])) p++;
        return p;
    }
}
