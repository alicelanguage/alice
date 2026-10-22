using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Numeric primitives with exact, documented semantics (docs/language.md "Numbers"):
/// floored `/` and `%`, checked `to_X` / wrapping `to_X!` conversions, bit counts, half-away-from-zero
/// rounding on the printed digits, and shortest round-trip float formatting.
/// </summary>
public static partial class Prelude
{
    // ---- integer power: exponentiation by squaring, overflow-checked ----

    public static int pow(int b, int e) => IPow(b, e);
    public static long pow(long b, int e) => IPow(b, e);
    public static long pow(long b, long e) => IPow(b, checked((int)e));

    static T IPow<T>(T b, int e) where T : IBinaryInteger<T>
    {
        if (e < 0) throw new ArgumentOutOfRangeException(nameof(e), e, "negative exponent for an integer power; use a Float64 base (2.0 ** -1)");
        T r = T.One;
        while (true)
        {
            if ((e & 1) != 0) r = checked(r * b);
            e >>= 1;
            if (e == 0) return r;
            b = checked(b * b);
        }
    }

    // ---- floored division (the sign of `%` follows the divisor, like Ruby and divmod) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int floor_div(int a, int b) { int q = a / b; if ((a % b != 0) && ((a ^ b) < 0)) q--; return q; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long floor_div(long a, long b) { long q = a / b; if ((a % b != 0) && ((a ^ b) < 0)) q--; return q; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int floor_mod(int a, int b) { int r = a % b; if (r != 0 && ((r ^ b) < 0)) r += b; return r; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long floor_mod(long a, long b) { long r = a % b; if (r != 0 && ((r ^ b) < 0)) r += b; return r; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double floor_fmod(double a, double b) { double r = a % b; if (r != 0 && (r < 0) != (b < 0)) r += b; return r; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float floor_fmod(float a, float b) { float r = a % b; if (r != 0 && (r < 0) != (b < 0)) r += b; return r; }

    /// <summary>Truncating division (C#'s `/`): -7.tdiv(2) == -3.</summary>
    public static T tdiv<T>(this T a, T b) where T : IBinaryInteger<T> => a / b;

    /// <summary>Truncating remainder (C#'s `%`, sign of the dividend): -7.remainder(3) == -1.</summary>
    public static T remainder<T>(this T a, T b) where T : INumber<T> => a % b;

    public static double fdiv<T>(this T a, double b) where T : INumber<T> => double.CreateChecked(a) / b;

    public static int sign<T>(this T v) where T : INumber<T> => T.Sign(v);

    // ---- bits ----

    public static int popcount<T>(this T v) where T : IBinaryInteger<T> => int.CreateTruncating(T.PopCount(v));
    public static int trailing_zeros_count<T>(this T v) where T : IBinaryInteger<T> => int.CreateTruncating(T.TrailingZeroCount(v));
    public static int leading_zeros_count<T>(this T v) where T : IBinaryInteger<T> => int.CreateTruncating(T.LeadingZeroCount(v));

    /// <summary>Bits needed to represent the value without its sign (Ruby's Integer#bit_length).</summary>
    public static int bit_length<T>(this T v) where T : IBinaryInteger<T>
    {
        if (T.IsNegative(v)) v = ~v;
        return int.CreateTruncating(T.LeadingZeroCount(T.Zero) - T.LeadingZeroCount(v));
    }

