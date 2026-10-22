using Alice.Cli.Projects;

namespace Alice.Tests.Tooling;

public class ManifestTests
{
    const string Toml = """
        # demo project
        [package]
        name = "demo-app"
        version = "0.2.0"
        kind = "exe"

        [dependencies]
        "MonoGame.Framework.DesktopGL" = "3.8.*"
        geo = { path = "../geo" }
        legacy = { path = "../legacy/Legacy.csproj" }

        [build]
        native = true
        """;

    [Fact]
    public void Parses_tables_strings_bools_and_inline_tables()
    {
        var t = TomlLite.Parse(Toml);
        Assert.Equal("demo-app", t["package"]["name"]);
        Assert.Equal("3.8.*", t["dependencies"]["MonoGame.Framework.DesktopGL"]);
        Assert.Equal("../geo", ((Dictionary<string, object>)t["dependencies"]["geo"])["path"]);
        Assert.Equal(true, t["build"]["native"]);
    }

    [Fact]
    public void Bad_toml_reports_the_line()
    {
        var ex = Assert.Throws<FormatException>(() => TomlLite.Parse("[package]\nname = \n"));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Generates_msbuild_project_from_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "alice-man-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        Directory.CreateDirectory(Path.Combine(root, "geo"));
        Directory.CreateDirectory(Path.Combine(root, "legacy"));
        File.WriteAllText(Path.Combine(root, "app", "alice.toml"), Toml);
        File.WriteAllText(Path.Combine(root, "geo", "alice.toml"), "[package]\nname = \"geo\"\nkind = \"lib\"\n");
        File.WriteAllText(Path.Combine(root, "legacy", "Legacy.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var proj = Manifest.Sync(Path.Combine(root, "app"));
        Assert.Equal(Path.Combine(root, "app", ".alice", "demo-app.alproj"), proj);
        var xml = File.ReadAllText(proj);
        Assert.Contains("generated from alice.toml", xml);
        Assert.Contains("<OutputType>Exe</OutputType>", xml);
        Assert.Contains("<PackageReference Include=\"MonoGame.Framework.DesktopGL\" Version=\"3.8.*\" />", xml);
        Assert.Contains("../../geo/.alice/geo.alproj", xml.Replace('\\', '/'));
        Assert.Contains("../../legacy/Legacy.csproj", xml.Replace('\\', '/'));
        Assert.Contains("<PublishAot>true</PublishAot>", xml);
        Assert.True(File.Exists(Path.Combine(root, "geo", ".alice", "geo.alproj")));   // path deps are synced too
        Assert.Contains("<OutputType>Library</OutputType>", File.ReadAllText(Path.Combine(root, "geo", ".alice", "geo.alproj")));
    }

    [Fact]
    public void Adding_a_dependency_edits_the_manifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-man-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"x\"\n");
        Manifest.AddDependency(dir, "Newtonsoft.Json", "13.0.3");
        Manifest.AddDependency(dir, "geo", null, "../geo");
        var t = TomlLite.Parse(File.ReadAllText(Path.Combine(dir, "alice.toml")));
        Assert.Equal("13.0.3", t["dependencies"]["Newtonsoft.Json"]);
        Assert.Equal("../geo", ((Dictionary<string, object>)t["dependencies"]["geo"])["path"]);
    }

    [Theory]
    [InlineData("", "false", "true", null)]
    [InlineData("[runtime]\ngc = \"server\"\n", "true", "true", null)]
    [InlineData("[runtime]\ngc = \"low-memory\"\n", "false", "false", "7")]
    public void Runtime_gc_mode_maps_to_msbuild(string runtime, string server, string concurrent, string? conserve)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-gc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"gc\"\n" + runtime);
        var xml = File.ReadAllText(Manifest.Sync(dir));
        Assert.Contains($"<ServerGarbageCollection>{server}</ServerGarbageCollection>", xml);
        Assert.Contains($"<ConcurrentGarbageCollection>{concurrent}</ConcurrentGarbageCollection>", xml);
        if (conserve is null) Assert.DoesNotContain("GCConserveMemory", xml);
        else Assert.Contains($"System.GC.ConserveMemory\" Value=\"{conserve}\"", xml);
    }

    [Fact]
    public void Unknown_gc_mode_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-gc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"gc\"\n[runtime]\ngc = \"fast\"\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Manifest.Sync(dir));
        Assert.Contains("workstation, server, low-memory", ex.Message);
    }
}
