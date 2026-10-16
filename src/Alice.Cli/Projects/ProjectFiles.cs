namespace Alice.Cli.Projects;

/// <summary>Alice projects are SDK-style MSBuild projects with the .alproj extension.</summary>
public static class ProjectFiles
{
    public static string SdkDir => Path.Combine(AppContext.BaseDirectory, "sdk");

    public static string? Find(string dir)
    {
        if (File.Exists(dir) && dir.EndsWith(".alproj")) return Path.GetFullPath(dir);
        if (File.Exists(dir) && Path.GetFileName(dir) == Manifest.FileName) dir = Path.GetDirectoryName(Path.GetFullPath(dir))!;
        if (!Directory.Exists(dir)) return null;
        if (File.Exists(Path.Combine(dir, Manifest.FileName))) return Manifest.Sync(dir);
        return Directory.GetFiles(dir, "*.alproj").FirstOrDefault();
    }

    public static string ProjectXml(string kind)
    {
        string sdk = SdkDir.Replace('\\', '/');
        string output = kind == "lib" ? "Library" : "Exe";
        string packages = kind == "game"
            ? "\n  <ItemGroup>\n    <PackageReference Include=\"MonoGame.Framework.DesktopGL\" Version=\"3.8.*\" />\n  </ItemGroup>"
            : "";
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{sdk}/Alice.props" />
              <PropertyGroup>
                <OutputType>{output}</OutputType>
              </PropertyGroup>{packages}
              <Import Project="{sdk}/Alice.targets" />
            </Project>

            """;
    }

    public static string MainSource(string name, string kind) => kind switch
    {
        "lib" => $$"""
            # {{name}}: a library usable from Alice and C#
            module {{Pascal(name)}}
              def self.greet(who : String) : String
                "Hello, #{who}!"
              end
            end

            """,
        "game" => Templates.Game(Pascal(name)),
        _ => $$"""
            # {{name}}
            def greet(who : String)
              "Hello, #{who}!"
            end

            puts greet("Alice")
            squares = (1..10).to_a.map { |i| i * i }
            puts "sum of squares: #{squares.sum}"

            """,
    };

    public static string TestSource(string name) => """
        # run with: alice test
        def test_greet
          assert_equal "Hello, Bob!", "Hello, #{"Bob"}!"
        end

        def test_math
          assert 2 + 2 == 4, "math works"
        end

        """;

    public static string Pascal(string name)
    {
        var parts = name.Split('-', '_', '.', ' ').Where(p => p.Length > 0);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
    }

    public static void NewSolution(string path)
    {
        if (path.EndsWith(".slnx")) File.WriteAllText(path, "<Solution>\n</Solution>\n");
        else File.WriteAllText(path, "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\nGlobal\r\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n\t\tDebug|Any CPU = Debug|Any CPU\r\n\t\tRelease|Any CPU = Release|Any CPU\r\n\tEndGlobalSection\r\nEndGlobal\r\n");
    }

    /// <summary>.slnx: `.alproj` needs an explicit Type (the solution model only knows built-in extensions).</summary>
    static void AddToSlnx(string slnx, string project)
    {
        var doc = System.Xml.Linq.XDocument.Load(slnx);
        string rel = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(slnx))!, Path.GetFullPath(project)).Replace('\\', '/');
        var root = doc.Root!;
        if (root.Descendants("Project").Any(p => (string?)p.Attribute("Path") == rel)) return;
        var e = new System.Xml.Linq.XElement("Project", new System.Xml.Linq.XAttribute("Path", rel));
        if (project.EndsWith(".alproj")) e.Add(new System.Xml.Linq.XAttribute("Type", "C#"));
        root.Add(e);
        doc.Save(slnx);
    }

    /// <summary>`dotnet sln add` rejects unknown extensions; the solution format itself accepts any SDK project.</summary>
    public static void AddToSolution(string sln, string project)
    {
        if (sln.EndsWith(".slnx")) { AddToSlnx(sln, project); return; }
        var text = File.ReadAllText(sln);
        string rel = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(sln))!, Path.GetFullPath(project)).Replace('/', '\\');
        if (text.Contains(rel)) return;
        string name = Path.GetFileNameWithoutExtension(project);
        string guid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        string entry = $"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = \"{name}\", \"{rel}\", \"{guid}\"\r\nEndProject\r\n";
        int at = text.IndexOf("Global", StringComparison.Ordinal);
        text = at < 0 ? text + entry : text.Insert(at, entry);
        // every solution configuration must map to the project, or `dotnet build x.sln` silently skips it
        var configs = System.Text.RegularExpressions.Regex.Matches(text, @"^\s*(?<c>[^=\r\n]+?) = \k<c>\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups["c"].Value.Trim()).Where(c => c.Contains('|')).Distinct().ToList();
        if (configs.Count == 0) configs = ["Debug|Any CPU", "Release|Any CPU"];
        var map = new System.Text.StringBuilder();
        foreach (var c in configs)
        {
            string cfg = c.Split('|')[0] + "|Any CPU";
            map.Append($"\t\t{guid}.{c}.ActiveCfg = {cfg}\r\n\t\t{guid}.{c}.Build.0 = {cfg}\r\n");
        }
        const string section = "GlobalSection(ProjectConfigurationPlatforms) = postSolution";
        int sec = text.IndexOf(section, StringComparison.Ordinal);
        if (sec >= 0)
        {
            int lineEnd = text.IndexOf('\n', sec) + 1;
            text = text.Insert(lineEnd, map.ToString());
        }
        else
        {
            int g = text.IndexOf("EndGlobal", StringComparison.Ordinal);
            if (g >= 0) text = text.Insert(g, "\t" + section + "\r\n" + map + "\tEndGlobalSection\r\n");
        }
        File.WriteAllText(sln, text);
    }
}
