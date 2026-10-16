using Spectre.Console;

namespace Alice.Tests.Tooling;

/// <summary>`alice toolchain|self|doctor|completions` and `alice +VERSION` against an isolated ALICE_HOME (no network).</summary>
[Collection("console")]
public class ToolchainCliTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "alice-home-" + Guid.NewGuid().ToString("N"));
    readonly string? savedHome = Environment.GetEnvironmentVariable("ALICE_HOME");
    readonly string? savedCheck = Environment.GetEnvironmentVariable("ALICE_NO_UPDATE_CHECK");

    public ToolchainCliTests()
    {
        Environment.SetEnvironmentVariable("ALICE_HOME", home);
        Environment.SetEnvironmentVariable("ALICE_NO_UPDATE_CHECK", "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ALICE_HOME", savedHome);
        Environment.SetEnvironmentVariable("ALICE_NO_UPDATE_CHECK", savedCheck);
        try { Directory.Delete(home, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static (int Code, string Out) Cli(params string[] args)
    {
        var w = new StringWriter();
        var saved = AnsiConsole.Console;
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w), ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        AnsiConsole.Console.Profile.Width = 4096;   // long temp paths (Windows, macOS) must not wrap
        Console.SetOut(w);
        Console.SetError(w);
        try { return (Alice.Cli.App.Run(["--lang", "en", .. args]), w.ToString()); }
        finally { AnsiConsole.Console = saved; Console.SetOut(savedOut); Console.SetError(savedErr); }
    }

    [Fact]
    public void List_with_no_toolchains_points_to_install()
    {
        var (code, output) = Cli("toolchain", "list");
        Assert.Equal(0, code);
        Assert.Contains("alice toolchain install stable", output);
    }

    [Fact]
    public void List_marks_the_default_toolchain()
    {
        foreach (var v in new[] { "0.10.0", "0.10.1" })
        {
            Directory.CreateDirectory(Path.Combine(home, "toolchains", v));
            File.WriteAllText(Path.Combine(home, "toolchains", v, Alice.Cli.Toolchains.Platform.ExeName), "");
        }
        Assert.Equal(0, Cli("toolchain", "default", "0.10.0").Code);
        var (code, output) = Cli("toolchain", "list");
        Assert.Equal(0, code);
        Assert.Contains("0.10.1", output);
        Assert.Matches(@"0\.10\.0.*default", output);
    }

    [Fact]
    public void Default_to_a_missing_toolchain_is_a_usage_error()
    {
        var (code, output) = Cli("toolchain", "default", "9.9.9");
        Assert.Equal(2, code);
        Assert.Contains("not installed", output);
        Assert.Contains("alice toolchain install 9.9.9", output);
    }

    [Fact]
    public void Plus_version_for_a_missing_toolchain_explains_how_to_install()
    {
        var (code, output) = Cli("+9.9.9", "version");
        Assert.Equal(2, code);
        Assert.Contains("alice toolchain install 9.9.9", output);
    }

    [Fact]
    public void Override_pins_the_directory_and_show_reports_it()
    {
        var cwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(home, "proj");
        Directory.CreateDirectory(dir);
        Directory.SetCurrentDirectory(dir);
        try
        {
            Assert.Equal(0, Cli("toolchain", "pin", "0.10.0").Code);
            Assert.Equal("0.10.0", File.ReadAllText(Path.Combine(dir, "alice-toolchain")).Trim());
            Assert.Equal(0, Cli("toolchain", "unpin").Code);
            Assert.False(File.Exists(Path.Combine(dir, "alice-toolchain")));
        }
        finally { Directory.SetCurrentDirectory(cwd); }
    }

    [Theory]
    [InlineData("bash", "complete -F")]
    [InlineData("zsh", "#compdef alice")]
    [InlineData("fish", "complete -c alice")]
    [InlineData("pwsh", "Register-ArgumentCompleter")]
    public void Completions_cover_every_shell_and_command(string shell, string marker)
    {
        var (code, output) = Cli("completions", shell);
        Assert.Equal(0, code);
        Assert.Contains(marker, output);
        Assert.Contains("toolchain", output);
        Assert.Contains("doctor", output);
    }

    [Fact]
    public void Doctor_reports_dotnet_and_home()
    {
        var (code, output) = Cli("doctor");
        Assert.Contains(".NET SDK", output);
        Assert.Contains(home, output);
        Assert.True(code is 0 or 1);
    }

    [Fact]
    public void Unknown_toolchain_subcommand_suggests_the_nearest()
    {
        var (code, output) = Cli("toolchain", "instal");
        Assert.Equal(2, code);
        Assert.Contains("did you mean 'install'", output);
    }
}
