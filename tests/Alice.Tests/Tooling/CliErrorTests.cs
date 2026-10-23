using Spectre.Console;

namespace Alice.Tests.Tooling;

/// <summary>Every CLI mistake ends in a short error + hint and exit code 2 (usage) or 1 (failure); never a stack trace.</summary>
[Collection("console")]
public class CliErrorTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-cli-" + Guid.NewGuid().ToString("N"));
    readonly string cwd = Directory.GetCurrentDirectory();

    public CliErrorTests()
    {
        Directory.CreateDirectory(dir);
        Directory.SetCurrentDirectory(dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(cwd);
        // Reflection-loaded assemblies remain locked until process exit on Windows.
        try { Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static (int Code, string Out) Cli(params string[] args)
    {
        var w = new StringWriter();
        var saved = AnsiConsole.Console;
        var savedErr = Console.Error;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w), ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        Console.SetError(w);
        try { return (Alice.Cli.App.Run(["--lang", "en", .. args]), w.ToString()); }
        finally { AnsiConsole.Console = saved; Console.SetError(savedErr); }
    }

    [Theory]
    [InlineData(new[] { "run", "nope.al" }, 1, "file not found: nope.al")]
    [InlineData(new[] { "run", "-o" }, 2, "-o needs a value")]
    [InlineData(new[] { "biuld" }, 2, "did you mean 'build'")]
    [InlineData(new[] { "tset" }, 2, "did you mean 'test'")]
    [InlineData(new[] { "new" }, 2, "usage: alice new <name>")]
    [InlineData(new[] { "add" }, 2, "usage: alice add <package>")]
    [InlineData(new[] { "remove" }, 2, "usage: alice remove <package>")]
    [InlineData(new[] { "search" }, 2, "usage: alice search <term>")]
    [InlineData(new[] { "run" }, 2, "no alice.toml here")]
    [InlineData(new[] { "build" }, 2, "no alice.toml here")]
    [InlineData(new[] { "add", "Newtonsoft.Json" }, 2, "no alice.toml here")]
    [InlineData(new[] { "list" }, 2, "no alice.toml here")]
    [InlineData(new[] { "test" }, 2, "no tests found")]
    [InlineData(new[] { "clean", "missing/dir" }, 1, "directory not found: missing/dir")]
    public void Mistakes_explain_themselves(string[] args, int code, string message)
    {
        var (c, o) = Cli(args);
        Assert.Equal(code, c);
        Assert.Contains(message, o);
        Assert.DoesNotContain("   at ", o);                  // no stack traces
        Assert.DoesNotContain("could not compile", o);       // usage errors are not compile errors
        Assert.DoesNotContain(".alproj", o);                 // internal project files stay internal
    }

    [Fact]
    public void Gen_keeps_framework_pack_references()
    {
        string pack = Path.Combine(dir, "packs", "Example.Framework.Ref", "1.0", "ref", "net10.0");
        Directory.CreateDirectory(pack);
        string reference = Path.Combine(pack, "Example.Framework.dll");
        File.WriteAllBytes(reference, Alice.Compiler.AliceCompiler.Emit(
            ["namespace Example.Framework; public static class Api { public static int Answer() => 42; }"],
            "Example.Framework", exe: false));
        File.WriteAllText("refs.txt", reference);
        File.WriteAllText("main.al", "using Example.Framework\nputs Api.Answer()\n");

        Assert.Equal(0, Cli("gen", "-o", "generated", "--refs-file", "refs.txt", "main.al").Code);
        Assert.True(File.Exists(Path.Combine("generated", "main.al.g.cs")));
    }

    [Fact]
    public void Missing_file_suggests_a_close_name()
    {
        File.WriteAllText("hello.al", "puts 1\n");
        var (c, o) = Cli("run", "helo.al");
        Assert.Equal(1, c);
        Assert.Contains("did you mean 'hello.al'", o);
    }

    [Fact]
    public void Existing_directory_is_not_overwritten_by_new()
    {
        Directory.CreateDirectory("app");
        File.WriteAllText("app/keep.txt", "x");
        var (c, o) = Cli("new", "app");
        Assert.Equal(1, c);
        Assert.Contains("already exists", o);
        Assert.True(File.Exists("app/keep.txt"));
    }

    [Fact]
    public void New_project_gets_AGENTS_md_and_a_CLAUDE_md_that_mirrors_it()
    {
        Assert.Equal(0, Cli("new", "agentsdemo").Code);
        var agents = File.ReadAllText("agentsdemo/AGENTS.md");
        Assert.Contains("alice guide", agents);
        Assert.Contains(Alice.Cli.Commands.GuideCommand.Start, agents);
        Assert.Equal("@AGENTS.md\n", File.ReadAllText("agentsdemo/CLAUDE.md"));   // Claude Code imports AGENTS.md: one source, no drift
    }

    [Fact]
    public void Remove_and_list_edit_the_manifest()
    {
        Assert.Equal(0, Cli("new", "demo").Code);
        Directory.SetCurrentDirectory("demo");
        Alice.Cli.Projects.Manifest.AddDependency(".", "Humanizer.Core", "2.14.1", null);
        var (lc, lo) = Cli("list");
        Assert.Equal(0, lc);
        Assert.Contains("Humanizer.Core", lo);
        Assert.Contains("2.14.1", lo);
        var (rc, ro) = Cli("remove", "Humanizer.Core", "--no-restore");
        Assert.Equal(0, rc);
        Assert.DoesNotContain("Humanizer.Core", File.ReadAllText("alice.toml"));
        var (mc, mo) = Cli("remove", "Nope.Package", "--no-restore");
        Assert.Equal(1, mc);
        Assert.Contains("not a dependency: Nope.Package", mo);
    }

    [Fact]
    public void New_lib_and_tool_are_ready_to_publish()
    {
        Assert.Equal(0, Cli("new", "geo", "--lib").Code);
        string lib = File.ReadAllText("geo/alice.toml");
        Assert.Contains("naming = \"dotnet\"", lib);   // C#/F# callers see a .NET-style API (ADR 0019)
        Assert.Contains("description = ", lib);
        Assert.Equal(0, Cli("new", "wand", "--tool").Code);
        string tool = File.ReadAllText("wand/alice.toml");
        Assert.Contains("kind = \"tool\"", tool);
        Assert.Contains("command = \"wand\"", tool);
        Assert.Contains("ARGV", File.ReadAllText("wand/src/main.al"));
    }
}
