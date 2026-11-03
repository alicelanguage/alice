using System.Text;
using Alice.Core.Text;
using Alice.Runtime;
using CoreRegex = Alice.Core.Text.Regex;

namespace Alice.Tests.Runtime;

/// <summary>Ruby/Onigmo semantics of the Alice regex engine (ADR 0036) and the Prelude surface over it.</summary>
public class RegexRubyTests
{
    static string? First(string pattern, string input, string flags = "") => new CoreRegex(pattern, flags).Match(input)?[0];

    static string[] All(string pattern, string input, string flags = "")
    {
        var re = new CoreRegex(pattern, flags);
        var r = new List<string>();
        foreach (var m in re.Matches(input.AsSpan())) r.Add(input[m]);
        return r.ToArray();
    }

    [Fact]
    public void Caret_and_dollar_are_line_anchors_and_caret_skips_a_final_newline()
    {
        Assert.True(new CoreRegex("^b$").IsMatch("a\nb\nc"));
        Assert.Equal(1, new CoreRegex("^").Count("a\n"));
        Assert.Equal(2, new CoreRegex("$").Count("a\n"));
        Assert.True(new CoreRegex("b\\Z").IsMatch("ab\n"));
        Assert.False(new CoreRegex("b\\z").IsMatch("ab\n"));
        Assert.False(new CoreRegex("\\Ab").IsMatch("a\nb"));
    }

    [Fact]
    public void Flag_m_is_dot_all_and_s_is_an_alias()
    {
        Assert.False(new CoreRegex("a.b").IsMatch("a\nb"));
        Assert.True(new CoreRegex("a.b", "m").IsMatch("a\nb"));
        Assert.True(new CoreRegex("a.b", "s").IsMatch("a\nb"));
        Assert.True(new CoreRegex("a(?m:.)b").IsMatch("a\nb"));
        Assert.Equal("ab", First("a b # comment\n", "xab", "x"));
    }

    [Fact]
    public void Shorthands_are_ascii_and_posix_brackets_are_unicode()
    {
        string naive = "na" + (char)0xEF + "ve";
        Assert.Equal("na", First("\\w+", naive));
        Assert.Equal(naive, First("[[:alpha:]]+", naive));
        Assert.Equal(naive, First("\\p{L}+", naive));
        Assert.Equal("BEEF", First("\\h+", "xyzBEEF"));
        string arabicThree = ((char)0x663).ToString();
        Assert.Null(First("\\d", arabicThree));            // \\d is [0-9]
        Assert.Equal(arabicThree, First("[[:digit:]]", arabicThree));
        Assert.Equal(new[] { "na", "ve" }, All("\\b\\w+\\b", naive));
    }

    [Fact]
    public void Inline_flags_apply_to_the_rest_of_the_group_alternatives_included()
    {
        Assert.False(new CoreRegex("a(?i)b|c").IsMatch("C"));   // Onigmo: a(?i:b|c)
        Assert.True(new CoreRegex("a(?i)b|c").IsMatch("aC"));
        Assert.True(new CoreRegex("x(?i:ab)y").IsMatch("xABy"));
        Assert.False(new CoreRegex("x(?i:ab)y").IsMatch("xABY"));
    }

    [Fact]
    public void Case_folding_is_unicode_simple_folding()
    {
        Assert.True(new CoreRegex("k", "i").IsMatch(((char)0x212A).ToString()));   // KELVIN SIGN
        Assert.True(new CoreRegex("s", "i").IsMatch(((char)0x17F).ToString()));    // LONG S
        string ete = (char)0xE9 + "t" + (char)0xE9, ETE = (char)0xC9 + "T" + (char)0xC9;
        Assert.True(new CoreRegex(ete, "i").IsMatch(ETE));
        Assert.True(new CoreRegex(((char)0x3C3).ToString(), "i").IsMatch(((char)0x3C2).ToString()));   // final sigma
        Assert.True(new CoreRegex("[a-z]+", "i").IsMatch("ABC"));
    }

