using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Alice.Runtime;

/// <summary>Ruby String methods beyond the basics.</summary>
public static partial class Prelude
{
    public static string center(this string s, int n, char pad = ' ')
    {
        if (n <= s.Length) return s;
        int left = (n - s.Length) / 2;
        return new string(pad, left) + s + new string(pad, n - s.Length - left);
    }

    public static string chomp(this string s) =>
        s.EndsWith("\r\n", StringComparison.Ordinal) ? s[..^2] : s.EndsWith('\n') || s.EndsWith('\r') ? s[..^1] : s;
    public static string chop(this string s) => s.Length == 0 ? s : s[..^1];

    public static string squeeze(this string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (sb.Length == 0 || sb[^1] != c) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Ruby `count(set)`: number of characters of `s` that occur in `set` (supports `a-z` ranges).</summary>
    public static int count(this string s, string set) { var cs = CharSet(set); return s.Count(cs.Contains); }
    public static int index(this string s, string x) => s.IndexOf(x, StringComparison.Ordinal);
    public static int rindex(this string s, string x) => s.LastIndexOf(x, StringComparison.Ordinal);

    public static string sub(this string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        return i < 0 ? s : s[..i] + b + s[(i + a.Length)..];
    }

    public static string sub(this string s, Regex re, string b) => re.Replace(s, b, 1);
    public static string gsub(this string s, Regex re, string b) => re.Replace(s, b);
    public static string[] scan(this string s, Regex re) => re.Matches(s).Select(m => m.Value).ToArray();
    public static string[] scan(this string s, string pattern) => s.scan(new Regex(pattern));
    public static bool match_q(this string s, Regex re) => re.IsMatch(s);
    public static bool match_q(this string s, string pattern) => Regex.IsMatch(s, pattern);

    /// <summary>Ruby `tr`: maps characters of `from` to `to` (last `to` char repeats; `a-z` ranges).</summary>
    public static string tr(this string s, string from, string to)
    {
        var f = Expand(from);
        var t = Expand(to);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            int i = f.IndexOf(c);
            if (i < 0) sb.Append(c);
            else if (t.Length > 0) sb.Append(t[Math.Min(i, t.Length - 1)]);
        }
        return sb.ToString();
    }

    public static string delete(this string s, string set) { var cs = CharSet(set); return string.Concat(s.Where(c => !cs.Contains(c))); }

    public static void each_line(this string s, Action<string> f)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n') { f(s[start..(i + 1)]); start = i + 1; }
        if (start < s.Length) f(s[start..]);
    }

    public static string repeat(this string s, int n) => n <= 0 ? "" : new StringBuilder(s.Length * n).Insert(0, s, n).ToString();
    public static string to_sym(this string s) => s;
    public static int[] bytes(this string s) => Encoding.UTF8.GetBytes(s).Select(b => (int)b).ToArray();
    public static int hex(this string s) => Convert.ToInt32(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, 16);
    public static int oct(this string s) => Convert.ToInt32(s, 8);

    /// <summary>Ruby `String#succ`: increments the rightmost alphanumeric with carry ("az" -> "ba", "zz" -> "aaa").</summary>
    public static string succ(this string s)
    {
        if (s.Length == 0) return s;
        var a = s.ToCharArray();
        int i = a.Length - 1;
        while (i >= 0 && !char.IsLetterOrDigit(a[i])) i--;
        if (i < 0) { a[^1]++; return new string(a); }
        while (true)
        {
            char c = a[i];
            (char next, char first, bool carry) = c switch
            {
                'z' => ('a', 'a', true),
                'Z' => ('A', 'A', true),
                '9' => ('0', '1', true),
                _ => ((char)(c + 1), c, false),
            };
            a[i] = next;
            if (!carry) return new string(a);
            int j = i - 1;
            while (j >= 0 && !char.IsLetterOrDigit(a[j])) j--;
            if (j < 0) return new string(a, 0, i) + first + new string(a, i, a.Length - i);
            i = j;
        }
    }

    public static string swapcase(this string s) =>
        string.Concat(s.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)));
    public static string title(this string s) =>
        string.Join(' ', s.Split(' ').Select(w => w.capitalize()));

    static string Expand(string set)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < set.Length; i++)
        {
            if (i + 2 < set.Length && set[i + 1] == '-')
            {
                for (char c = set[i]; c <= set[i + 2]; c++) sb.Append(c);
                i += 2;
            }
            else sb.Append(set[i]);
        }
        return sb.ToString();
    }

    static HashSet<char> CharSet(string set) => Expand(set).ToHashSet();
}
