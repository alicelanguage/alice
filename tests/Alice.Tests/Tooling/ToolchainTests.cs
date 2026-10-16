using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Alice.Cli.Toolchains;

namespace Alice.Tests.Tooling;

public class ToolchainTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "alice-tc-" + Guid.NewGuid().ToString("N"));

    public ToolchainTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("0.10.1", "0.10.2")]
    [InlineData("0.10.9", "0.11.0")]
    [InlineData("0.11.0-nightly.20261015", "0.11.0-nightly.20261016")]
    [InlineData("0.11.0-nightly.20261016", "0.11.0")]
    [InlineData("v0.10.1", "0.10.10")]
    public void Versions_order_semantically(string lower, string higher)
    {
        Assert.True(ToolchainVersion.Parse(lower) < ToolchainVersion.Parse(higher));
        Assert.True(ToolchainVersion.Parse(higher) > ToolchainVersion.Parse(lower));
    }

    [Fact]
    public void Version_parsing_rejects_garbage_and_reports_channel()
    {
        Assert.Null(ToolchainVersion.TryParse("latest"));
        Assert.Equal("nightly", ToolchainVersion.Parse("0.11.0-nightly.20261016").Channel);
        Assert.Equal("stable", ToolchainVersion.Parse("v0.10.1").Channel);
        Assert.Equal("0.10.1", ToolchainVersion.Parse("v0.10.1").ToString());
    }

    [Fact]
    public void Archive_names_match_release_packaging()
    {
        Assert.Equal("alice-0.10.1-linux-x64.tar.gz", Platform.ArchiveName("0.10.1", "linux-x64"));
        Assert.Equal("alice-0.10.1-win-x64.zip", Platform.ArchiveName("0.10.1", "win-x64"));
        Assert.Contains(Platform.CurrentRid(), new[] { "linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64" });
    }

    [Fact]
    public void Github_release_json_is_parsed_into_releases()
    {
        const string json = """
            [
              {"tag_name":"nightly","name":"Alice nightly 0.11.0-nightly.20261016","prerelease":true,"draft":false,
               "published_at":"2026-10-16T03:00:00Z","body":"n",
               "assets":[{"name":"alice-0.11.0-nightly.20261016-linux-x64.tar.gz","browser_download_url":"https://x/n.tgz","size":10}]},
              {"tag_name":"v0.10.1","name":"Alice v0.10.1","prerelease":false,"draft":false,
               "published_at":"2026-10-16T01:00:00Z","body":"notes",
               "assets":[{"name":"alice-0.10.1-linux-x64.tar.gz","browser_download_url":"https://x/a.tgz","size":5},
                         {"name":"alice-0.10.1-linux-x64.tar.gz.sha256","browser_download_url":"https://x/a.sha","size":1}]},
              {"tag_name":"v0.9.0","prerelease":false,"draft":true,"assets":[]}
            ]
            """;
        var rs = ReleaseFeed.Parse(json);
        Assert.Equal(2, rs.Count);
        var nightly = rs[0];
        Assert.Equal("0.11.0-nightly.20261016", nightly.Version.ToString());
        Assert.Equal("nightly", nightly.Version.Channel);
        var stable = rs[1];
        Assert.Equal("https://x/a.tgz", stable.Asset("linux-x64")!.Url);
        Assert.Equal("https://x/a.sha", stable.Checksum("linux-x64")!.Url);
        Assert.Equal(stable, ReleaseFeed.Pick(rs, "stable"));
        Assert.Equal(nightly, ReleaseFeed.Pick(rs, "nightly"));
        Assert.Equal(stable, ReleaseFeed.Pick(rs, "0.10.1"));
        Assert.Null(ReleaseFeed.Pick(rs, "0.10.7"));
    }

    [Fact]
    public void Install_verifies_checksum_extracts_and_sets_default()
    {
        var home = new ToolchainHome(Path.Combine(root, "home"));
        var (archive, sha) = FakeArchive("0.10.1", tamper: false);
        var source = new FakeSource(Release("0.10.1", archive, sha));

        var installed = new Installer(home, source).Install("stable", Platform.CurrentRid());

        Assert.Equal("0.10.1", installed.ToString());
        Assert.True(File.Exists(Path.Combine(home.ToolchainDir("0.10.1"), Platform.ExeName)));
        Assert.Equal(["0.10.1"], home.Installed().Select(v => v.ToString()));
        Assert.Equal("0.10.1", home.Default);
        Assert.True(File.Exists(home.ShimPath));
        Assert.Contains(home.ToolchainDir("0.10.1"), File.ReadAllText(home.ShimPath));
    }

    [Fact]
    public void Install_rejects_tampered_archives_and_leaves_nothing_behind()
    {
        var home = new ToolchainHome(Path.Combine(root, "home"));
        var (archive, sha) = FakeArchive("0.10.2", tamper: true);
        var source = new FakeSource(Release("0.10.2", archive, sha));

        var e = Assert.Throws<ToolchainException>(() => new Installer(home, source).Install("0.10.2", Platform.CurrentRid()));
        Assert.Contains("checksum", e.Message);
        Assert.Empty(home.Installed());
        Assert.False(Directory.Exists(home.ToolchainDir("0.10.2")));
    }

    [Fact]
    public void Override_file_in_a_parent_directory_selects_the_toolchain()
    {
        var home = new ToolchainHome(Path.Combine(root, "home"));
        home.Default = "0.10.1";
        var project = Path.Combine(root, "proj", "src", "deep");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(root, "proj", ToolchainHome.OverrideFile), "0.10.0\n");

        Assert.Equal(("0.10.0", "alice-toolchain file"), Strip(home.Resolve(project, env: null)));
        Assert.Equal(("0.10.3", "ALICE_TOOLCHAIN"), Strip(home.Resolve(project, env: "0.10.3")));
        Assert.Equal(("0.10.1", "default"), Strip(home.Resolve(root, env: null)));

        static (string, string) Strip((string Version, string Source) r) => (r.Version, r.Source.Split(' ')[0] == "alice-toolchain" ? "alice-toolchain file" : r.Source);
    }

    [Fact]
    public void Uninstall_removes_the_toolchain_and_clears_a_dangling_default()
    {
        var home = new ToolchainHome(Path.Combine(root, "home"));
        Directory.CreateDirectory(home.ToolchainDir("0.10.1"));
        File.WriteAllText(Path.Combine(home.ToolchainDir("0.10.1"), Platform.ExeName), "");
        home.Default = "0.10.1";
        home.Uninstall("0.10.1");
        Assert.Empty(home.Installed());
        Assert.Null(home.Default);
    }

    [Fact]
    public void Update_notice_only_when_newer_and_cache_is_respected()
    {
        var home = new ToolchainHome(Path.Combine(root, "home"));
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdateNotifier.IsStale(home, now));
        UpdateNotifier.Save(home, "0.10.2", now);
        Assert.False(UpdateNotifier.IsStale(home, now.AddHours(5)));
        Assert.True(UpdateNotifier.IsStale(home, now.AddHours(25)));
        Assert.Contains("0.10.2", UpdateNotifier.Notice(home, "0.10.1")!);
        Assert.Contains("alice self update", UpdateNotifier.Notice(home, "0.10.1")!);
        Assert.Null(UpdateNotifier.Notice(home, "0.10.2"));
        Assert.Null(UpdateNotifier.Notice(home, "0.11.0-nightly.20261020"));
    }

    [Fact]
    public void Update_checks_are_disabled_in_ci_and_by_opt_out()
    {
        Assert.False(UpdateNotifier.Enabled(name => name == "CI" ? "true" : null));
        Assert.False(UpdateNotifier.Enabled(name => name == "ALICE_NO_UPDATE_CHECK" ? "1" : null));
        Assert.True(UpdateNotifier.Enabled(_ => null));
    }

    // ---- fakes ----

    sealed class FakeSource(Release release) : IReleaseSource
    {
        public IReadOnlyList<Release> List() => [release];
        public Stream Open(string url) => File.OpenRead(url);
    }

    static Release Release(string version, string archive, string sha) =>
        new(ToolchainVersion.Parse(version), "v" + version, DateTimeOffset.UnixEpoch, "",
            [new ReleaseAsset(Path.GetFileName(archive), archive, new FileInfo(archive).Length),
             new ReleaseAsset(Path.GetFileName(sha), sha, new FileInfo(sha).Length)]);

    (string Archive, string Sha) FakeArchive(string version, bool tamper)
    {
        string rid = Platform.CurrentRid();
        var payload = Path.Combine(root, "payload-" + version);
        Directory.CreateDirectory(Path.Combine(payload, "sdk"));
        File.WriteAllText(Path.Combine(payload, Platform.ExeName), "#!/bin/sh\necho alice " + version + "\n");
        File.WriteAllText(Path.Combine(payload, "sdk", "Alice.props"), "<Project />");
        var archive = Path.Combine(root, Platform.ArchiveName(version, rid));
        if (archive.EndsWith(".zip")) ZipFile.CreateFromDirectory(payload, archive);
        else
        {
            using var fs = File.Create(archive);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            TarFile.CreateFromDirectory(payload, gz, includeBaseDirectory: false);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
        if (tamper) hash = new string('0', 64);
        var sha = archive + ".sha256";
        File.WriteAllText(sha, $"{hash}  {Path.GetFileName(archive)}\n", Encoding.ASCII);
        return (archive, sha);
    }
}