    [Fact]
    public void Named_groups_turn_off_unnamed_captures()
    {
        var re = new CoreRegex("(\\d+)-(?<m>\\d+)");
        Assert.Equal(1, re.GroupCount);
        var m = re.Match("2024-05")!;
        Assert.Equal("05", m[1]);
        Assert.Equal("05", m["m"]);
        Assert.Throws<RegexParseException>(() => new CoreRegex("(?<a>x)\\1"));
        Assert.Equal("aa", new CoreRegex("(?<a>a)\\k<a>").Match("xaa")![0]);
    }

    [Fact]
    public void Match_data_follows_ruby()
    {
        var m = new CoreRegex("b(\\d)(x)?").Match("ab7c")!;
        Assert.Equal("b7", m.ToString());
        Assert.Equal("7", m[1]);
        Assert.Null(m[2]);
        Assert.Equal("7", m[-2]);
        Assert.Equal("a", m.PreMatch);
        Assert.Equal("c", m.PostMatch);
        Assert.Equal(1, m.Begin(0));
        Assert.Equal(3, m.End(0));
        Assert.Equal(-1, m.Begin(2));
        Assert.Equal(new[] { "7", null }, m.Captures);
        Assert.Equal(3, m.Size);
        Assert.Equal(new[] { "b7", "7", null }, m.ToArray());
        Assert.Equal(new[] { "7", "b7" }, m.ValuesAt(1, 0));
        var n = new CoreRegex("(?<y>\\d+)-(?<d>\\d+)").Match("on 2024-05")!;
        Assert.Equal(new Dictionary<string, string?> { ["y"] = "2024", ["d"] = "05" }, n.NamedCaptures);
        Assert.Equal(new[] { "y", "d" }, n.Names);
    }

    [Fact]
    public void Replacement_templates_and_empty_matches()
    {
        char bq = (char)96, q = (char)39;
        Assert.Equal("Smith, John", new CoreRegex("(\\w+) (\\w+)").Replace("John Smith", "\\2, \\1"));
        Assert.Equal("[05]", new CoreRegex("(?<m>\\d+)").Replace("05", "[\\k<m>]"));
        Assert.Equal("<ab>", new CoreRegex("ab").Replace("ab", "<\\0>"));
        Assert.Equal("<ab>", new CoreRegex("ab").Replace("ab", "<\\&>"));
        Assert.Equal("x\\y", new CoreRegex("-").Replace("x-y", "\\\\"));
        Assert.Equal("a[a|c]c", new CoreRegex("b").Replace("abc", "[\\" + bq + "|\\" + q + "]"));
        Assert.Equal("-a-b-c-", new CoreRegex("x*").Replace("abc", "-"));
        Assert.Equal("-a--c-", new CoreRegex("b*").Replace("abc", "-"));
        Assert.Equal("X-b-c", new CoreRegex("a").Replace("a-b-c", "X", 1));
        Assert.Equal(new[] { "", "", "", "" }, All("", "abc"));
    }

    [Fact]
    public void Split_follows_ruby()
    {
        string[] Split(string pat, string s, int limit = 0) => new CoreRegex(pat).Split(s, limit);
        Assert.Equal(new[] { "a", "b" }, Split(",", "a,b,,"));
        Assert.Equal(new[] { "", "a" }, Split(",", ",a"));
        Assert.Equal(new[] { "a", "b", "c" }, Split("", "abc"));
        Assert.Equal(new[] { "a", "b" }, Split("x*", "axb"));
        Assert.Equal(new[] { "a", "1", "b", "2", "c" }, Split("(\\d)", "a1b2c"));
        Assert.Equal(new[] { "a", "b,c" }, Split(",", "a,b,c", 2));
        Assert.Equal(new[] { "a", "b", "", "" }, Split(",", "a,b,,", -1));
        Assert.Empty(Split(",", ""));
    }

