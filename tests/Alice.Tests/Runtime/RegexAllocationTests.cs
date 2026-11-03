using System.Text;
using Alice.Runtime;
using Xunit.Abstractions;
using CoreRegex = Alice.Core.Text.Regex;

namespace Alice.Tests.Runtime;

/// <summary>Match-only regex calls allocate nothing (ADR 0036): IsMatch, Count, Find, Matches, TryMatch over strings
/// and UTF-8, Prelude match? and =~ (the last match is materialized lazily), on both tiers. scan with a block
/// allocates only the strings it yields.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class RegexAllocationTests(ITestOutputHelper output)
{
    static readonly string Text = string.Concat(Enumerable.Repeat("The quick brown fox, 12:30 and Sherlock Holmes at 09:15. ", 50));
    static readonly byte[] Utf8 = Encoding.UTF8.GetBytes(Text);

    long Least(Action a)
    {
        for (int i = 0; i < 20; i++) a();   // warm caches, pools, tier-1
        return AllocationProbe.Least(a, output.WriteLine);
    }

    [Theory]
    [InlineData("Sherlock Holmes")]
    [InlineData("[0-9]{1,2}:[0-9]{2}")]
    [InlineData("\\b\\w+\\b")]
    [InlineData("(?i)holmes|watson")]
    [InlineData("(?<=at )\\d+")]
    [InlineData("(\\w+) \\1")]
    public void Match_only_calls_do_not_allocate(string pattern)
    {
        var re = new CoreRegex(pattern);
        Assert.Equal(0, Least(() => re.IsMatch(Text)));
        Assert.Equal(0, Least(() => re.Count(Text)));
        Assert.Equal(0, Least(() => re.Count(Utf8)));
        Assert.Equal(0, Least(() => re.Find(Text.AsSpan(), 7, out _)));
        Assert.Equal(0, Least(() => { int n = 0; foreach (var m in re.Matches(Utf8)) n += m.End.Value - m.Start.Value; }));
        Assert.Equal(0, Least(() => { Span<Range> g = stackalloc Range[4]; re.TryMatch(Text.AsSpan(), g); }));
    }

    [Fact]
    public void Prelude_match_q_and_match_index_do_not_allocate()
    {
        var re = Prelude.regex("(\\d+):(\\d+)", "");
        Assert.Equal(0, Least(() => Text.match_q(re)));
        Assert.Equal(0, Least(() => Prelude.match_index(Text, re)));
        Assert.Equal("12:30", Prelude.last_match(0));
    }

    [Fact]
    public void Scan_block_allocates_only_the_yielded_strings()
    {
        var re = Prelude.regex("\\d+", "");
        string s = "a1b22c333";
        long scan = Least(() => s.scan(re, static _ => { }));
        long strings = Least(() => { _ = s[1..2]; _ = s[3..5]; _ = s[6..9]; });
        Assert.Equal(strings, scan);
    }
}
