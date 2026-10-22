using System.Numerics;

namespace Alice.Runtime;

/// <summary>Ruby Integer/Float methods beyond the basics.</summary>
public static partial class Prelude
{
    public static T gcd<T>(this T a, T b) where T : IBinaryInteger<T> { a = T.Abs(a); b = T.Abs(b); while (!T.IsZero(b)) (a, b) = (b, a % b); return a; }
    public static T lcm<T>(this T a, T b) where T : IBinaryInteger<T> => T.IsZero(a) || T.IsZero(b) ? T.Zero : T.Abs(a / a.gcd(b) * b);

    /// <summary>Base-10 digits, least significant first (Ruby `Integer#digits`).</summary>
    public static int[] digits(this int n) => n == 0 ? new[] { 0 } : Math.Abs((long)n).ToString(Inv).Reverse().Select(c => c - '0').ToArray();
    public static int[] digits(this long n) => n == 0 ? new[] { 0 } : BigInteger.Abs(n).ToString(Inv).Reverse().Select(c => c - '0').ToArray();

    /// <summary>Floored division and modulo (Ruby semantics for negatives).</summary>
    public static T[] divmod<T>(this T a, T b) where T : IBinaryInteger<T>
    {
        var q = a / b;
        var m = a % b;
        if (!T.IsZero(m) && (T.IsNegative(m) != T.IsNegative(b))) { q -= T.One; m += b; }
        return new[] { q, m };
    }

    public static T clamp<T>(this T x, T lo, T hi) where T : INumber<T> => T.Clamp(x, lo, hi);
    public static bool between_q<T>(this T x, T lo, T hi) where T : INumber<T> => x >= lo && x <= hi;

    public static void step(this int a, int limit, int by, Action<int> f)
    {
        if (by == 0) throw new ArgumentException("step can't be 0");
        for (int i = a; by > 0 ? i <= limit : i >= limit; i += by) f(i);
    }

    public static void step(this double a, double limit, double by, Action<double> f)
    {
        if (by == 0) throw new ArgumentException("step can't be 0");
        long n = (long)Math.Floor((limit - a) / by + 1e-9);
        for (long i = 0; i <= n; i++) f(a + i * by);
    }

    public static string to_s(this int n, int radix) => to_s((long)n, radix);
    public static string to_s(this long n, int radix)
    {
        if (radix is < 2 or > 36) throw new ArgumentException("invalid radix");
        if (n == 0) return "0";
        var v = BigInteger.Abs(n);
        var sb = new System.Text.StringBuilder();
        while (v > 0) { sb.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(v % radix)]); v /= radix; }
        return n < 0 ? "-" + sb : sb.ToString();
    }

    public static double cbrt(this double d) => Math.Cbrt(d);
    public static double log(this double d) => Math.Log(d);
    public static double log2(this double d) => Math.Log2(d);
    public static double log10(this double d) => Math.Log10(d);
    public static double truncate(this double d) => Math.Truncate(d);
    public static bool nan_q(this double d) => double.IsNaN(d);
    public static bool infinite_q(this double d) => double.IsInfinity(d);
    public static bool finite_q(this double d) => double.IsFinite(d);
    public static bool positive_q<T>(this T n) where T : INumber<T> => T.IsPositive(n) && !T.IsZero(n);
    public static bool negative_q<T>(this T n) where T : INumber<T> => T.IsNegative(n);
}
