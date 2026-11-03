using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoreRegex = Alice.Core.Text.Regex;
using CoreMatchData = Alice.Core.Text.MatchData;

namespace Alice.Runtime;

/// <summary>Runtime support for operator-level Ruby syntax: regex literals, =~, "%" formatting, "*" and "<<".</summary>
public static partial class Prelude
{
    // lazy: initialized on first use, not when a program first touches Prelude (StartupTests)
    static class RegexCacheHolder { public static readonly ConcurrentDictionary<(string, string), CoreRegex> Value = new(); }

    /// <summary>Regex literals (Ruby syntax, Alice's own engine, ADR 0036) compile once per (pattern, flags) and are
    /// reused. Flags: i, m (dot-all), x; s is a dot-all alias.</summary>
    public static CoreRegex regex(string pattern, string flags) => RegexCacheHolder.Value.GetOrAdd((pattern, flags), static k => new CoreRegex(k.Item1, k.Item2));

    // $~ is the last match of this thread, written by =~, match, scan/gsub/sub (block forms) and never by match?.
    // =~ stores only (regex, subject, start): the MatchData is built when $~ or $1 is read, so =~ does not allocate.
    sealed class LastMatch
    {
        public CoreRegex? Re;
        public string? Text;
        public int Start;
        public CoreMatchData? Data;
    }

    static class LastMatchSlot
    {
        [ThreadStatic] static LastMatch? value;
        public static LastMatch Get() => value ??= new LastMatch();
        public static LastMatch? Peek => value;
    }

    internal static void SetLastMatch(CoreRegex re, string text, int start)
    {
        var l = LastMatchSlot.Get();
        l.Re = re; l.Text = text; l.Start = start; l.Data = null;
    }

    internal static void SetLastMatch(CoreMatchData? m)
    {
        if (m == null) { ClearLastMatch(); return; }
        var l = LastMatchSlot.Get();
        l.Re = null; l.Text = null; l.Data = m;
    }

    static void ClearLastMatch()
    {
        if (LastMatchSlot.Peek is { } l) { l.Re = null; l.Text = null; l.Data = null; }
    }

    /// <summary>$~ : the last match on this thread (nil after a failed match).</summary>
    public static CoreMatchData? last_match()
    {
        var l = LastMatchSlot.Peek;
        if (l == null) return null;
        if (l.Data != null) return l.Data;
        if (l.Re == null || l.Text == null) return null;
        return l.Data = l.Re.Match(l.Text, l.Start);
    }

    /// <summary>$1 .. $9 ($~[n]), nil when there is no match or the group did not take part.</summary>
    public static string? last_match(int group) => last_match()?[group];

    /// <summary>$~[:name].</summary>
    public static string? last_match(string name) => last_match()?[name];

    /// <summary>The text before the last match.</summary>
    public static string? last_pre_match() => last_match()?.PreMatch;

    /// <summary>The text after the last match.</summary>
    public static string? last_post_match() => last_match()?.PostMatch;

    /// <summary>str =~ /re/ : index of the first match, or nil; sets $~ (without allocating).</summary>
    public static int? match_index(string? s, CoreRegex re)
    {
        if (s == null || !re.Find(s.AsSpan(), out var r)) { ClearLastMatch(); return null; }
        int start = r.Start.Value;
        SetLastMatch(re, s, start);
        return start;
    }

    /// <summary>/re/ =~ str.</summary>
    public static int? match_index(CoreRegex re, string? s) => match_index(s, re);

    /// <summary>str !~ /re/.</summary>
    public static bool no_match(string? s, CoreRegex re) => s == null || !re.IsMatch(s);

    // System.Text.RegularExpressions.Regex objects that user code builds explicitly keep .NET semantics
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

    /// <summary>Ruby `obj.tap { |o| ... }`: runs the block on the receiver and returns the receiver (builder style).</summary>
    public static T tap<T>(this T self, Action<T> f) { f(self); return self; }

    /// <summary>Ruby `obj.then { |o| ... }`: the block's value.</summary>
    public static R then<T, R>(this T self, Func<T, R> f) => f(self);

    public static string str_mul(string s, int n) => n <= 0 ? "" : new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    /// <summary>`xs << v` appends and returns the receiver, so appends chain (`xs << 1 << 2`).</summary>
    public static List<T> append_op<T>(List<T> xs, T v) { xs.Add(v); return xs; }
    public static StringBuilder append_op<T>(StringBuilder sb, T v) => sb.Append(str(v));

    public static T[] arr_plus<T>(IEnumerable<T> a, IEnumerable<T> b) => a.Concat(b).ToArray();
    public static T[] arr_minus<T>(IEnumerable<T> a, IEnumerable<T> b) { var s = new HashSet<T>(b); return a.Where(x => !s.Contains(x)).ToArray(); }
    public static T[] arr_and<T>(IEnumerable<T> a, IEnumerable<T> b) => a.Intersect(b).ToArray();
    public static T[] arr_or<T>(IEnumerable<T> a, IEnumerable<T> b) => a.Union(b).ToArray();
}
