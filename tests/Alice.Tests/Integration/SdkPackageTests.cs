using System.Diagnostics;

namespace Alice.Tests.Integration;

/// <summary>Alice.Sdk from a NuGet feed: a plain .csproj with Sdk="Alice.Sdk/x" compiles .al files, nothing installed.</summary>
[Trait("Category", "Integration")]
public class SdkPackageTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static string Dotnet(string dir, string cache, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        psi.Environment["NUGET_PACKAGES"] = cache;   // fresh cache outside the repo: the package must be self-contained
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, o);
        return o;
    }

    [Fact]
    public void Csproj_with_alice_sdk_compiles_alice_and_csharp_together()
    {
        var work = Path.Combine(Path.GetTempPath(), "alice-sdkpkg-" + Guid.NewGuid().ToString("N"));
        var feed = Path.Combine(work, "feed");
        var app = Path.Combine(work, "app");
        Directory.CreateDirectory(app);
        var cache = Path.Combine(work, "nuget-cache");
        Dotnet(Root(), cache, "pack", Path.Combine(Root(), "src", "Alice.Sdk"), "-c", "Release", "-o", feed, "-nologo");
        string version = Path.GetFileNameWithoutExtension(Directory.GetFiles(feed, "Alice.Sdk.*.nupkg").Single())["Alice.Sdk.".Length..];

        File.WriteAllText(Path.Combine(app, "nuget.config"),
            $"<configuration><packageSources><clear /><add key=\"local\" value=\"{feed}\" /><add key=\"nuget\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(app, "App.csproj"), $"<Project Sdk=\"Alice.Sdk/{version}\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(app, "greet.al"), "module Greeter\n  def self.greet(who : String) : String\n    \"Hello, #{who}!\"\n  end\nend\n");
        File.WriteAllText(Path.Combine(app, "Entry.cs"), "public static class Entry { public static void Main() => System.Console.WriteLine(Greeter.greet(\"C#\")); }\n");
        Assert.Contains("Hello, C#!", Dotnet(app, cache, "run"));
    }
}
