using System.Text.RegularExpressions;
using System.Xml.Linq;
using Alice.Cli.Toolchains;

namespace Alice.Tests.Tooling;

/// <summary>Release metadata the Release workflow depends on: the version in Directory.Build.props passes the
/// pattern in .github/scripts/release-version.ps1, the CHANGELOG has its section, and every test INVARIANTS.md
/// names exists.</summary>
public class ReleaseMetadataTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static string Version() =>
        XDocument.Load(Path.Combine(Root(), "Directory.Build.props")).Descendants("Version").Single().Value;

    /// <summary>The pattern of the script's `-cnotmatch '...'` check (PowerShell -cmatch is a case-sensitive .NET regex).</summary>
    static Regex ScriptPattern()
    {
        var script = File.ReadAllText(Path.Combine(Root(), ".github", "scripts", "release-version.ps1"));
        var m = Regex.Match(script, @"\$version -cnotmatch '([^']+)'");
        Assert.True(m.Success, "release-version.ps1 must check $version with -cnotmatch '<pattern>'");
        return new Regex(m.Groups[1].Value, RegexOptions.CultureInvariant);
    }

    [Fact]
    public void Source_version_passes_release_validation()
    {
        var v = Version();
        Assert.Matches(ScriptPattern(), v);
        var parsed = ToolchainVersion.TryParse(v);
        Assert.NotNull(parsed);
        Assert.True(parsed!.Major >= 1, $"no more 0.x releases: {v}");
        Assert.NotEqual("nightly", parsed.Channel);
    }

    [Theory]
    [InlineData("1.0.0-rc.1", true)]
    [InlineData("1.0.0-rc.12", true)]
    [InlineData("1.0.0", true)]
    [InlineData("1.2.10", true)]
    [InlineData("2.0.0", true)]
    [InlineData("0.14.0", false)]
    [InlineData("0.19.3", false)]
    [InlineData("1.0.0-RC.1", false)]
    [InlineData("1.0.0-rc.0", false)]
    [InlineData("1.0.0-rc", false)]
    [InlineData("1.0.0-beta.1", false)]
    [InlineData("1.0.0-nightly.20261102", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.00.0", false)]
    [InlineData("1.0", false)]
    [InlineData("v1.0.0", false)]
    public void Release_versions_are_semver_from_one_with_optional_rc(string version, bool accepted) =>
        Assert.Equal(accepted, ScriptPattern().IsMatch(version));

    [Fact]
    public void Release_candidates_sort_before_their_release_and_are_not_stable()
    {
        var rc = ToolchainVersion.Parse("1.0.0-rc.1");
        Assert.Equal("preview", rc.Channel);
        Assert.True(rc.CompareTo(ToolchainVersion.Parse("1.0.0")) < 0);
        Assert.True(rc.CompareTo(ToolchainVersion.Parse("1.0.0-rc.2")) < 0);
        Assert.True(rc.CompareTo(ToolchainVersion.Parse("0.14.0")) > 0);
    }

    [Fact]
    public void Changelog_has_a_section_for_the_version_below_an_empty_unreleased()
    {
        var lines = File.ReadAllLines(Path.Combine(Root(), "CHANGELOG.md"));
        var headings = lines.Select((l, i) => (l, i)).Where(t => t.l.StartsWith("## ")).ToList();
        Assert.Equal("## Unreleased", headings[0].l);
        // the Release workflow takes notes from the section whose heading starts with "## <version> "
        Assert.StartsWith($"## {Version()} ", headings[1].l);
        Assert.All(lines[(headings[0].i + 1)..headings[1].i], l => Assert.True(string.IsNullOrWhiteSpace(l), l));
    }

    [Fact]
    public void Every_test_named_in_INVARIANTS_md_exists()
    {
        var root = Root();
        var text = File.ReadAllText(Path.Combine(root, "INVARIANTS.md"));
        var sections = Regex.Split(text, @"^## \d+\. ", RegexOptions.Multiline).Skip(1).ToList();
        Assert.True(sections.Count >= 12, $"expected at least 12 guarantees, found {sections.Count}");
        foreach (var section in sections)
        {
            var title = section.Split('\n')[0];
            Assert.Contains("**Statement.**", section);
            Assert.Contains("**Scope and exclusions.**", section);
            var pinned = section[section.IndexOf("**Pinned by.**", StringComparison.Ordinal)..];
            var tests = Regex.Matches(pinned, @"`(tests/[^`]+\.cs)`").Select(m => m.Groups[1].Value).ToList();
            Assert.True(tests.Count > 0, $"'{title}' names no test");
            foreach (var t in tests)
                Assert.True(File.Exists(Path.Combine(root, t)), $"'{title}' names a missing test: {t}");
        }
        Assert.Contains("SECURITY.md", text);
    }
}
