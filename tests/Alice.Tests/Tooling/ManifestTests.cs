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
    [InlineData("[runtime]\ngc = \"workstation\"\n", "false", "true", null)]
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

    [Theory]
    [InlineData("", false)]
    [InlineData("[runtime]\nglobalization = \"culture\"\n", false)]
    [InlineData("[runtime]\nglobalization = \"invariant\"\n", true)]
    public void Runtime_globalization_maps_to_msbuild(string runtime, bool invariant)
    {
        // invariant: no ICU at run time (2-4 MB less memory on every program measured); strings sort and compare
        // ordinally, as in Ruby, instead of linguistically
        var dir = Path.Combine(Path.GetTempPath(), "alice-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"glob\"\n" + runtime);
        var xml = File.ReadAllText(Manifest.Sync(dir));
        Assert.Equal(invariant, xml.Contains("<InvariantGlobalization>true</InvariantGlobalization>"));
    }

    [Fact]
    public void Unknown_gc_mode_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-gc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"gc\"\n[runtime]\ngc = \"fast\"\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Manifest.Sync(dir));
        Assert.Contains("auto, workstation, server, low-memory", ex.Message);
    }

    static string SyncToml(string toml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), toml);
        return File.ReadAllText(Manifest.Sync(dir));
    }

    [Fact]
    public void Package_metadata_maps_to_nuget_properties()
    {
        var xml = SyncToml("""
            [package]
            name = "geo"
            id = "Acme.Geo"
            version = "1.2.3"
            kind = "lib"
            description = "Geometry for Alice & C#"
            authors = ["Ada", "Grace"]
            license = "Apache-2.0"
            repository = "https://github.com/acme/geo"
            homepage = "https://acme.dev/geo"
            tags = ["geometry", "math"]
            """);
        Assert.Contains("<PackageId>Acme.Geo</PackageId>", xml);
        Assert.Contains("<Description>Geometry for Alice &amp; C#</Description>", xml);
        Assert.Contains("<Authors>Ada;Grace</Authors>", xml);
        Assert.Contains("<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>", xml);
        Assert.Contains("<RepositoryUrl>https://github.com/acme/geo</RepositoryUrl>", xml);
        Assert.Contains("<PackageProjectUrl>https://acme.dev/geo</PackageProjectUrl>", xml);
        Assert.Contains("<PackageTags>geometry;math;alice</PackageTags>", xml);
        Assert.Contains("<IsPackable>true</IsPackable>", xml);
    }

    [Fact]
    public void Tool_kind_packs_as_a_dotnet_tool()
    {
        var xml = SyncToml("[package]\nname = \"rails-al\"\nkind = \"tool\"\ncommand = \"ral\"\n");
        Assert.Contains("<OutputType>Exe</OutputType>", xml);
        Assert.Contains("<PackAsTool>true</PackAsTool>", xml);
        Assert.Contains("<ToolCommandName>ral</ToolCommandName>", xml);
        // default command is the package name
        Assert.Contains("<ToolCommandName>tl</ToolCommandName>", SyncToml("[package]\nname = \"tl\"\nkind = \"tool\"\n"));
    }

    [Fact]
    public void Readme_is_packed_when_present()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-readme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"r\"\nkind = \"lib\"\n");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# r\n");
        var xml = File.ReadAllText(Manifest.Sync(dir));
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", xml);
        Assert.Contains("Pack=\"true\" PackagePath=\"/\"", xml);
    }

    [Theory]
    [InlineData("kind = \"app\"", "exe, lib, tool, game")]
    [InlineData("id = \"bad id!\"", "not a valid NuGet package id")]
    [InlineData("version = \"one\"", "not a valid version")]
    public void Bad_package_fields_are_rejected(string field, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SyncToml("[package]\nname = \"x\"\n" + field + "\n"));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Default_gc_is_decided_by_the_compiler()
    {
        // auto: the build picks server GC when the compiled code forks or runs parallel loops (alice gen reports it)
        var dir = Path.Combine(Path.GetTempPath(), "alice-gcauto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alice.toml"), "[package]\nname = \"gc\"\n");
        var xml = File.ReadAllText(Manifest.Sync(dir));
        Assert.Contains("<AliceGc>auto</AliceGc>", xml);
        Assert.DoesNotContain("<ServerGarbageCollection>", xml);
    }
}
