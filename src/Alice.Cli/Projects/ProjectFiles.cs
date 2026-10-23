namespace Alice.Cli.Projects;

/// <summary>Alice projects are SDK-style MSBuild projects with the .alproj extension.</summary>
public static class ProjectFiles
{
    public static string SdkDir => Path.Combine(AppContext.BaseDirectory, "sdk");

    /// <summary>The package directory of a generated project (.alice/name.alproj sits inside it).</summary>
    public static string PackageDir(string proj) => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(proj))!)!;

    /// <summary>`kind` from the package's alice.toml ("exe" when there is none).</summary>
    public static string Kind(string proj)
    {
        string toml = Path.Combine(PackageDir(proj), Manifest.FileName);
        if (!File.Exists(toml)) return "exe";
        var pkg = TomlLite.Parse(File.ReadAllText(toml)).GetValueOrDefault("package");
        return pkg != null && pkg.TryGetValue("kind", out var k) ? (string)k : "exe";
    }

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
        "tool" => $$"""
            # {{name}}: a command-line tool. alice pack, then dotnet tool install -g --add-source bin/package {{name}}
            if ARGV.size == 0 || ARGV[0] == "--help"
              puts "usage: {{name}} <name>"
              exit 0
            end
            puts "Hello, #{ARGV[0]}!"

            """,
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
}
