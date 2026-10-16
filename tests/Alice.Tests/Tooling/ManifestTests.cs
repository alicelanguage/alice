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
        Assert.Equal(Path.Combine(root, "app", "demo-app.alproj"), proj);
        var xml = File.ReadAllText(proj);
        Assert.Contains("generated from alice.toml", xml);
        Assert.Contains("<OutputType>Exe</OutputType>", xml);
        Assert.Contains("<PackageReference Include=\"MonoGame.Framework.DesktopGL\" Version=\"3.8.*\" />", xml);
        Assert.Contains("../geo/geo.alproj", xml.Replace('\\', '/'));
        Assert.Contains("../legacy/Legacy.csproj", xml.Replace('\\', '/'));
        Assert.Contains("<PublishAot>true</PublishAot>", xml);
        Assert.True(File.Exists(Path.Combine(root, "geo", "geo.alproj")));   // path deps are synced too
        Assert.Contains("<OutputType>Library</OutputType>", File.ReadAllText(Path.Combine(root, "geo", "geo.alproj")));
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

    [Fact]
    public void Slnx_entries_get_the_csharp_project_type()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        var slnx = Path.Combine(dir, "All.slnx");
        ProjectFiles.NewSolution(slnx);
        ProjectFiles.AddToSolution(slnx, Path.Combine(dir, "a", "a.alproj"));
        ProjectFiles.AddToSolution(slnx, Path.Combine(dir, "a", "a.alproj"));
        var text = File.ReadAllText(slnx);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "a.alproj"));
        Assert.Contains("Type=\"C#\"", text);
    }
}
