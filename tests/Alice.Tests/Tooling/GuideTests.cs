using Alice.Cli.Commands;
using Alice.Compiler;

namespace Alice.Tests.Tooling;

/// <summary>`alice guide` prints guide/GUIDE.md, embedded in the CLI so it always matches the installed version.</summary>
public class GuideTests
{
    [Fact]
    public void Guide_is_embedded_and_covers_the_agent_workflow()
    {
        string g = GuideCommand.Text();
        Assert.StartsWith("# Alice guide", g);
        foreach (var must in new[] { "alice prove", "RULES.al", "PROOF.al", "alice explain", "own ", "borrow ", "scope", "pure def", "par a, b" })
            Assert.Contains(must, g);
    }

    // helpers the RULES.al example refers to
    const string RuleHelpers = "def clamp(x : Int32, lo : Int32, hi : Int32) : Int32\n  x < lo ? lo : (x > hi ? hi : x)\nend\ndef toggle(b : Bool, n : Int32) : Bool\n  !b\nend\n";

    public static IEnumerable<object[]> Examples() =>
        GuideCommand.Topics().Prepend("guide").SelectMany(topic =>
            System.Text.RegularExpressions.Regex.Matches(GuideCommand.Text(topic), "```alice\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select((m, i) => new object[] { $"{topic}#{i}", m.Groups[1].Value }));

    [Fact]
    public void Guide_has_alice_examples() => Assert.True(Examples().Count() >= 14);

    [Fact]
    public void Guide_text_is_the_same_on_every_os()
    {
        Assert.Equal("# A\n```alice\nx = 1\n```\n", GuideCommand.Normalize("# A\r\n```alice\r\nx = 1\r\n```\r\n"));   // CRLF checkout on Windows
        Assert.DoesNotContain("\r", GuideCommand.Text());
    }

    [Fact]
    public void Topics_are_embedded()
    {
        Assert.Equal(["interop", "performance", "proofs"], GuideCommand.Topics());
        Assert.Contains("When to use what", GuideCommand.Text("performance"));
    }

    [Fact]
    public void Agents_block_merges_into_an_existing_AGENTS_md()
    {
        string once = GuideCommand.MergeAgents("# My project\n\nOur rules.\n");
        Assert.StartsWith("# My project\n\nOur rules.\n\n" + GuideCommand.Start, once);
        Assert.Contains("Not Ruby, not Crystal", once);
        Assert.Equal(once, GuideCommand.MergeAgents(once));   // idempotent: the block is replaced, not appended again
        Assert.EndsWith(GuideCommand.End + "\n", GuideCommand.MergeAgents(null));
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Every_guide_example_compiles(string where, string code)
    {
        _ = where;
        AliceCompiler.Transpile(code.Contains("# RULES.al") ? RuleHelpers + code : code);
    }
}
