using System.Diagnostics;
using System.IO.Compression;

namespace Alice.Tests.Integration;

/// <summary>alice pack / alice publish: Alice libraries and tools as NuGet packages, end to end through a local feed.</summary>
[Trait("Category", "Integration")]
[Collection("msbuild")]   // MSBuild runs on the same repository projects (obj/) must not overlap
public class PackageTests : IDisposable
{
    readonly List<string> created = [];

    string Temp(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"alice-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        created.Add(d);
        return d;
    }

    /// <summary>Test projects, feeds and tool installs are deleted: /tmp is often a small RAM disk.</summary>
    public void Dispose()
    {
        foreach (var d in created)
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static (int Code, string Output) Run(string exe, string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    static List<string> Entries(string nupkg)
    {
        using var z = ZipFile.OpenRead(nupkg);
        return z.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public void Library_package_carries_the_runtime_and_metadata()
    {
        var dir = Temp("packlib");
        File.WriteAllText(Path.Combine(dir, "alice.toml"), """
            [package]
            name = "geo"
            id = "Acme.Geo"
            version = "1.4.0"
            kind = "lib"
            description = "Geometry"
            authors = ["Ada"]
            license = "MIT"
            """);
        File.WriteAllText(Path.Combine(dir, "README.md"), "# geo\n");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "geo.al"), "module Geo\n  def self.twice(x : Int32) : Int32\n    x * 2\n  end\nend\n");

        var outDir = Path.Combine(dir, "out");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "pack", dir, "-o", outDir]));
        var nupkg = Path.Combine(outDir, "Acme.Geo.1.4.0.nupkg");
        Assert.True(File.Exists(nupkg), string.Join(", ", Directory.GetFiles(outDir)));
        var entries = Entries(nupkg);
        Assert.Contains("lib/net10.0/geo.dll", entries);
        Assert.Contains("lib/net10.0/Alice.Runtime.dll", entries);
        Assert.Contains("README.md", entries);
        using var z = ZipFile.OpenRead(nupkg);
        using var spec = new StreamReader(z.GetEntry("Acme.Geo.nuspec")!.Open());
        string nuspec = spec.ReadToEnd();
        Assert.Contains("<license type=\"expression\">MIT</license>", nuspec);
        Assert.DoesNotContain("Alice.Runtime\" version", nuspec);   // bundled, not a nuget.org dependency
    }

    [Fact]
    public void Tool_publishes_to_a_feed_installs_and_runs()
    {
        var dir = Temp("packtool");
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"hatter\"\nversion = \"0.3.0\"\nkind = \"tool\"\ncommand = \"hatter\"\n");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "main.al"), "puts \"tea for #{ARGV.size} guests\"\n");

        var feed = Temp("feed");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "publish", dir, "--source", feed]));
        Assert.True(File.Exists(Path.Combine(feed, "hatter.0.3.0.nupkg")) || Directory.Exists(Path.Combine(feed, "hatter")),
            string.Join(", ", Directory.GetFileSystemEntries(feed)));

        var tools = Temp("tools");
        var (code, o) = Run("dotnet", tools, "tool", "install", "hatter", "--tool-path", tools, "--add-source", feed, "--version", "0.3.0");
        Assert.True(code == 0, o);
        var (rc, ro) = Run(Path.Combine(tools, OperatingSystem.IsWindows() ? "hatter.exe" : "hatter"), tools, "a", "b");
        Assert.True(rc == 0, ro);
        Assert.Contains("tea for 2 guests", ro);
    }

    [Fact]
    public void Publish_to_a_remote_source_needs_an_api_key()
    {
        var dir = Temp("nokey");
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"k\"\nkind = \"lib\"\n");
        Assert.Equal(2, Alice.Cli.App.Run(["-q", "publish", dir, "--source", "https://api.nuget.org/v3/index.json", "--dry-run"]));
    }

    [Fact]
    public void Games_are_not_packable()
    {
        var dir = Temp("nogame");
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"g\"\nkind = \"game\"\n");
        Assert.Equal(2, Alice.Cli.App.Run(["-q", "pack", dir]));
    }

    [Theory]
    [InlineData("def fib(n : Int32) : Int64\n  return n.to_i64 if n < 2\n  fib(n - 1) + fib(n - 2)\nend\nputs fib(20)\n", true)]
    [InlineData("puts 1 + 2\n", false)]
    public void Auto_gc_is_server_exactly_when_the_program_runs_in_parallel(string main, bool server)
    {
        // benchmarks/inet tree(20): building a tree from forked calls took 85 ms on workstation GC (57 gen0
        // collections copying the live tree) and 24 ms on server GC with DATAS, which starts as fast as workstation
        var dir = Temp("gcauto");
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"gcauto\"\n");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "main.al"), main);
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));
        var cfg = File.ReadAllText(Directory.GetFiles(Path.Combine(dir, "bin"), "gcauto.runtimeconfig.json", SearchOption.AllDirectories).Single());
        Assert.Contains($"\"System.GC.Server\": {(server ? "true" : "false")}", cfg);
    }

    [Fact]
    public void Builds_are_incremental_and_track_added_and_deleted_sources()
    {
        var dir = Temp("incr");
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"incr\"\nkind = \"lib\"\n");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "a.al"), "module A\n  def self.one : Int32\n    1\n  end\nend\n");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));
        var stamp = Directory.GetFiles(Path.Combine(dir, "obj"), "alice.stamp", SearchOption.AllDirectories).Single();
        var gen = Path.GetDirectoryName(stamp)!;
        var t0 = File.GetLastWriteTimeUtc(stamp);

        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));   // nothing changed: alice gen does not run
        Assert.Equal(t0, File.GetLastWriteTimeUtc(stamp));

        File.WriteAllText(Path.Combine(dir, "src", "b.al"), "module B\n  def self.two : Int32\n    2\n  end\nend\n");
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));   // an added file is generated and compiled
        Assert.Contains(Directory.GetFiles(gen, "*.g.cs"), f => Path.GetFileName(f).StartsWith("b."));

        File.Delete(Path.Combine(dir, "src", "b.al"));
        Assert.Equal(0, Alice.Cli.App.Run(["-q", "build", dir]));   // a deleted file's C# goes away with it
        Assert.DoesNotContain(Directory.GetFiles(gen, "*.g.cs"), f => Path.GetFileName(f).StartsWith("b."));
    }
}
