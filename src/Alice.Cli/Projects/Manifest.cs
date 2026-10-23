using System.Text;

namespace Alice.Cli.Projects;

/// <summary>
/// alice.toml is the project manifest (cargo-style). MSBuild needs XML, so `alice` generates
/// `.alice/&lt;name&gt;.alproj` on every command; C# projects reference that generated file.
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
        var (pkgProps, pkgItems) = PackageMetadata.Render(dir, name, kind, pkg);
        props.Append(pkgProps);
        if (pkg.TryGetValue("version", out var v)) props.Append($"    <Version>{v}</Version>\n");
        if (pkg.TryGetValue("target", out var tf)) props.Append($"    <TargetFramework>{tf}</TargetFramework>\n");
        var build = toml.GetValueOrDefault("build") ?? new Dictionary<string, object>();
        if (build.TryGetValue("native", out var nat) && nat is true)
            props.Append("    <PublishAot>true</PublishAot>\n    <OptimizationPreference>Speed</OptimizationPreference>\n");

        var runtime = toml.GetValueOrDefault("runtime") ?? new Dictionary<string, object>();
        string gc = runtime.TryGetValue("gc", out var g) ? (string)g : "auto";
        var (server, concurrent, conserve) = gc switch
        {
            // decided by the compiler: server GC when the program forks or runs parallel loops (alice gen reports it)
            "auto" => ((bool?)null, true, (string?)null),
            // short-lived CLIs and benchmarks: one heap, fast startup, low RSS
            "workstation" => (false, true, null),
            // long-running, allocation-heavy parallel work: one heap per core (DATAS keeps it adaptive)
            "server" => (true, true, null),
            "low-memory" => (false, false, "7"),
            _ => throw new InvalidOperationException($"[runtime] gc = \"{gc}\" is not a GC mode (expected one of: auto, workstation, server, low-memory)"),
        };
        if (kind == "wasm")
            // the browser runtime has one GC mode; JS interop attributes are browser-only by design (CA1416)
            props.Append("    <NoWarn>$(NoWarn);CA1416</NoWarn>\n");
        else
        {
            if (server is null) props.Append("    <AliceGc>auto</AliceGc>\n");
            else props.Append($"    <ServerGarbageCollection>{(server == true ? "true" : "false")}</ServerGarbageCollection>\n");
            props.Append($"    <ConcurrentGarbageCollection>{(concurrent ? "true" : "false")}</ConcurrentGarbageCollection>\n");
        }
        // invariant: ICU is not loaded (2-4 MB less memory per process, measured); strings then sort and compare
        // ordinally (Ruby order: "A" < "B" < "a") instead of linguistically. Numbers already print invariantly either way
        string glob = runtime.TryGetValue("globalization", out var gl) ? (string)gl : "culture";
        if (glob is not ("culture" or "invariant"))
            throw new InvalidOperationException($"[runtime] globalization = \"{glob}\" is not a mode (expected one of: culture, invariant)");
        if (glob == "invariant") props.Append("    <InvariantGlobalization>true</InvariantGlobalization>\n");

        var items = new StringBuilder(pkgItems);
        if (conserve != null) items.Append($"    <RuntimeHostConfigurationOption Include=\"System.GC.ConserveMemory\" Value=\"{conserve}\" />\n");
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

        if (kind == "wasm")   // the package's wwwroot (index.html, main.js) is the site's root
            items.Append("    <Content Include=\"$(AliceSourceRoot)wwwroot/**\" Link=\"wwwroot/%(RecursiveDir)%(Filename)%(Extension)\" CopyToPublishDirectory=\"PreserveNewest\" />\n");

        string sdk = ProjectFiles.SdkDir.Replace('\\', '/');
        string netSdk = kind == "wasm" ? "Microsoft.NET.Sdk.WebAssembly" : "Microsoft.NET.Sdk";
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
              <Import Project="Sdk.props" Sdk="{netSdk}" />
              <Import Project="{sdk}/Alice.props" Condition="'$(AliceCliProject)' == ''" />
              <PropertyGroup>
            {props}  </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(AliceSourceRoot)src/**/*.cs" />
                <!-- UI markup: default item globs are off in .alproj, so XAML for Avalonia is listed here (inert elsewhere) -->
                <AvaloniaXaml Include="$(AliceSourceRoot)src/**/*.axaml" />
            {items}  </ItemGroup>
              <Import Project="{sdk}/Alice.targets" Condition="'$(AliceCliProject)' == ''" />
              <Import Project="Sdk.targets" Sdk="{netSdk}" />
            </Project>

            """;
        Directory.CreateDirectory(genDir);
        if (!File.Exists(proj) || File.ReadAllText(proj) != xml) File.WriteAllText(proj, xml);
        return proj;
    }

    /// <summary>[dependencies] entries in file order: (name, version or "path:dir").</summary>
    public static List<(string Name, string Spec)> Dependencies(string dir)
    {
        var r = new List<(string, string)>();
        bool inDeps = false;
        foreach (var raw in File.ReadAllLines(Path.Combine(dir, FileName)))
        {
            string l = raw.Trim();
            if (l.StartsWith('[')) { inDeps = l == "[dependencies]"; continue; }
            if (!inDeps || l.Length == 0 || l.StartsWith('#') || !l.Contains('=')) continue;
            string name = l[..l.IndexOf('=')].Trim().Trim('"');
            string val = l[(l.IndexOf('=') + 1)..].Trim();
            r.Add((name, val.StartsWith('{') && val.Contains("path") ? "path:" + val.Split('"')[1] : val.Trim('"')));
        }
        return r;
    }

    /// <summary>Removes a [dependencies] entry; false when it is not there.</summary>
    public static bool RemoveDependency(string dir, string name)
    {
        string file = Path.Combine(dir, FileName);
        var lines = File.ReadAllLines(file).ToList();
        bool inDeps = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string l = lines[i].Trim();
            if (l.StartsWith('[')) { inDeps = l == "[dependencies]"; continue; }
            if (inDeps && l.Contains('=') && l[..l.IndexOf('=')].Trim().Trim('"').Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                File.WriteAllLines(file, lines);
                return true;
            }
        }
        return false;
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
