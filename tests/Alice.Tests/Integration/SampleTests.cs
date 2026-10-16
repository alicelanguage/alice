using System.Diagnostics;

namespace Alice.Tests.Integration;

/// <summary>Two-way interop through MSBuild: C# consumes an Alice project and Alice consumes a C# project.</summary>
[Trait("Category", "Integration")]
public class SampleTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static string Dotnet(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.Combine(Root(), "samples") };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, o);
        return o;
    }

    [Fact]
    public void Interop_samples_build_and_run()
    {
        Dotnet("build", "CSharpApp/CSharpApp.csproj");
        Dotnet("build", "AliceApp/AliceApp.alproj");
        var cs = Dotnet("run", "--no-build", "--project", "CSharpApp/CSharpApp.csproj");
        Assert.Contains("distance=5", cs);
        Assert.Contains("pattern-matched an Alice sum type from C#", cs);
        var al = Dotnet("run", "--no-build", "--project", "AliceApp/AliceApp.alproj");
        Assert.Contains("median from C#: 3", al);
        Assert.Contains("C# counter: 45", al);
        Assert.Contains("distance: 10", al);
    }
}
