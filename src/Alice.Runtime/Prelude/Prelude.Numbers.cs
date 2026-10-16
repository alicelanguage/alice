using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

public static partial class Prelude
{
    public static int pow(int b, int e) { int r = 1; while (e-- > 0) r *= b; return r; }
    public static long pow(long b, int e) { long r = 1; while (e-- > 0) r *= b; return r; }
    public static double pow(double b, double e) => Math.Pow(b, e);

    public static double rand() => Random.Shared.NextDouble();
    public static int rand(int n) => Random.Shared.Next(n);

    public static IEnumerable<int> range(int a, int b, bool exclusive)
    {
        int end = exclusive ? b : b + 1;
        for (int i = a; i < end; i++) yield return i;
    }

    public static IEnumerable<long> range(long a, long b, bool exclusive)
    {
        long end = exclusive ? b : b + 1;
        for (long i = a; i < end; i++) yield return i;
    }

    public static Dictionary<K, V> hash<K, V>(params (K, V)[] kv) where K : notnull
    {
        var d = new Dictionary<K, V>(kv.Length);
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    public static void times(this int n, Action<int> f) { for (int i = 0; i < n; i++) f(i); }
    public static void upto(this int a, int b, Action<int> f) { for (int i = a; i <= b; i++) f(i); }
    public static void downto(this int a, int b, Action<int> f) { for (int i = a; i >= b; i--) f(i); }
    public static bool even_q<T>(this T n) where T : IBinaryInteger<T> => T.IsEvenInteger(n);
    public static bool odd_q<T>(this T n) where T : IBinaryInteger<T> => T.IsOddInteger(n);
    public static bool zero_q<T>(this T n) where T : INumber<T> => T.IsZero(n);
    public static T abs<T>(this T n) where T : INumber<T> => T.Abs(n);
    public static T succ<T>(this T n) where T : INumber<T> => n + T.One;
    public static T pred<T>(this T n) where T : INumber<T> => n - T.One;
    public static int to_i(this double d) => (int)d;
    public static int to_i(this float d) => (int)d;
    public static double to_f(this float d) => d;
    public static float to_f32(this float d) => d;
    public static float to_f32(this long d) => d;
    public static int to_i(this long d) => (int)d;
    public static int to_i(this int d) => d;
    public static int to_i(this char c) => c;
    public static int to_i(this string s) => int.Parse(s.Trim(), Inv);
    public static long to_i64(this int d) => d;
    public static long to_i64(this double d) => (long)d;
    public static long to_i64(this string s) => long.Parse(s.Trim(), Inv);
    public static double to_f(this int d) => d;
    public static double to_f(this long d) => d;
    public static double to_f(this double d) => d;
    public static double to_f(this string s) => double.Parse(s.Trim(), Inv);
    public static float to_f32(this double d) => (float)d;
    public static float to_f32(this int d) => d;
    public static char chr(this int c) => (char)c;
    public static int ord(this char c) => c;
    public static double floor(this double d) => Math.Floor(d);
    public static double ceil(this double d) => Math.Ceiling(d);
    public static double round(this double d) => Math.Round(d);
    public static double sqrt(this double d) => Math.Sqrt(d);
}
