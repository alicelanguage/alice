using Alice.Cli.Projects;

namespace Alice.Tests.Tooling;

/// <summary>Cargo-style workspaces: members, resolution by name, lockfile pinning, hidden generated projects.</summary>
public class WorkspaceTests
{
    static string NewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "alice-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "apps", "web"));
        Directory.CreateDirectory(Path.Combine(root, "apps", "cli"));
        Directory.CreateDirectory(Path.Combine(root, "geo"));
        File.WriteAllText(Path.Combine(root, "alice.toml"), "[workspace]\nmembers = [\"geo\", \"apps/*\"]\n");
        File.WriteAllText(Path.Combine(root, "geo", "alice.toml"), "[package]\nname = \"geo\"\nkind = \"lib\"\n");
        File.WriteAllText(Path.Combine(root, "apps", "web", "alice.toml"), "[package]\nname = \"web\"\n\n[dependencies]\ngeo = { workspace = true }\n\"Newtonsoft.Json\" = \"13.*\"\n");
        File.WriteAllText(Path.Combine(root, "apps", "cli", "alice.toml"), "[package]\nname = \"cli\"\n\n[dependencies]\ngeo = { workspace = true }\n");
        return root;
    }

    [Fact]
    public void Members_expand_globs_and_sort_dependencies_first()
    {
        var root = NewWorkspace();
        var ws = Workspace.Load(root);
        Assert.Equal(["geo", "cli", "web"], ws.Members.Select(m => m.Name));   // geo before its dependents
    }

    [Fact]
    public void Workspace_dependency_resolves_to_the_member_project()
    {
        var root = NewWorkspace();
        var proj = Manifest.Sync(Path.Combine(root, "apps", "web"));
        var xml = File.ReadAllText(proj).Replace('\\', '/');
        Assert.Contains("../../../geo/.alice/geo.alproj", xml);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.*\" />", xml);
    }

    [Fact]
    public void Unknown_workspace_dependency_is_an_error()
    {
        var root = NewWorkspace();
        File.WriteAllText(Path.Combine(root, "apps", "cli", "alice.toml"), "[package]\nname = \"cli\"\n\n[dependencies]\nnope = { workspace = true }\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Manifest.Sync(Path.Combine(root, "apps", "cli")));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Lockfile_pins_resolved_versions()
    {
        var root = NewWorkspace();
        Lockfile.Write(root, new Dictionary<string, string> { ["Newtonsoft.Json"] = "13.0.3" });
        Assert.Contains("\"Newtonsoft.Json\" = \"13.0.3\"", File.ReadAllText(Path.Combine(root, "alice.lock")));
        var xml = File.ReadAllText(Manifest.Sync(Path.Combine(root, "apps", "web")));
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"[13.0.3]\" />", xml);
    }

    [Fact]
    public void Lock_reads_resolved_versions_from_restore_assets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        File.WriteAllText(Path.Combine(dir, "obj", "project.assets.json"), "{\"libraries\":{\"Newtonsoft.Json/13.0.3\":{},\"Other/1.0.0\":{}}}");
        var resolved = Lockfile.FromAssets(Path.Combine(dir, "obj", "project.assets.json"), ["Newtonsoft.Json"]);
        Assert.Equal("13.0.3", resolved["Newtonsoft.Json"]);
        Assert.False(resolved.ContainsKey("Other"));
    }
}
