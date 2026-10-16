using System.Text;

namespace Alice.Cli.Projects;

/// <summary>
/// alice.toml is the project manifest (cargo-style). MSBuild needs XML, so `alice` generates
/// `&lt;name&gt;.alproj` next to it on every command; solutions and C# projects reference that file.
/// </summary>
public static class Manifest
{
    public const string FileName = "alice.toml";

    public static string Sync(string dir) => Sync(Path.GetFullPath(dir), new HashSet<string>());

    static string Sync(string dir, HashSet<string> visiting)
    {
        var toml = TomlLite.Parse(File.ReadAllText(Path.Combine(dir, FileName)));
        var pkg = toml.GetValueOrDefault("package") ?? new Dictionary<string, object>();
        string name = pkg.TryGetValue("name", out var n) ? (string)n : Path.GetFileName(dir);
        string kind = pkg.TryGetValue("kind", out var k) ? (string)k : "exe";
        string genDir = Path.Combine(dir, ".alice");
        string proj = Path.Combine(genDir, name + ".alproj");
        if (!visiting.Add(dir)) return proj;
        var locked = Lockfile.Read(dir);
        Workspace? ws = null;

        var props = new StringBuilder();
        props.Append($"    <OutputType>{(kind == "lib" ? "Library" : "Exe")}</OutputType>\n");
        if (pkg.TryGetValue("version", out var v)) props.Append($"    <Version>{v}</Version>\n");
        if (pkg.TryGetValue("target", out var tf)) props.Append($"    <TargetFramework>{tf}</TargetFramework>\n");
        var build = toml.GetValueOrDefault("build") ?? new Dictionary<string, object>();
        if (build.TryGetValue("native", out var nat) && nat is true)
            props.Append("    <PublishAot>true</PublishAot>\n    <OptimizationPreference>Speed</OptimizationPreference>\n");

        var items = new StringBuilder();
        foreach (var (dep, spec) in toml.GetValueOrDefault("dependencies") ?? new Dictionary<string, object>())
        {
            if (spec is string version)
            {
                string pinned = locked.TryGetValue(dep, out var lv) ? $"[{lv}]" : version;
                items.Append($"    <PackageReference Include=\"{dep}\" Version=\"{pinned}\" />\n");
                continue;
            }
            if (spec is not Dictionary<string, object> t) continue;
            string target;
            if (t.TryGetValue("workspace", out var w) && w is true)
            {
                var root = Workspace.FindRoot(dir) ?? throw new InvalidOperationException($"{dep} = {{ workspace = true }} but {dir} is not inside a workspace");
                ws ??= Workspace.Load(root);
                var member = ws.Members.FirstOrDefault(m => m.Name == dep) ?? throw new InvalidOperationException($"workspace has no member named '{dep}' (members: {string.Join(", ", ws.Members.Select(m => m.Name))})");
                target = member.Dir;
            }
            else if (t.TryGetValue("path", out var p)) target = Path.GetFullPath(Path.Combine(dir, (string)p));
            else continue;
            string reference;
            if (Directory.Exists(target) && File.Exists(Path.Combine(target, FileName))) reference = Sync(target, visiting);
            else if (Directory.Exists(target)) reference = Directory.GetFiles(target, "*.*proj").FirstOrDefault() ?? target;
            else reference = target;
            items.Append($"    <ProjectReference Include=\"{Path.GetRelativePath(genDir, reference).Replace('\\', '/')}\" />\n");
        }
        if (kind == "game" && !items.ToString().Contains("MonoGame"))
            items.Append("    <PackageReference Include=\"MonoGame.Framework.DesktopGL\" Version=\"3.8.*\" />\n");

        string sdk = ProjectFiles.SdkDir.Replace('\\', '/');
        // Hidden MSBuild project: sources and bin/obj stay in the package directory, nothing to hand-edit.
        string xml = $"""
            <!-- generated from alice.toml by alice: edit alice.toml instead -->
            <Project>
              <PropertyGroup>
                <AliceSourceRoot>$(MSBuildThisFileDirectory)../</AliceSourceRoot>
                <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)../obj/</BaseIntermediateOutputPath>
                <BaseOutputPath>$(MSBuildThisFileDirectory)../bin/</BaseOutputPath>
                <AssemblyName>{name}</AssemblyName>
                <RootNamespace>{name}</RootNamespace>
              </PropertyGroup>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <Import Project="{sdk}/Alice.props" Condition="'$(AliceCliProject)' == ''" />
              <PropertyGroup>
            {props}  </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(AliceSourceRoot)src/**/*.cs" />
            {items}  </ItemGroup>
              <Import Project="{sdk}/Alice.targets" Condition="'$(AliceCliProject)' == ''" />
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
            </Project>

            """;
        Directory.CreateDirectory(genDir);
        if (!File.Exists(proj) || File.ReadAllText(proj) != xml) File.WriteAllText(proj, xml);
        return proj;
    }

    /// <summary>Adds or updates `name = "version"` / `name = { path = "..." }` under [dependencies].</summary>
    public static void AddDependency(string dir, string name, string? version, string? path = null)
    {
        string file = Path.Combine(dir, FileName);
        var lines = File.ReadAllLines(file).ToList();
        string key = name.Contains('.') || name.Contains('-') ? $"\"{name}\"" : name;
        string entry = path != null ? $"{key} = {{ path = \"{path}\" }}" : $"{key} = \"{version ?? "*"}\"";
        int sec = lines.FindIndex(l => l.Trim() == "[dependencies]");
        if (sec < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[dependencies]");
            lines.Add(entry);
        }
        else
        {
            int end = sec + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            int existing = lines.FindIndex(sec + 1, end - sec - 1, l => l.TrimStart().StartsWith(key + " ") || l.TrimStart().StartsWith(key + "="));
            if (existing >= 0) lines[existing] = entry;
            else
            {
                int at = end;
                while (at > sec + 1 && lines[at - 1].Trim().Length == 0) at--;
                lines.Insert(at, entry);
            }
        }
        File.WriteAllLines(file, lines);
    }
}
