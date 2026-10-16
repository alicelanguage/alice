using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

public static partial class Prelude
{
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
    public static void each_char_with_index(this string s, Action<char, int> f) { for (int i = 0; i < s.Length; i++) f(s[i], i); }
    public static string ljust(this string s, int n, char pad = ' ') => s.PadRight(n, pad);
    public static string rjust(this string s, int n, char pad = ' ') => s.PadLeft(n, pad);
}
