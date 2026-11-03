using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoreRegex = Alice.Core.Text.Regex;
using CoreMatchData = Alice.Core.Text.MatchData;

namespace Alice.Runtime;

/// <summary>Ruby/Crystal String and Char methods beyond the basics.</summary>
public static partial class Prelude
{
    [Pure] public static string lstrip(this string s) => s.TrimStart();
    [Pure] public static string rstrip(this string s) => s.TrimEnd();

    /// <summary>Bytes of the UTF-8 encoding (Ruby `bytesize`).</summary>
    [Pure] public static int bytesize(this string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>Unicode code points (surrogate pairs are one code point).</summary>
    [Pure]
    public static int[] codepoints(this string s)
    {
        var r = new List<int>(s.Length);
        foreach (var rune in s.EnumerateRunes()) r.Add(rune.Value);
        return r.ToArray();
    }

    /// <summary>Empty or only whitespace (Crystal `blank?`).</summary>
    [Pure] public static bool blank_q(this string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>`"foo_bar".camelcase` -> "FooBar" (Crystal); `lower: true` -> "fooBar".</summary>
    [Pure]
    public static string camelcase(this string s, bool lower = false)
    {
        var sb = new StringBuilder(s.Length);
        bool up = !lower;
        foreach (var c in s)
        {
            if (c == '_') { up = true; continue; }
            sb.Append(up ? char.ToUpperInvariant(c) : c);
            up = false;
        }
        if (lower && sb.Length > 0) sb[0] = char.ToLowerInvariant(sb[0]);
        return sb.ToString();
    }

    /// <summary>`"FooBar".underscore` -> "foo_bar", `"HTTPServer"` -> "http_server" (Crystal).</summary>
    [Pure]
    public static string underscore(this string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsUpper(c))
            {
                bool prevLowerOrDigit = i > 0 && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]));
                bool acronymEnd = i > 0 && char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                if (prevLowerOrDigit || acronymEnd) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c == '-' ? '_' : c);
        }
        return sb.ToString();
    }

    /// <summary>`"ff".to_i(16)`: parses in base 2..36 (optional sign, `_` separators); raises on bad digits like `to_i`.</summary>
    [Pure] public static int to_i(this string s, int @base) => ParseBase(s, @base) ?? throw new FormatException($"invalid Int32 in base {@base}: {inspect(s)}");

    /// <summary>`to_i?`: nil instead of raising (Crystal). Base 10 unless given.</summary>
    [Pure] public static int? to_i_q(this string s, int @base = 10) => ParseBase(s, @base);

    /// <summary>`to_f?`: nil instead of raising.</summary>
    [Pure] public static double? to_f_q(this string s) => double.TryParse(s.Trim(), NumberStyles.Float, Inv, out var d) ? d : null;

    static int? ParseBase(string s, int b)
    {
        if (b is < 2 or > 36) throw new ArgumentException("invalid base " + b);
        var t = s.AsSpan().Trim();
        bool neg = false;
        if (t.Length > 0 && (t[0] == '-' || t[0] == '+')) { neg = t[0] == '-'; t = t[1..]; }
        if (t.Length == 0) return null;
        long v = 0;
        bool digit = false;
        foreach (var c in t)
        {
            if (c == '_' && digit) continue;
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 99;
            if (d >= b) return null;
            v = v * b + d;
            if (v > (long)int.MaxValue + 1) return null;
            digit = true;
        }
        v = neg ? -v : v;
        return v is < int.MinValue or > int.MaxValue ? null : (int)v;
    }

    /// <summary>`each_char` without a block: the characters as an enumerable (lazy; `.to_a`, `.first`, `.with_index`...).</summary>
    public static IEnumerable<char> each_char(this string s) => s;

    /// <summary>`String.build { |io| io << "a" << 1 }`: the builder's text.</summary>
    public static string string_build(Action<StringBuilder> f) { var sb = new StringBuilder(); f(sb); return sb.ToString(); }

    // padding with a String (Ruby cycles the pad: "ab".ljust(6, "12") == "ab1212")
    [Pure] public static string ljust(this string s, int n, string pad) => n <= s.Length ? s : s + Pad(pad, n - s.Length);
    [Pure] public static string rjust(this string s, int n, string pad) => n <= s.Length ? s : Pad(pad, n - s.Length) + s;
    [Pure]
    public static string center(this string s, int n, string pad)
    {
        if (n <= s.Length) return s;
        int left = (n - s.Length) / 2;
        return Pad(pad, left) + s + Pad(pad, n - s.Length - left);
    }

    static string Pad(string pad, int n)
    {
        if (pad.Length == 0) throw new ArgumentException("zero width padding");
        return string.Create(n, pad, static (span, p) => { for (int i = 0; i < span.Length; i++) span[i] = p[i % p.Length]; });
    }

    /// <summary>s.match(/re/): the first match as MatchData, or nil; sets $~.</summary>
    public static CoreMatchData? match(this string s, CoreRegex re)
    {
        var m = re.Match(s);
        SetLastMatch(m);
        return m;
    }

    /// <summary>s.match(/re/, pos): the first match at or after pos (negative: from the end).</summary>
    public static CoreMatchData? match(this string s, CoreRegex re, int pos)
    {
        var m = re.Match(s, pos < 0 ? Math.Max(0, s.Length + pos) : pos);
        SetLastMatch(m);
        return m;
    }

    /// <summary>gsub(/re/) { |m| ... } and sub(/re/) { |m| ... }: each match replaced by the block's value; $~ is the
    /// current match inside the block.</summary>
    public static string gsub(this string s, CoreRegex re, Func<string, string> f) => ReplaceBlock(s, re, f, -1);
    public static string sub(this string s, CoreRegex re, Func<string, string> f) => ReplaceBlock(s, re, f, 1);

    static int ScalarWidth(string s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;

    static string ReplaceBlock(string s, CoreRegex re, Func<string, string> f, int count)
    {
        StringBuilder? sb = null;
        int pos = 0, last = 0, done = 0;
        while ((count < 0 || done < count) && pos <= s.Length && re.Find(s.AsSpan(), pos, out var r))
        {
            int ms = r.Start.Value, me = r.End.Value;
            sb ??= new StringBuilder(s.Length + 16);
            sb.Append(s, last, ms - last);
            SetLastMatch(re, s, ms);
            sb.Append(f(s[ms..me]));
            last = me;
            done++;
            if (me == ms)
            {
                if (me >= s.Length) break;
                int w = ScalarWidth(s, me);
                sb.Append(s, me, w);
                last = pos = me + w;
            }
            else pos = me;
        }
        if (sb == null) return s;
        sb.Append(s, last, s.Length - last);
        return sb.ToString();
    }

    /// <summary>gsub(/re/, hash): each match replaced by its hash value (missing: removed, as in Ruby).</summary>
    public static string gsub(this string s, CoreRegex re, Dictionary<string, string> h) => ReplaceBlock(s, re, m => h.TryGetValue(m, out var r) ? r : "", -1);

    /// <summary>scan(/re/) { |m| ... }: the block gets each whole match and $~ is the current match. The only
    /// allocations are the match strings it yields.</summary>
    public static void scan(this string s, CoreRegex re, Action<string> f)
    {
        int pos = 0;
        while (pos <= s.Length && re.Find(s.AsSpan(), pos, out var r))
        {
            int ms = r.Start.Value, me = r.End.Value;
            SetLastMatch(re, s, ms);
            f(s[ms..me]);
            if (me > ms) pos = me;
            else if (me >= s.Length) break;
            else pos = me + ScalarWidth(s, me);
        }
    }

    /// <summary>Ruby scan for a pattern with groups: one array of group texts per match.</summary>
    public static string?[][] scan_groups(this string s, CoreRegex re)
    {
        var all = re.MatchAll(s);
        if (all.Count > 0) SetLastMatch(all[^1]);
        return all.Select(m => re.GroupCount == 0 ? new[] { m[0] } : m.Captures).ToArray();
    }

    /// <summary>Ruby split(/re/, limit = 0): captured groups are kept, trailing empty fields dropped (limit 0).</summary>
    public static string[] split(this string s, CoreRegex re, int limit = 0) => re.Split(s, limit);

    /// <summary>s[/re/] : the first match's text, or nil; s[/re/, n] : group n. Sets $~.</summary>
    public static string? slice(this string s, CoreRegex re, int group = 0)
    {
        var m = re.Match(s);
        SetLastMatch(m);
        return m?[group];
    }

    // System.Text.RegularExpressions.Regex objects that user code builds explicitly keep .NET semantics; their
    // matches surface as the same MatchData
    public static CoreMatchData? match(this string s, Regex re) => FromBcl(s, re.Match(s));
    public static CoreMatchData? match(this Regex re, string s) => FromBcl(s, re.Match(s));
    public static string gsub(this string s, Regex re, Func<string, string> f) => re.Replace(s, m => f(m.Value));
    public static string sub(this string s, Regex re, Func<string, string> f) => re.Replace(s, m => f(m.Value), 1);
    public static string gsub(this string s, Regex re, Dictionary<string, string> h) => re.Replace(s, m => h.TryGetValue(m.Value, out var r) ? r : "");

    static CoreMatchData? FromBcl(string s, Match m)
    {
        if (!m.Success) return null;
        var offs = new int[2 * m.Groups.Count];
        var names = new string?[m.Groups.Count];
        for (int g = 0; g < m.Groups.Count; g++)
        {
            var gr = m.Groups[g];
            offs[2 * g] = gr.Success ? gr.Index : -1;
            offs[2 * g + 1] = gr.Success ? gr.Index + gr.Length : -1;
            names[g] = g > 0 && !int.TryParse(gr.Name, out _) ? gr.Name : null;
        }
        return CoreMatchData.FromOffsets(s, offs, names);
    }

    [Pure] public static string dup(this string s) => s;   // strings are immutable: the copy is the string
    [Pure] public static string clone(this string s) => s;
    [Pure] public static int hash(this string s) => s.GetHashCode();
    [Pure] public static bool eql_q(this string a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
    [Pure] public static bool equal_q(this string a, string? b) => ReferenceEquals(a, b);

    // ---- Char ----

    [Pure] public static char upcase(this char c) => char.ToUpperInvariant(c);
    [Pure] public static char downcase(this char c) => char.ToLowerInvariant(c);
    [Pure] public static bool letter_q(this char c) => char.IsLetter(c);
    [Pure] public static bool digit_q(this char c) => char.IsAsciiDigit(c);
    [Pure] public static bool number_q(this char c) => char.IsNumber(c);
    [Pure] public static bool alphanumeric_q(this char c) => char.IsLetterOrDigit(c);
    [Pure] public static bool whitespace_q(this char c) => char.IsWhiteSpace(c);
    [Pure] public static bool uppercase_q(this char c) => char.IsUpper(c);
    [Pure] public static bool lowercase_q(this char c) => char.IsLower(c);
    [Pure] public static bool ascii_q(this char c) => char.IsAscii(c);
    [Pure] public static bool ascii_letter_q(this char c) => char.IsAsciiLetter(c);
    [Pure] public static bool hex_q(this char c) => char.IsAsciiHexDigit(c);
}
