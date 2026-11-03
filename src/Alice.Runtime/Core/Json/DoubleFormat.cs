using System.Numerics;
using System.Runtime.CompilerServices;

namespace Alice.Core.Json;

/// <summary>
/// Shortest round-trip double to UTF-8 (Ryu, Adams 2018, the d2d core with 125-bit tables in
/// <c>DoubleFormat.Tables.cs</c>). Layout equals .NET's <c>double.ToString(InvariantCulture)</c> (shortest, "G"): fixed notation
/// for decimal exponents -5 &lt; e &lt; 17, otherwise `d.dddE+XX` (tested bit for bit against it). About 4x faster than
/// <c>double.TryFormat</c> on canada.json.
/// </summary>
static partial class DoubleFormat
{
    /// <summary>Writes a finite <paramref name="value"/>; returns the byte count (at most 25).</summary>
    public static int Format(double value, Span<byte> dest)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        bool neg = (bits >> 63) != 0;
        ulong mant = bits & ((1UL << 52) - 1);
        int expBits = (int)((bits >> 52) & 0x7FF);
        int w = 0;
        if (neg) dest[w++] = (byte)'-';
        if (expBits == 0 && mant == 0)
        {
            dest[w++] = (byte)'0';
            return w;
        }
        D2d(mant, expBits, out ulong digits, out int exp);
        return w + Layout(digits, exp, dest.Slice(w));
    }

    static int Layout(ulong digits, int exp, Span<byte> d)
    {
        Span<byte> tmp = stackalloc byte[20];
        int n = 0;
        do
        {
            ulong q = digits / 10;
            tmp[19 - n++] = (byte)('0' + (int)(digits - q * 10));
            digits = q;
        } while (digits != 0);
        var ds = tmp.Slice(20 - n, n);
        int e = exp + n - 1; // decimal exponent of the first digit
        int w = 0;
        if (e > -5 && e < 17)
        {
            if (e >= 0)
            {
                if (n <= e + 1)
                {
                    ds.CopyTo(d);
                    w = n;
                    d.Slice(w, e + 1 - n).Fill((byte)'0');
                    return e + 1;
                }
                ds.Slice(0, e + 1).CopyTo(d);
                w = e + 1;
                d[w++] = (byte)'.';
                ds.Slice(e + 1).CopyTo(d.Slice(w));
                return w + n - e - 1;
            }
            d[w++] = (byte)'0';
            d[w++] = (byte)'.';
            for (int z = 0; z < -e - 1; z++) d[w++] = (byte)'0';
            ds.CopyTo(d.Slice(w));
            return w + n;
        }
        d[w++] = ds[0];
        if (n > 1)
        {
            d[w++] = (byte)'.';
            ds.Slice(1).CopyTo(d.Slice(w));
            w += n - 1;
        }
        d[w++] = (byte)'E';
        d[w++] = e < 0 ? (byte)'-' : (byte)'+';
        int a = Math.Abs(e);
        if (a >= 100) { d[w++] = (byte)('0' + a / 100); a %= 100; d[w++] = (byte)('0' + a / 10); d[w++] = (byte)('0' + a % 10); }
        else { d[w++] = (byte)('0' + a / 10); d[w++] = (byte)('0' + a % 10); }
        return w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Pow5Bits(int e) => (int)(((uint)e * 1217359) >> 19) + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Log10Pow2(int e) => (int)(((uint)e * 78913) >> 18);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Log10Pow5(int e) => (int)(((uint)e * 732923) >> 20);

    static int Pow5Factor(ulong v)
    {
        int c = 0;
        while (v % 5 == 0) { v /= 5; c++; }
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool MultipleOfPowerOf5(ulong v, int p) => Pow5Factor(v) >= p;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool MultipleOfPowerOf2(ulong v, int p) => (v & ((1UL << p) - 1)) == 0;

    /// <summary>(m * mul) >> j for a 128-bit mul given as (lo, hi), j >= 64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MulShift64(ulong m, ulong lo, ulong hi, int j)
    {
        ulong high1 = Math.BigMul(m, hi, out ulong low1);
        ulong high0 = Math.BigMul(m, lo, out _);
        ulong sum = high0 + low1;
        if (sum < high0) high1++;
        int s = j - 64;
        return s == 0 ? sum : (high1 << (64 - s)) | (sum >> s);
    }

    /// <summary>Ryu d2d: the shortest decimal <paramref name="output"/> x 10^<paramref name="exp"/> that rounds to
    /// the double with these IEEE fields.</summary>
    static void D2d(ulong ieeeMantissa, int ieeeExponent, out ulong output, out int exp)
    {
        int e2;
        ulong m2;
        if (ieeeExponent == 0)
        {
            e2 = 1 - 1023 - 52 - 2;
            m2 = ieeeMantissa;
        }
        else
        {
            e2 = ieeeExponent - 1023 - 52 - 2;
            m2 = (1UL << 52) | ieeeMantissa;
        }
        bool even = (m2 & 1) == 0, acceptBounds = even;
        ulong mv = 4 * m2;
        uint mmShift = ieeeMantissa != 0 || ieeeExponent <= 1 ? 1u : 0u;
        ulong vr, vp, vm;
        int e10;
        bool vmIsTrailingZeros = false, vrIsTrailingZeros = false;
        if (e2 >= 0)
        {
            int q = Log10Pow2(e2) - (e2 > 3 ? 1 : 0);
            e10 = q;
            int k = 125 + Pow5Bits(q) - 1;
            int i = -e2 + q + k;
            ulong lo = Pow5InvSplit[2 * q], hi = Pow5InvSplit[2 * q + 1];
            vr = MulShift64(4 * m2, lo, hi, i);
            vp = MulShift64(4 * m2 + 2, lo, hi, i);
            vm = MulShift64(4 * m2 - 1 - mmShift, lo, hi, i);
            if (q <= 21)
            {
                if (mv % 5 == 0) vrIsTrailingZeros = MultipleOfPowerOf5(mv, q);
                else if (acceptBounds) vmIsTrailingZeros = MultipleOfPowerOf5(mv - 1 - mmShift, q);
                else vp -= MultipleOfPowerOf5(mv + 2, q) ? 1UL : 0UL;
            }
        }
        else
        {
            int q = Log10Pow5(-e2) - (-e2 > 1 ? 1 : 0);
            e10 = q + e2;
            int i = -e2 - q;
            int k = Pow5Bits(i) - 125;
            int j = q - k;
            ulong lo = Pow5Split[2 * i], hi = Pow5Split[2 * i + 1];
            vr = MulShift64(4 * m2, lo, hi, j);
            vp = MulShift64(4 * m2 + 2, lo, hi, j);
            vm = MulShift64(4 * m2 - 1 - mmShift, lo, hi, j);
            if (q <= 1)
            {
                vrIsTrailingZeros = true;
                if (acceptBounds) vmIsTrailingZeros = mmShift == 1;
                else --vp;
            }
            else if (q < 63)
            {
                vrIsTrailingZeros = MultipleOfPowerOf2(mv, q);
            }
        }

        int removed = 0;
        int lastRemovedDigit = 0;
        if (vmIsTrailingZeros || vrIsTrailingZeros)
        {
            while (true)
            {
                ulong vpDiv10 = vp / 10, vmDiv10 = vm / 10;
                if (vpDiv10 <= vmDiv10) break;
                int vmMod10 = (int)(vm - 10 * vmDiv10);
                ulong vrDiv10 = vr / 10;
                int vrMod10 = (int)(vr - 10 * vrDiv10);
                vmIsTrailingZeros &= vmMod10 == 0;
                vrIsTrailingZeros &= lastRemovedDigit == 0;
                lastRemovedDigit = vrMod10;
                vr = vrDiv10; vp = vpDiv10; vm = vmDiv10;
                removed++;
            }
            if (vmIsTrailingZeros)
            {
                while (true)
                {
                    ulong vmDiv10 = vm / 10;
                    int vmMod10 = (int)(vm - 10 * vmDiv10);
                    if (vmMod10 != 0) break;
                    ulong vpDiv10 = vp / 10, vrDiv10 = vr / 10;
                    int vrMod10 = (int)(vr - 10 * vrDiv10);
                    vrIsTrailingZeros &= lastRemovedDigit == 0;
                    lastRemovedDigit = vrMod10;
                    vr = vrDiv10; vp = vpDiv10; vm = vmDiv10;
                    removed++;
                }
            }
            if (vrIsTrailingZeros && lastRemovedDigit == 5 && vr % 2 == 0) lastRemovedDigit = 4;
            output = vr + ((vr == vm && (!acceptBounds || !vmIsTrailingZeros)) || lastRemovedDigit >= 5 ? 1UL : 0UL);
        }
        else
        {
            bool roundUp = false;
            ulong vpDiv100 = vp / 100, vmDiv100 = vm / 100;
            if (vpDiv100 > vmDiv100)
            {
                ulong vrDiv100 = vr / 100;
                int vrMod100 = (int)(vr - 100 * vrDiv100);
                roundUp = vrMod100 >= 50;
                vr = vrDiv100; vp = vpDiv100; vm = vmDiv100;
                removed += 2;
            }
            while (true)
            {
                ulong vpDiv10 = vp / 10, vmDiv10 = vm / 10;
                if (vpDiv10 <= vmDiv10) break;
                ulong vrDiv10 = vr / 10;
                int vrMod10 = (int)(vr - 10 * vrDiv10);
                roundUp = vrMod10 >= 5;
                vr = vrDiv10; vp = vpDiv10; vm = vmDiv10;
                removed++;
            }
            output = vr + (vr == vm || roundUp ? 1UL : 0UL);
        }
        exp = e10 + removed;
    }
}
