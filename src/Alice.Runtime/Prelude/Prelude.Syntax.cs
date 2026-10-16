using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Alice.Runtime;

/// <summary>Runtime support for operator-level Ruby syntax: regex literals, =~, "%" formatting, "*" and "<<".</summary>
public static partial class Prelude
{
    static readonly ConcurrentDictionary<(string, string), Regex> RegexCache = new();

    /// <summary>Regex literals compile once per (pattern, flags) and are reused.</summary>
    public static Regex regex(string pattern, string flags) => RegexCache.GetOrAdd((pattern, flags), static k =>
    {
        var o = RegexOptions.CultureInvariant;
        foreach (var f in k.Item2)
            o |= f switch { 'i' => RegexOptions.IgnoreCase, 'm' => RegexOptions.Multiline, 'x' => RegexOptions.IgnorePatternWhitespace, 's' => RegexOptions.Singleline, _ => 0 };
        return new Regex(k.Item1, o);
    });

    /// <summary>`str =~ /re/`: index of the first match, or nil.</summary>
    public static int? match_index(string? s, Regex re)
    {
        if (s == null) return null;
        var m = re.Match(s);
        return m.Success ? m.Index : null;
    }

    public static bool no_match(string? s, Regex re) => s == null || !re.IsMatch(s);

    /// <summary>`"%05d %s" % [a, b]` (or a single value): C-style formatting, invariant culture.</summary>
    public static string sprintf<T>(string fmt, T args)
    {
        object?[] vals = args is System.Collections.IEnumerable e and not string ? e.Cast<object?>().ToArray() : [args];
        var sb = new StringBuilder();
        int vi = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
            if (fmt[i + 1] == '%') { sb.Append('%'); i++; continue; }
            int j = i + 1;
            string flags = "";
            while (j < fmt.Length && "-0+ ".Contains(fmt[j])) flags += fmt[j++];
            int width = 0;
            while (j < fmt.Length && char.IsDigit(fmt[j])) width = width * 10 + (fmt[j++] - '0');
            int prec = -1;
            if (j < fmt.Length && fmt[j] == '.') { j++; prec = 0; while (j < fmt.Length && char.IsDigit(fmt[j])) prec = prec * 10 + (fmt[j++] - '0'); }
            if (j >= fmt.Length) { sb.Append(fmt, i, fmt.Length - i); break; }
            char conv = fmt[j];
            object? v = vi < vals.Length ? vals[vi++] : null;
            string body = conv switch
            {
                'd' or 'i' => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                'f' => Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("F" + (prec < 0 ? 6 : prec), CultureInfo.InvariantCulture),
                'e' => Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString((prec < 0 ? "e6" : "e" + prec), CultureInfo.InvariantCulture),
                'x' => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("x"),
                'X' => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("X"),
                'o' => Convert.ToString(Convert.ToInt64(v, CultureInfo.InvariantCulture), 8),
                'b' => Convert.ToString(Convert.ToInt64(v, CultureInfo.InvariantCulture), 2),
                'c' => v is char ch ? ch.ToString() : ((char)Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToString(),
                _ => str(v),
            };
            if (prec >= 0 && conv == 's' && body.Length > prec) body = body[..prec];
            if (flags.Contains('+') && conv is 'd' or 'i' or 'f' && !body.StartsWith('-')) body = "+" + body;
            if (body.Length < width)
            {
                if (flags.Contains('-')) body = body.PadRight(width);
                else if (flags.Contains('0') && conv is not ('s' or 'c'))
                {
                    bool neg = body.StartsWith('-') || body.StartsWith('+');
                    body = neg ? body[0] + body[1..].PadLeft(width - 1, '0') : body.PadLeft(width, '0');
                }
                else body = body.PadLeft(width);
            }
            sb.Append(body);
            i = j;
        }
        return sb.ToString();
    }

    public static string str_mul(string s, int n) => n <= 0 ? "" : new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    /// <summary>`xs << v` appends and returns the receiver, so appends chain (`xs << 1 << 2`).</summary>
    public static List<T> append_op<T>(List<T> xs, T v) { xs.Add(v); return xs; }
    public static StringBuilder append_op<T>(StringBuilder sb, T v) => sb.Append(str(v));

    public static T[] arr_plus<T>(IEnumerable<T> a, IEnumerable<T> b) => a.Concat(b).ToArray();
    public static T[] arr_minus<T>(IEnumerable<T> a, IEnumerable<T> b) { var s = new HashSet<T>(b); return a.Where(x => !s.Contains(x)).ToArray(); }
    public static T[] arr_and<T>(IEnumerable<T> a, IEnumerable<T> b) => a.Intersect(b).ToArray();
    public static T[] arr_or<T>(IEnumerable<T> a, IEnumerable<T> b) => a.Union(b).ToArray();
}
