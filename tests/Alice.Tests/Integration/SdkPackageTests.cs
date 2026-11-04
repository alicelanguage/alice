using System.Diagnostics;

namespace Alice.Tests.Integration;

/// <summary>Alice.Sdk from a NuGet feed: a plain .csproj with Sdk="Alice.Sdk/x" compiles .al files, nothing installed.</summary>
[Trait("Category", "Integration")]
[Collection("msbuild")]   // MSBuild runs on the same repository projects (obj/) must not overlap
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
        var (code, o) = DotnetRun(dir, cache, args);
        Assert.True(code == 0, o);
        return o;
    }

    static (int Code, string Output) DotnetRun(string dir, string cache, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        psi.Environment["NUGET_PACKAGES"] = cache;   // fresh cache outside the repo: the package must be self-contained
        return ChildProcess.Run(psi);   // async reads, no build servers: the suite never hangs on a pipe
    }

    [Fact]
    public void Csproj_with_alice_sdk_compiles_alice_and_csharp_together()
    {
        var work = Path.Combine(Path.GetTempPath(), "alice-sdkpkg-" + Guid.NewGuid().ToString("N"));
        try { Run(work); }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }   // the NuGet cache alone is ~175 MB
    }

    static void Run(string work)
    {
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

        // null safety in a package build comes from the binder (AL0310), as in the CLI: Roslyn's CS86xx are no
        // longer build errors there (D1.12)
        var bad = Path.Combine(work, "bad");
        Directory.CreateDirectory(bad);
        File.Copy(Path.Combine(app, "nuget.config"), Path.Combine(bad, "nuget.config"));
        File.WriteAllText(Path.Combine(bad, "Bad.csproj"), $"<Project Sdk=\"Alice.Sdk/{version}\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(bad, "main.al"), "def pick(f : Bool) : String?\n  f ? \"a\" : nil\nend\ns = pick(false)\nputs s.size\n");
        var (code, output) = DotnetRun(bad, cache, "build", "-nologo");
        Assert.NotEqual(0, code);
        Assert.Contains("AL0310", output);
        Assert.Contains("main.al(", output);  // Compiler error shows AL source location
        Assert.DoesNotContain("CS8602", output);
    }

    // ---- which alice.dll Alice.targets runs (the windows-2025 runner built Release only; the samples asked for Debug) ----

    static (int Code, string Output) MsBuild(string dir, Dictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return ChildProcess.Run(psi);
    }

    static string Project(string dir, string properties)
    {
        var targets = Path.Combine(Root(), "src", "Alice.Sdk", "Alice.targets");
        var proj = Path.Combine(dir, "App.csproj");
        File.WriteAllText(proj, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>{properties}</PropertyGroup><Import Project=\"{targets}\" /></Project>");
        return proj;
    }

    static string Compiler(string dir, string proj, string configuration, Dictionary<string, string> env)
    {
        var (code, o) = MsBuild(dir, env, "msbuild", proj, "-nologo", $"-p:Configuration={configuration}", "-getProperty:AliceCompiler");
        Assert.True(code == 0, o);
        return Path.GetFullPath(o.Trim());
    }

    [Fact]
    public void Repo_layout_resolves_the_compiler_that_exists()
    {
        var work = Path.Combine(Path.GetTempPath(), "alice-sdkcli-" + Guid.NewGuid().ToString("N"));
        try
        {
            // a src/Alice.Cli stand-in built in Debug only
            var cli = Path.Combine(work, "cli");
            var debug = Path.Combine(cli, "bin", "Debug", "net10.0", "alice.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(debug)!);
            File.WriteAllText(debug, "");
            var running = Path.Combine(work, "running", "alice.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(running)!);
            File.WriteAllText(running, "");
            var app = Path.Combine(work, "app");
            Directory.CreateDirectory(app);
            var proj = Project(app, $"<AliceCliProject>{Path.Combine(cli, "Alice.Cli.csproj")}</AliceCliProject>");
            var release = Path.GetFullPath(Path.Combine(cli, "bin", "Release", "net10.0", "alice.dll"));

            // `alice build`: the alice.dll that drives the build, whatever was built under src/Alice.Cli
            Assert.Equal(Path.GetFullPath(running), Compiler(app, proj, "Release", new() { ["AliceFromCli"] = "true", ["AliceRunningCompiler"] = running }));
            // `alice build` from a CLI that does not say which it is: the configuration that exists
            Assert.Equal(Path.GetFullPath(debug), Compiler(app, proj, "Release", new() { ["AliceFromCli"] = "true" }));
            // plain `dotnet build`: the ProjectReference builds bin/$(Configuration) first
            Assert.Equal(release, Compiler(app, proj, "Release", new()));
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    [Fact]
    public void Missing_compiler_fails_with_the_paths_tried()
    {
        var work = Path.Combine(Path.GetTempPath(), "alice-sdknocli-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var home = Path.Combine(work, "nohome") + Path.DirectorySeparatorChar;
            var proj = Project(work, $"<AliceHome>{home}</AliceHome><OutputType>Exe</OutputType>");
            File.WriteAllText(Path.Combine(work, "main.al"), "puts 1\n");
            var (code, o) = MsBuild(work, new(), "build", proj, "-nologo");
            Assert.NotEqual(0, code);
            Assert.Contains("Alice compiler not found", o);
            Assert.Contains(home + "alice.dll", o);
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }
}
