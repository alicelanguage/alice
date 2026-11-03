using Alice.Cli.Projects;

namespace Alice.Tests.Runtime;

/// <summary>The CLI reads alice.toml through Alice.Core.Toml (<see cref="ManifestToml"/>): same shape as the former
/// TomlLite for every manifest the repository uses, TOML 1.0 errors with the line.</summary>
public class CoreManifestTests
{
    const string Manifest = """
        # demo project
        [package]
        name = "demo-app"
        version = "0.2.0"
        kind = "exe"
        authors = ["a", "b"]

        [dependencies]
        "MonoGame.Framework.DesktopGL" = "3.8.*"
        Newtonsoft.Json = "13.0.3"
        geo = { path = "../geo" }
        util = { workspace = true }

        [build]
        native = true
        opt = 3

        [runtime.extra]
        gc = "server"
        """;

    [Fact]
    public void Core_toml_gives_the_manifest_shape_of_toml_lite()
    {
        var core = ManifestToml.Parse(Manifest);
        var lite = TomlLite.Parse(Manifest.Replace("[runtime.extra]", "[runtime.extra]"));
        foreach (var table in new[] { "package", "build" })
            Assert.Equal(lite[table].OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + Show(kv.Value)), core[table].OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + Show(kv.Value)));
        Assert.Equal("3.8.*", core["dependencies"]["MonoGame.Framework.DesktopGL"]);
        Assert.Equal("13.0.3", core["dependencies"]["Newtonsoft.Json"]);
        Assert.Equal("../geo", ((Dictionary<string, object>)core["dependencies"]["geo"])["path"]);
        Assert.Equal(true, ((Dictionary<string, object>)core["dependencies"]["util"])["workspace"]);
        Assert.Equal(3L, core["build"]["opt"]);
        Assert.Equal("server", core["runtime.extra"]["gc"]);
        Assert.Equal(lite["runtime.extra"]["gc"], core["runtime.extra"]["gc"]);
        Assert.Empty(core[""]);
    }

    static string Show(object v) => v is List<object> l ? "[" + string.Join(",", l) + "]" : v.ToString()!;

    [Fact]
    public void Errors_name_the_manifest_line()
    {
        var e = Assert.Throws<FormatException>(() => ManifestToml.Parse("[package]\nname = \n"));
        Assert.Contains("alice.toml line 2", e.Message);
        Assert.Throws<FormatException>(() => ManifestToml.Parse("[package]\nname = \"a\"\nname = \"b\"\n"));
    }

    [Fact]
    public void Manifest_sync_reads_through_core_toml()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-core-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"coremf\"\nkind = \"lib\"\n\n[dependencies]\nNewtonsoft.Json = \"13.0.3\"\n");
            var xml = File.ReadAllText(Alice.Cli.Projects.Manifest.Sync(dir));
            Assert.Contains("<OutputType>Library</OutputType>", xml);
            Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", xml);
            File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"coremf\"\n[package]\n");
            var e = Assert.Throws<FormatException>(() => Alice.Cli.Projects.Manifest.Sync(dir));
            Assert.Contains("defined twice", e.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
