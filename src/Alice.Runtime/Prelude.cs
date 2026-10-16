using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

/// <summary>
/// Ruby/Crystal-flavoured standard library for Alice programs. Generated code does
/// <c>using static Alice.Runtime.Prelude;</c>, so these read as builtins / methods.
/// Names ending in <c>_q</c> are Alice <c>name?</c> predicates.
/// </summary>
public static class Prelude
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Alice programs are culture-invariant (like Crystal/Ruby).</summary>
    public static void init()
    {
        CultureInfo.DefaultThreadCurrentCulture = Inv;
        CultureInfo.CurrentCulture = Inv;
    }

    // ---- formatting -------------------------------------------------------
    public static string str(string? s) => s ?? "";
    public static string str(bool b) => b ? "true" : "false";
    public static string str(char c) => c.ToString();
    public static string str(int v) => v.ToString(Inv);
    public static string str(long v) => v.ToString(Inv);
    public static string str(double v) => v.ToString(Inv);
    public static string str<T>(T v) => v switch
    {
        null => "",
        string s => s,
        bool b => str(b),
        IFormattable f => f.ToString(null, Inv),
        IEnumerable e => "[" + string.Join(", ", Items(e)) + "]",
        _ => v.ToString() ?? "",
    };

    public static string inspect<T>(this T v) => v switch
    {
        null => "nil",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
        char c => "'" + c + "'",
        IEnumerable e => "[" + string.Join(", ", Items(e)) + "]",
        _ => str(v),
    };

    static IEnumerable<string> Items(IEnumerable e) { foreach (var x in e) yield return inspect(x); }

    // ---- io -----------------------------------------------------------------
    public static void puts() => Console.Out.WriteLine();
    public static void puts(string? s) => Console.Out.WriteLine(s);
    public static void puts<T>(T v) => Console.Out.WriteLine(str(v));
    public static void puts(params object?[] vs) { foreach (var v in vs) Console.Out.WriteLine(str(v)); }
    public static void print() { }
    public static void print(string? s) => Console.Out.Write(s);
    public static void print<T>(T v) => Console.Out.Write(str(v));
    public static void print(params object?[] vs) { foreach (var v in vs) Console.Out.Write(str(v)); }
    public static T p<T>(T v) { Console.Out.WriteLine(inspect(v)); return v; }
    public static string? gets() => Console.In.ReadLine();
    public static void exit(int code = 0) => Environment.Exit(code);

    public static Exception raise(string msg) => new(msg);
    public static Exception raise(Exception e) => e;

    // ---- concurrency ----------------------------------------------------------
    public static Task spawn(Action f) => Task.Run(f);
    public static Task<T> spawn<T>(Func<T> f) => Task.Run(f);
    public static void wait(this Task t) => t.GetAwaiter().GetResult();
    public static T wait<T>(this Task<T> t) => t.GetAwaiter().GetResult();
    public static R[] par_map<T, R>(this T[] a, Func<T, R> f) => Par.Map(a, f);
    public static void par_each(this int n, Action<int> f) => Par.For(n, f);

    // ---- numbers ------------------------------------------------------------
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

    // ---- strings ------------------------------------------------------------
    public static int size(this string s) => s.Length;
    public static int length(this string s) => s.Length;
    public static bool empty_q(this string s) => s.Length == 0;
    public static char[] chars(this string s) => s.ToCharArray();
    public static string upcase(this string s) => s.ToUpperInvariant();
    public static string downcase(this string s) => s.ToLowerInvariant();
    public static string capitalize(this string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    public static string strip(this string s) => s.Trim();
    public static string[] lines(this string s) => s.Split('\n');
    public static string reverse(this string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }
    public static bool includes_q(this string s, string x) => s.Contains(x, StringComparison.Ordinal);
    public static bool includes_q(this string s, char x) => s.Contains(x);
    public static bool starts_with_q(this string s, string x) => s.StartsWith(x, StringComparison.Ordinal);
    public static bool ends_with_q(this string s, string x) => s.EndsWith(x, StringComparison.Ordinal);
    public static string gsub(this string s, string a, string b) => s.Replace(a, b, StringComparison.Ordinal);
    public static string[] split(this string s, string sep = " ") => s.Split(sep, StringSplitOptions.RemoveEmptyEntries);
    public static void each_char(this string s, Action<char> f) { foreach (var c in s) f(c); }
    public static string ljust(this string s, int n, char pad = ' ') => s.PadRight(n, pad);
    public static string rjust(this string s, int n, char pad = ' ') => s.PadLeft(n, pad);

    // ---- collections ----------------------------------------------------------
    public static int size<T>(this T[] a) => a.Length;
    public static int size<T>(this List<T> a) => a.Count;
    public static int size<K, V>(this Dictionary<K, V> a) where K : notnull => a.Count;
    public static int size<T>(this IEnumerable<T> a) => a.Count();
    public static int length<T>(this T[] a) => a.Length;
    public static int length<T>(this IEnumerable<T> a) => a.Count();
    public static int count<T>(this IEnumerable<T> a) => a.Count();
    public static int count<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Count(f);
    public static bool empty_q<T>(this IEnumerable<T> a) => !a.Any();
    public static bool any_q<T>(this IEnumerable<T> a) => a.Any();
    public static bool any_q<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Any(f);
    public static bool all_q<T>(this IEnumerable<T> a, Func<T, bool> f) => a.All(f);
    public static bool includes_q<T>(this IEnumerable<T> a, T x) => a.Contains(x);
    public static T first<T>(this IEnumerable<T> a) => a.First();
    public static T last<T>(this IEnumerable<T> a) => a.Last();
    public static T? find<T>(this IEnumerable<T> a, Func<T, bool> f) => a.FirstOrDefault(f);
    public static int index<T>(this IList<T> a, T x) => a.IndexOf(x);

    public static void each<T>(this T[] a, Action<T> f) { for (int i = 0; i < a.Length; i++) f(a[i]); }
    public static void each<T>(this IEnumerable<T> a, Action<T> f) { foreach (var x in a) f(x); }
    public static void each<K, V>(this Dictionary<K, V> a, Action<K, V> f) where K : notnull { foreach (var kv in a) f(kv.Key, kv.Value); }
    public static void each_with_index<T>(this IEnumerable<T> a, Action<T, int> f) { int i = 0; foreach (var x in a) f(x, i++); }

    public static R[] map<T, R>(this T[] a, Func<T, R> f)
    {
        var r = new R[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = f(a[i]);
        return r;
    }

    public static R[] map<T, R>(this IEnumerable<T> a, Func<T, R> f) => a.Select(f).ToArray();
    public static R[] map_with_index<T, R>(this IEnumerable<T> a, Func<T, int, R> f) => a.Select(f).ToArray();
    public static T[] select<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Where(f).ToArray();
    public static T[] reject<T>(this IEnumerable<T> a, Func<T, bool> f) => a.Where(x => !f(x)).ToArray();
    public static R reduce<T, R>(this IEnumerable<T> a, R seed, Func<R, T, R> f) => a.Aggregate(seed, f);
    public static T reduce<T>(this IEnumerable<T> a, Func<T, T, T> f) => a.Aggregate(f);
    public static T sum<T>(this T[] a) where T : unmanaged, INumber<T> => Simd.Sum<T>(a);
    public static T sum<T>(this IEnumerable<T> a) where T : INumber<T> { T s = T.Zero; foreach (var x in a) s += x; return s; }
    public static T max<T>(this T[] a) where T : unmanaged, INumber<T> => Simd.Max<T>(a);
    public static T max<T>(this IEnumerable<T> a) => a.Max()!;
    public static T min<T>(this T[] a) where T : unmanaged, INumber<T> => Simd.Min<T>(a);
    public static T min<T>(this IEnumerable<T> a) => a.Min()!;
    public static T max_by<T, K>(this IEnumerable<T> a, Func<T, K> f) => a.MaxBy(f)!;
    public static T min_by<T, K>(this IEnumerable<T> a, Func<T, K> f) => a.MinBy(f)!;
    public static T[] sort<T>(this IEnumerable<T> a) { var r = a.ToArray(); Array.Sort(r); return r; }
    public static T[] sort_by<T, K>(this IEnumerable<T> a, Func<T, K> f) => a.OrderBy(f).ToArray();
    public static T[] reverse<T>(this IEnumerable<T> a) { var r = a.ToArray(); Array.Reverse(r); return r; }
    public static T[] uniq<T>(this IEnumerable<T> a) => a.Distinct().ToArray();
    public static T[] to_a<T>(this IEnumerable<T> a) => a.ToArray();
    public static List<T> to_list<T>(this IEnumerable<T> a) => a.ToList();
    public static T[] take<T>(this IEnumerable<T> a, int n) => a.Take(n).ToArray();
    public static T[] drop<T>(this IEnumerable<T> a, int n) => a.Skip(n).ToArray();
    public static string join<T>(this IEnumerable<T> a, string sep = "") => string.Join(sep, a.Select(x => str(x)));
    public static void push<T>(this List<T> a, T x) => a.Add(x);
    public static T pop<T>(this List<T> a) { var x = a[^1]; a.RemoveAt(a.Count - 1); return x; }
    public static K[] keys<K, V>(this Dictionary<K, V> d) where K : notnull => d.Keys.ToArray();
    public static V[] values<K, V>(this Dictionary<K, V> d) where K : notnull => d.Values.ToArray();
    public static bool has_key_q<K, V>(this Dictionary<K, V> d, K k) where K : notnull => d.ContainsKey(k);

    // ---- builders -------------------------------------------------------------
    public static StringBuilder append(this StringBuilder sb, string s) => sb.Append(s);
}
