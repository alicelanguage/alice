using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Alice.Runtime;

/// <summary>Brain float: the upper 16 bits of an IEEE float32 (8-bit exponent, 7-bit mantissa).</summary>
[StructLayout(LayoutKind.Sequential, Size = 2)]
public readonly struct BFloat16 : IEquatable<BFloat16>
{
    public readonly ushort Bits;

    BFloat16(ushort bits) => Bits = bits;

    public static BFloat16 FromBits(ushort bits) => new(bits);

    /// <summary>Round to nearest even; NaN stays NaN.</summary>
    public static explicit operator BFloat16(float f)
    {
        uint u = BitConverter.SingleToUInt32Bits(f);
        if (float.IsNaN(f)) return new((ushort)((u >> 16) | 0x40));
        u += 0x7FFF + ((u >> 16) & 1);
        return new((ushort)(u >> 16));
    }

    public static explicit operator float(BFloat16 b) => BitConverter.UInt32BitsToSingle((uint)b.Bits << 16);

    public bool Equals(BFloat16 o) => Bits == o.Bits;
    public override bool Equals(object? o) => o is BFloat16 b && Equals(b);
    public override int GetHashCode() => Bits;
    public override string ToString() => ((float)this).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static void Convert(ReadOnlySpan<float> src, Span<BFloat16> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("destination too short");
        for (int i = 0; i < src.Length; i++) dst[i] = (BFloat16)src[i];
    }

    /// <summary>Widening is a 16-bit shift, vectorized.</summary>
    public static void Convert(ReadOnlySpan<BFloat16> src, Span<float> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("destination too short");
        var s = MemoryMarshal.Cast<BFloat16, ushort>(src);
        var d = MemoryMarshal.Cast<float, uint>(dst);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
            for (; i + 16 <= s.Length; i += 16)
            {
                var (lo, hi) = Vector256.Widen(Vector256.Create(s.Slice(i, 16)));
                Vector256.ShiftLeft(lo, 16).CopyTo(d.Slice(i));
                Vector256.ShiftLeft(hi, 16).CopyTo(d.Slice(i + 8));
            }
        for (; i < s.Length; i++) d[i] = (uint)s[i] << 16;
    }
}

/// <summary>Span conversions for <see cref="Half"/>.</summary>
public static class HalfConvert
{
    public static void Convert(ReadOnlySpan<float> src, Span<Half> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("destination too short");
        for (int i = 0; i < src.Length; i++) dst[i] = (Half)src[i];
    }

    public static void Convert(ReadOnlySpan<Half> src, Span<float> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("destination too short");
        for (int i = 0; i < src.Length; i++) dst[i] = (float)src[i];
    }
}
