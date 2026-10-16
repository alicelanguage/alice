using System.Xml.Linq;

namespace Alice.Tests.Architecture;

/// <summary>Executable layering rules: Runtime &lt;- Compiler &lt;- Cli. The runtime never depends on the compiler.</summary>
public class DependencyTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static List<string> ProjectRefs(string project) =>
        XDocument.Load(Path.Combine(Root(), "src", project, project + ".csproj"))
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToList();

    [Theory]
    [InlineData("Alice.Runtime", new string[0])]
    [InlineData("Alice.Compiler", new[] { "Alice.Runtime" })]
    [InlineData("Alice.Cli", new[] { "Alice.Compiler" })]
    public void Project_references_follow_layering(string project, string[] allowed)
        => Assert.All(ProjectRefs(project), r => Assert.Contains(r, allowed));

    [Fact]
    public void Runtime_has_no_package_dependencies()
    {
        var doc = XDocument.Load(Path.Combine(Root(), "src", "Alice.Runtime", "Alice.Runtime.csproj"));
        Assert.Empty(doc.Descendants("PackageReference"));
    }
}
