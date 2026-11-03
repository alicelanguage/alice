using System.Globalization;
using System.Text;
using CoreRegex = Alice.Core.Text.Regex;

namespace Alice.Tests.Runtime;

/// <summary>
/// The vectorized search paths of the Alice regex engine (ADR 0036) on long haystacks, where the random parity
/// inputs are too short to reach them: two-offset literal scans (bytes, UTF-16 lanes and UTF-16 packed to bytes),
/// ASCII case-insensitive scans (with the non-ASCII case variants ſ, Kelvin K and the ı that OrdinalIgnoreCase
/// confuses with i), start-state acceleration of the lazy DFA, and end-only counting. Each pattern is checked against
/// a reference written with plain string operations, on UTF-16 and UTF-8, from every start offset near the end.
/// </summary>
public class RegexScanTests
{
    static readonly string[] Words =
    [
        "the", "and", "Sherlock", "SHERLOCK", "sherlocK", "ſherlock", "sherlocK", "Shylock", "Holmes",
        "holmeſ", "HOLMES", "Hxlmes", "AKIA", "ASIA", "AKIAIOSFODNN7EXAMPLE", "ASIAXYZ0123456789AB", "bit", "BIT",
        "bıt", "BİT", "Łódź", "中文", "Émile", "\U0001D400bc", "Watson", "watson",
        "12:30", "9:05", "1:2", "back", "check", "lock", "Mr.", "Irene", "Анна", "x�y",
        "ÿĀ", "a", "I",
    ];

    static readonly string Text = Build(20_000);

    static string Build(int words)
    {
        var r = new Random(7);
        var sb = new StringBuilder();
        for (int i = 0; i < words; i++)
        {
            sb.Append(Words[r.Next(Words.Length)]);
            sb.Append(r.Next(9) == 0 ? '\n' : r.Next(5) == 0 ? ", " : " ");
        }
        return sb.ToString();
    }

    // leftmost-first literal alternation
    static List<(int, int)> RefLiterals(string s, params string[] alts)
    {
        var o = new List<(int, int)>();
        for (int p = 0; p < s.Length;)
        {
            string? hit = alts.FirstOrDefault(a => s.AsSpan(p).StartsWith(a, StringComparison.Ordinal));
            if (hit != null) { o.Add((p, p + hit.Length)); p += hit.Length; } else p++;
        }
        return o;
    }

    // Unicode simple case folding of an ASCII literal: s also matches ſ, k the Kelvin sign; ı and İ match nothing
    static bool FoldEq(char c, char l)
    {
        char lo = char.ToLowerInvariant(l);
        if (c < 0x80) return char.ToLowerInvariant(c) == lo;
        return (lo == 's' && c == 'ſ') || (lo == 'k' && c == 'K');
    }

    static List<(int, int)> RefCaseless(string s, string lit)
    {
        var o = new List<(int, int)>();
        for (int p = 0; p + lit.Length <= s.Length;)
        {
            bool ok = true;
            for (int k = 0; k < lit.Length && ok; k++) ok = FoldEq(s[p + k], lit[k]);
            if (ok) { o.Add((p, p + lit.Length)); p += lit.Length; } else p++;
        }
        return o;
    }

    // \p{Lu}\p{Ll}+ by scalar categories
    static List<(int, int)> RefUpperLower(string s)
    {
        var o = new List<(int, int)>();
        static (UnicodeCategory, int) At(string s, int i) => (CharUnicodeInfo.GetUnicodeCategory(s, i), char.IsSurrogatePair(s, i) ? 2 : 1);
        for (int p = 0; p < s.Length;)
        {
            var (c0, w0) = At(s, p);
            int q = p + w0;
            if (c0 == UnicodeCategory.UppercaseLetter)
                while (q < s.Length && At(s, q) is (UnicodeCategory.LowercaseLetter, var w)) q += w;
            if (c0 == UnicodeCategory.UppercaseLetter && q > p + w0) { o.Add((p, q)); p = q; } else p += w0;
        }
        return o;
    }

    static List<(int, int)> Ours(CoreRegex re, string s, int from = 0)
    {
        var o = new List<(int, int)>();
        foreach (var m in re.Matches(s.AsSpan(from))) o.Add((from + m.Start.Value, from + m.End.Value));
        return o;
    }

    static void Check(string pattern, Func<string, List<(int, int)>> reference)
    {
        var re = new CoreRegex(pattern);
        var want = reference(Text);
        Assert.NotEmpty(want);
        Assert.Equal(want, Ours(re, Text));
        Assert.Equal(want.Count, re.Count(Text));
        Assert.Equal(want.Count, re.Count(Encoding.UTF8.GetBytes(Text)));
        // every start near the end: the scalar tails after the vector loops
        for (int from = Text.Length - 200; from <= Text.Length; from += 7)
        {
            if (char.IsLowSurrogate(Text[Math.Min(from, Text.Length - 1)])) continue;
            string tail = Text[from..];
            Assert.Equal(reference(tail), Ours(re, tail));
            Assert.Equal(reference(tail).Count, re.Count(Encoding.UTF8.GetBytes(tail)));
        }
    }

    [Theory]
    [InlineData("AKIA|ASIA")]
    [InlineData("Sherlock|Shylock")]
    [InlineData("Holmes|Hxlmes")]
    [InlineData("Watson|Irene|back")]
    public void Literal_sets_match_a_leftmost_first_scan(string pattern) =>
        Check(pattern, s => RefLiterals(s, pattern.Split('|')));

    [Theory]
    [InlineData("holmes")]
    [InlineData("sherlock holmes")]
    [InlineData("watson")]
    [InlineData("bit")]
    [InlineData("check")]
    public void Ascii_caseless_literals_fold_like_unicode_simple_case_folding(string lit) =>
        Check("(?i)" + lit, s => RefCaseless(s, lit));

    [Fact]
    public void Start_state_acceleration_keeps_unicode_class_matches() =>
        Check("\\p{Lu}\\p{Ll}+", RefUpperLower);

    [Theory]
    [InlineData("[0-9]{1,2}:[0-9]{2}")]
    [InlineData("[A-Za-z]{8,13}")]
    [InlineData("[A-Z][a-z]+")]
    public void Ascii_patterns_match_the_bcl(string pattern) =>
        Check(pattern, s => System.Text.RegularExpressions.Regex.Matches(s, pattern).Select(m => (m.Index, m.Index + m.Length)).ToList());
}