    [Fact]
    public void Backtracking_constructs_run_on_the_own_backtracker()
    {
        Assert.Equal(RegexTier.Backtracking, new CoreRegex("(?=a)").Tier);
        Assert.Equal(RegexTier.Regular, new CoreRegex("a+b").Tier);
        Assert.Equal("d", First("(?<=ab|c)d", "cd"));
        Assert.Null(First("(?<!a)b", "ab"));
        Assert.Equal("aabaa", First("(a+)b\\1", "aabaa"));
        Assert.Equal("aBA", First("(a)B\\1", "aBA", "i"));
        Assert.Null(First("(?>a+)a", "aaaa"));
        Assert.Null(First("a*+a", "aaaa"));
        Assert.Equal("ab", First("a(?=b)b", "ab"));
        Assert.Equal("\r\n", First("\\R", "x\r\ny"));
        Assert.Equal(new string('a', 40), First("a{40}", new string('a', 50)));
        Assert.Throws<RegexParseException>(() => new CoreRegex("(?<=a+)b"));
    }

    [Fact]
    public void Catastrophic_patterns_stay_linear()
    {
        string many = new('a', 20_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(new CoreRegex("(a*)*b").IsMatch(many));         // empty-able loop: backtracker with memo
        Assert.False(new CoreRegex("(a|aa)+c").IsMatch(many));        // automata
        Assert.False(new CoreRegex("(?=(a+))a*b\\1").IsMatch(new string('a', 300)));   // backreference: budget-bounded
        Assert.True(sw.ElapsedMilliseconds < 20_000, sw.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void Utf8_and_utf16_inputs_agree()
    {
        string e = ((char)0xE9).ToString(), E = ((char)0xC9).ToString(), smile = char.ConvertFromUtf32(0x1F600);
        string s = "caf" + e + " na" + (char)0xEF + "ve " + smile + " " + E + "t" + e + " x";
        byte[] b = Encoding.UTF8.GetBytes(s);
        foreach (var p in new[] { "\\p{L}+", ".", "[^a-z ]", e, "(?i)" + e + "t" + e, "\\b\\w+\\b", "x$", "(?<=" + e + ")" })
        {
            var re = new CoreRegex(p);
            Assert.Equal(re.Count(s), re.Count(b));
        }
        Assert.Equal(1, new CoreRegex(".").Count(smile));   // a surrogate pair is one character
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        Assert.Equal(2, new CoreRegex(e).Replace(b, "E"u8, w));
        Assert.Equal(s.Replace(e, "E"), Encoding.UTF8.GetString(w.WrittenSpan));
    }

    [Fact]
    public void Syntax_errors_are_rejected_with_offsets()
    {
        foreach (var bad in new[] { "(", "a)", "[a", "a{2,1}", "*a", "\\k<nope>", "(?<n>a)(?<n2>\\2)", "\\p{Nope}", "\\X", "(?~a)" })
            Assert.Throws<RegexParseException>(() => new CoreRegex(bad));
    }

    [Fact]
    public void Prelude_last_match_is_set_by_match_index_and_never_by_match_q()
    {
        var re = Prelude.regex("(\\d)+", "");
        Assert.Equal(2, Prelude.match_index("ab12c", re));
        Assert.Equal("12", Prelude.last_match(0));
        Assert.Equal("2", Prelude.last_match(1));
        Assert.Equal("ab", Prelude.last_pre_match());
        Assert.True("zz9".match_q(re));
        Assert.Equal("12", Prelude.last_match(0));   // match? leaves the last match alone
        Assert.Null(Prelude.match_index("abc", re));
        Assert.Null(Prelude.last_match());            // a failed =~ clears it
        var seen = new List<string>();
        "a1b22".scan(re, m => seen.Add(m + ":" + Prelude.last_match(0)));
        Assert.Equal(new[] { "1:1", "22:22" }, seen);
        Assert.Equal("a<1>b<22>", "a1b22".gsub(Prelude.regex("\\d+", ""), d => "<" + d + ">"));
        Assert.Equal(new[] { "a", "b", "c" }, "a1b22c".split(Prelude.regex("\\d+", "")));
        Assert.Equal(new[] { "1", "22" }, "a1b22".scan(Prelude.regex("\\d+", "")));
        Assert.Equal(new[] { "." }, "a.b".scan("."));   // a string pattern is literal in scan
        Assert.True("a1".match_q("\\d"));             // and a regex in match?
    }
}