    // ---- conversions: `to_X` raises OverflowException out of range (NaN too); `to_X!` wraps (floats saturate) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int to_i<T>(this T v) where T : INumber<T> => int.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte to_i8<T>(this T v) where T : INumber<T> => sbyte.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short to_i16<T>(this T v) where T : INumber<T> => short.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int to_i32<T>(this T v) where T : INumber<T> => int.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long to_i64<T>(this T v) where T : INumber<T> => long.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte to_u8<T>(this T v) where T : INumber<T> => byte.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort to_u16<T>(this T v) where T : INumber<T> => ushort.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint to_u32<T>(this T v) where T : INumber<T> => uint.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong to_u64<T>(this T v) where T : INumber<T> => ulong.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double to_f<T>(this T v) where T : INumber<T> => double.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double to_f64<T>(this T v) where T : INumber<T> => double.CreateChecked(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float to_f32<T>(this T v) where T : INumber<T> => float.CreateChecked(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int to_i_b<T>(this T v) where T : INumber<T> => int.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte to_i8_b<T>(this T v) where T : INumber<T> => sbyte.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short to_i16_b<T>(this T v) where T : INumber<T> => short.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int to_i32_b<T>(this T v) where T : INumber<T> => int.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long to_i64_b<T>(this T v) where T : INumber<T> => long.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte to_u8_b<T>(this T v) where T : INumber<T> => byte.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort to_u16_b<T>(this T v) where T : INumber<T> => ushort.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint to_u32_b<T>(this T v) where T : INumber<T> => uint.CreateTruncating(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong to_u64_b<T>(this T v) where T : INumber<T> => ulong.CreateTruncating(v);

    // ---- rounding: half away from zero; with digits, on the shortest printed form (0.29.floor(2) == 0.29) ----

    public static double round(this double d) => Math.Round(d, MidpointRounding.AwayFromZero);
    public static float round(this float d) => MathF.Round(d, MidpointRounding.AwayFromZero);
    public static double round(this double d, int n) => DigitRound(d, n, MidpointRounding.AwayFromZero);
    public static double floor(this double d, int n) => DigitRound(d, n, MidpointRounding.ToNegativeInfinity);
    public static double ceil(this double d, int n) => DigitRound(d, n, MidpointRounding.ToPositiveInfinity);

    static double DigitRound(double d, int n, MidpointRounding mode)
    {
        if (!double.IsFinite(d) || n > 15) return d;   // a double has no digits past ~15-17
        if (n >= 0 && Math.Abs(d) < 7.9e27)
        {
            // the shortest round-trip text is what the user wrote and sees: round those digits, not the binary expansion
            Span<char> text = stackalloc char[32];   // shortest round-trip form fits in 25 chars: no string allocation
            d.TryFormat(text, out int len, "R", Inv);
            decimal m = decimal.Parse(text[..len], NumberStyles.Float, Inv);
            double r = (double)decimal.Round(m, n, mode);
            return r == 0 && double.IsNegative(d) ? -0.0 : r;
        }
        double f = Math.Pow(10, n);
        return Math.Round(d * f, mode) / f;
    }

    // ---- formatting: shortest round-trip digits, always with a decimal point, Ruby's exponent thresholds ----

    public static string str(float v) => FormatFloat(v.ToString("R", Inv), float.IsNaN(v), float.IsInfinity(v));
    public static string to_s(this double v) => str(v);
    public static string to_s(this float v) => str(v);

    static string FormatFloat(string r, bool nan, bool inf)
    {
        if (nan) return "NaN";
        if (inf) return r.StartsWith('-') ? "-Infinity" : "Infinity";
        bool neg = r.StartsWith('-');
        if (neg) r = r[1..];
        int e = 0, ei = r.IndexOfAny(['E', 'e']);
        if (ei >= 0) { e = int.Parse(r[(ei + 1)..], Inv); r = r[..ei]; }
        int dot = r.IndexOf('.');
        string digits = dot < 0 ? r : r[..dot] + r[(dot + 1)..];
        int point = (dot < 0 ? r.Length : dot) + e;   // decimal point position within `digits`
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..].TrimEnd('0');
        point -= lead;
        string sign = neg ? "-" : "";
        if (digits.Length == 0) return sign + "0.0";
        int x = point - 1;   // scientific exponent: d.ddd × 10^x
        if (x < -4 || x >= 16)
            return sign + digits[0] + "." + (digits.Length > 1 ? digits[1..] : "0") + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("00", Inv);
        if (point <= 0) return sign + "0." + new string('0', -point) + digits;
        if (point >= digits.Length) return sign + digits + new string('0', point - digits.Length) + ".0";
        return sign + digits[..point] + "." + digits[point..];
    }

    /// <summary>Column of a flattened row (`points[i][j]`): j must be in [0, width), as in the jagged array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int column(int j, int width) => (uint)j < (uint)width ? j : throw new IndexOutOfRangeException($"column {j} outside a row of {width}");
}
