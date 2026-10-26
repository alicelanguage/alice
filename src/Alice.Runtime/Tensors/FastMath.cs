using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Alice.Runtime;

/// <summary>
/// exp(x) = 2^n * p(r), r = x - n ln2 in [-ln2/2, ln2/2], degree-6 polynomial (rel. error ~1e-7 for x in [-87, 88]).
/// Same algorithm in scalar, Vector128 and Vector256 so every tier gives the same result.
/// </summary>
public static class FastMath
{
    const float Log2E = 1.44269504f, Ln2Hi = 0.693359375f, Ln2Lo = -2.12194440e-4f, Lo = -87.3f, Hi = 88.7f;
    const float C2 = 0.5f, C3 = 1f / 6, C4 = 1f / 24, C5 = 1f / 120, C6 = 1f / 720;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Exp(float x)
    {
        x = Math.Clamp(x, Lo, Hi);
        float n = MathF.Floor(x * Log2E + 0.5f);
        float r = x - n * Ln2Hi - n * Ln2Lo;
        float p = 1 + r * (1 + r * (C2 + r * (C3 + r * (C4 + r * (C5 + r * C6)))));
        return p * BitConverter.Int32BitsToSingle(((int)n + 127) << 23);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Exp(Vector256<float> x)
    {
        x = Vector256.Min(Vector256.Max(x, Vector256.Create(Lo)), Vector256.Create(Hi));
        var n = Vector256.Floor(x * Vector256.Create(Log2E) + Vector256.Create(0.5f));
        var r = x - n * Vector256.Create(Ln2Hi) - n * Vector256.Create(Ln2Lo);
        var p = Vector256.Create(C6);
        p = p * r + Vector256.Create(C5);
        p = p * r + Vector256.Create(C4);
        p = p * r + Vector256.Create(C3);
        p = p * r + Vector256.Create(C2);
        p = p * r + Vector256<float>.One;
        p = p * r + Vector256<float>.One;
        var pow = Vector256.ShiftLeft(Vector256.ConvertToInt32(n) + Vector256.Create(127), 23).AsSingle();
        return p * pow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Exp(Vector128<float> x)
    {
        x = Vector128.Min(Vector128.Max(x, Vector128.Create(Lo)), Vector128.Create(Hi));
        var n = Vector128.Floor(x * Vector128.Create(Log2E) + Vector128.Create(0.5f));
        var r = x - n * Vector128.Create(Ln2Hi) - n * Vector128.Create(Ln2Lo);
        var p = Vector128.Create(C6);
        p = p * r + Vector128.Create(C5);
        p = p * r + Vector128.Create(C4);
        p = p * r + Vector128.Create(C3);
        p = p * r + Vector128.Create(C2);
        p = p * r + Vector128<float>.One;
        p = p * r + Vector128<float>.One;
        var pow = Vector128.ShiftLeft(Vector128.ConvertToInt32(n) + Vector128.Create(127), 23).AsSingle();
        return p * pow;
    }
}
