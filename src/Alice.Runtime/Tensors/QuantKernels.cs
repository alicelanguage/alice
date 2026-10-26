using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Alice.Runtime;

/// <summary>
/// Fused dequantize-dot kernels over quantized blocks (layout in <see cref="QuantFormat"/>). Tiers: AVX-512F, AVX2
/// (sign/zero-extending loads straight to int32 lanes), generic Vector128 (SSE/NEON), scalar. The scale is applied
/// once per block; one horizontal sum per row.
/// </summary>
public static unsafe class QuantKernels
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float DotInt8(byte* p, float* x, int blocks)
    {
        if (Avx512F.IsSupported)
        {
            var acc = Vector512<float>.Zero;
            for (int b = 0; b < blocks; b++, p += 34, x += 32)
            {
                var q = (sbyte*)(p + 2);
                var v0 = Vector512.ConvertToSingle(Avx512F.ConvertToVector512Int32(Vector128.Load(q)));
                var v1 = Vector512.ConvertToSingle(Avx512F.ConvertToVector512Int32(Vector128.Load(q + 16)));
                var s = v0 * Vector512.Load(x) + v1 * Vector512.Load(x + 16);
                acc = Vector512.FusedMultiplyAdd(s, Vector512.Create((float)*(Half*)p), acc);
            }
            return Vector512.Sum(acc);
        }
        if (Avx2.IsSupported)
        {
            var acc = Vector256<float>.Zero;
            for (int b = 0; b < blocks; b++, p += 34, x += 32)
            {
                var q = (sbyte*)(p + 2);
                var s = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(q)) * Vector256.Load(x);
                s = Vector256.FusedMultiplyAdd(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(q + 8)), Vector256.Load(x + 8), s);
                s = Vector256.FusedMultiplyAdd(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(q + 16)), Vector256.Load(x + 16), s);
                s = Vector256.FusedMultiplyAdd(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(q + 24)), Vector256.Load(x + 24), s);
                acc = Vector256.FusedMultiplyAdd(s, Vector256.Create((float)*(Half*)p), acc);
            }
            return Vector256.Sum(acc);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var acc = Vector128<float>.Zero;
            for (int b = 0; b < blocks; b++, p += 34, x += 32)
            {
                var q = (sbyte*)(p + 2);
                var s = Dot16(Vector128.Load(q), x) + Dot16(Vector128.Load(q + 16), x + 16);
                acc += s * Vector128.Create((float)*(Half*)p);
            }
            return Vector128.Sum(acc);
        }
        float sum = 0;
        for (int b = 0; b < blocks; b++, p += 34, x += 32)
        {
            var q = (sbyte*)(p + 2);
            float s = 0;
            for (int i = 0; i < 32; i++) s += q[i] * x[i];
            sum += s * (float)*(Half*)p;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float DotInt4(byte* p, float* x, int blocks)
    {
        var mask = Vector128.Create((byte)0x0F);
        var eight = Vector128.Create((byte)8);
        if (Avx512F.IsSupported)
        {
            var acc = Vector512<float>.Zero;
            for (int b = 0; b < blocks; b++, p += 18, x += 32)
            {
                var raw = Vector128.Load(p + 2);
                var lo = ((raw & mask) - eight).AsSByte();
                var hi = (Vector128.ShiftRightLogical(raw, 4) - eight).AsSByte();
                var s = Vector512.ConvertToSingle(Avx512F.ConvertToVector512Int32(lo)) * Vector512.Load(x)
                      + Vector512.ConvertToSingle(Avx512F.ConvertToVector512Int32(hi)) * Vector512.Load(x + 16);
                acc = Vector512.FusedMultiplyAdd(s, Vector512.Create((float)*(Half*)p), acc);
            }
            return Vector512.Sum(acc);
        }
        if (Avx2.IsSupported)
        {
            var acc = Vector256<float>.Zero;
            for (int b = 0; b < blocks; b++, p += 18, x += 32)
            {
                var raw = Vector128.Load(p + 2);
                var lo = ((raw & mask) - eight).AsSByte();
                var hi = (Vector128.ShiftRightLogical(raw, 4) - eight).AsSByte();
                var s = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(lo)) * Vector256.Load(x);
                s = Vector256.FusedMultiplyAdd(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(lo, 8))), Vector256.Load(x + 8), s);
                s = Vector256.FusedMultiplyAdd(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(hi)), Vector256.Load(x + 16), s);
                s = Vector256.FusedMultiplyAdd(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(Sse2.ShiftRightLogical128BitLane(hi, 8))), Vector256.Load(x + 24), s);
                acc = Vector256.FusedMultiplyAdd(s, Vector256.Create((float)*(Half*)p), acc);
            }
            return Vector256.Sum(acc);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var acc = Vector128<float>.Zero;
            for (int b = 0; b < blocks; b++, p += 18, x += 32)
            {
                var raw = Vector128.Load(p + 2);
                var s = Dot16(((raw & mask) - eight).AsSByte(), x) + Dot16((Vector128.ShiftRightLogical(raw, 4) - eight).AsSByte(), x + 16);
                acc += s * Vector128.Create((float)*(Half*)p);
            }
            return Vector128.Sum(acc);
        }
        float sum = 0;
        for (int b = 0; b < blocks; b++, p += 18, x += 32)
        {
            float s = 0;
            for (int i = 0; i < 16; i++) s += ((p[2 + i] & 0x0F) - 8) * x[i] + ((p[2 + i] >> 4) - 8) * x[i + 16];
            sum += s * (float)*(Half*)p;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<float> Dot16(Vector128<sbyte> q, float* x)
    {
        var (s0, s1) = Vector128.Widen(q);
        var (i0, i1) = Vector128.Widen(s0);
        var (i2, i3) = Vector128.Widen(s1);
        return Vector128.ConvertToSingle(i0) * Vector128.Load(x) + Vector128.ConvertToSingle(i1) * Vector128.Load(x + 4)
             + Vector128.ConvertToSingle(i2) * Vector128.Load(x + 8) + Vector128.ConvertToSingle(i3) * Vector128.Load(x + 12);
    }
}
