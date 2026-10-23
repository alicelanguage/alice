using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Alice.Cli.Projects;

/// <summary>
/// [package] fields that make a project a NuGet package (`alice pack` / `alice publish`): id, metadata, and
/// `kind = "tool"` for .NET tools installed with `dotnet tool install` (CLI apps, framework generators).
/// </summary>
public static class PackageMetadata
{
    public static readonly string[] Kinds = ["exe", "lib", "tool", "game", "wasm"];

    static readonly Regex Id = new(@"^[A-Za-z0-9_]+([.-][A-Za-z0-9_]+)*$");
    static readonly Regex SemVer = new(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$");

    static readonly (string Key, string Property)[] Plain =
    [
        ("description", "Description"), ("license", "PackageLicenseExpression"), ("repository", "RepositoryUrl"),
        ("homepage", "PackageProjectUrl"), ("copyright", "Copyright"), ("icon", "PackageIcon"),
    ];

    /// <summary>MSBuild properties for the package; validates fields so mistakes fail before `dotnet pack`.</summary>
    public static (string Props, string Items) Render(string dir, string name, string kind, Dictionary<string, object> pkg)
    {
        if (!Kinds.Contains(kind))
            throw new InvalidOperationException($"[package] kind = \"{kind}\" is not a kind (expected one of: {string.Join(", ", Kinds)})");
        string id = pkg.TryGetValue("id", out var i) ? (string)i : name;
        if (!Id.IsMatch(id)) throw new InvalidOperationException($"[package] id \"{id}\" is not a valid NuGet package id (letters, digits, '.', '-', '_')");
        if (pkg.TryGetValue("version", out var v) && !SemVer.IsMatch(v.ToString()!))
            throw new InvalidOperationException($"[package] version \"{v}\" is not a valid version (expected e.g. 1.2.3 or 1.2.3-beta.1)");

        var p = new StringBuilder();
        void Prop(string k, string val) => p.Append($"    <{k}>{SecurityElement.Escape(val)}</{k}>\n");
        Prop("PackageId", id);
        Prop("IsPackable", kind == "game" ? "false" : "true");
        foreach (var (key, prop) in Plain)
            if (pkg.TryGetValue(key, out var val)) Prop(prop, val.ToString()!);
        if (pkg.TryGetValue("authors", out var a)) Prop("Authors", Join(a));
        var tags = pkg.TryGetValue("tags", out var t) ? Join(t) : "";
        Prop("PackageTags", tags.Length == 0 ? "alice" : tags + ";alice");
        string naming = pkg.TryGetValue("naming", out var nm) ? (string)nm : "alice";
        if (naming is not ("alice" or "dotnet"))
            throw new InvalidOperationException($"[package] naming = \"{naming}\" is not a naming (expected one of: alice, dotnet)");
        Prop("AliceNaming", naming);
        if (kind == "tool")
        {
            Prop("PackAsTool", "true");
            Prop("ToolCommandName", pkg.TryGetValue("command", out var c) ? (string)c : name);
        }

        var items = new StringBuilder();
        string? readme = pkg.TryGetValue("readme", out var r) ? (string)r : File.Exists(Path.Combine(dir, "README.md")) ? "README.md" : null;
        if (readme != null)
        {
            Prop("PackageReadmeFile", Path.GetFileName(readme));
            items.Append($"    <None Include=\"$(AliceSourceRoot){SecurityElement.Escape(readme)}\" Pack=\"true\" PackagePath=\"/\" />\n");
        }
        if (pkg.TryGetValue("icon", out var ic))
            items.Append($"    <None Include=\"$(AliceSourceRoot){SecurityElement.Escape((string)ic)}\" Pack=\"true\" PackagePath=\"/\" />\n");
        return (p.ToString(), items.ToString());
    }

    static string Join(object v) => v is List<object> l ? string.Join(";", l) : v.ToString()!.Replace(',', ';').Replace(" ", "");
}
