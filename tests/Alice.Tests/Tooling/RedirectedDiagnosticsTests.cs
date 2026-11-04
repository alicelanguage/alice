using System.Diagnostics;
using Alice.Cli.Commands;
using Alice.Cli.Projects;
using Alice.Compiler;
using Spectre.Console;

namespace Alice.Tests.Tooling;

/// <summary>
/// Diagnostics reach a build log: with stdout redirected (MSBuild Exec, CI) the CLI writes them as plain text to
/// stderr, the summary repeats the first one, and an `alice gen` crash is a canonical MSBuild error line. On the
/// windows-2025 runner the web API sample failed with nothing but "could not compile due to 1 previous error(s)".
/// </summary>
[Collection("console")]
public class RedirectedDiagnosticsTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-redir-" + Guid.NewGuid().ToString("N"));

    public RedirectedDiagnosticsTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The CLI as a child process, stdout and stderr captured apart.</summary>
    (int Code, string Out, string Err) Child(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(typeof(Alice.Cli.App).Assembly.Location);
        psi.ArgumentList.Add("--lang");
        psi.ArgumentList.Add("en");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(TimeSpan.FromMinutes(5)), "alice did not exit");
        return (p.ExitCode, o.Result.Replace("\r", ""), e.Result.Replace("\r", ""));
    }

    [Fact]
    public void Redirected_check_writes_plain_diagnostics_with_code_and_location_to_stderr()
    {
        File.WriteAllText(Path.Combine(dir, "main.al"), "x = 1\nputs y\n");
        var (code, o, e) = Child("check", "main.al");
        Assert.Equal(1, code);
        var m = Alice.Core.Text.Regex.Cached(@"error\[(AL\d{4})\]: ").Match(e);
        Assert.True(m != null, $"stderr:\n{e}\nstdout:\n{o}");
        Assert.Contains("--> main.al:2:", e);
        Assert.False(e.Contains((char)27), e);               // plain text, no ANSI (ordinal: a culture compare ignores ESC)
        Assert.DoesNotContain("error[", o);                   // nothing on stdout
        // the summary names the first error, so a log that keeps one line still says what failed
        Assert.Contains($"error: could not compile due to 1 previous error(s): {m![1]} main.al:2:", e);
    }

    [Fact]
    public void Gen_crash_is_a_canonical_msbuild_error_line()
    {
        File.WriteAllText(Path.Combine(dir, "main.al"), "puts 1\n");
        var blocked = Path.Combine(dir, "taken");
        File.WriteAllText(blocked, "a file where gen wants its output directory");
        var (code, o, e) = Child("gen", "--no-check", "-o", blocked, "main.al");
        Assert.Equal(70, code);
        Assert.Contains("alice gen : error : internal error: System.IO.IOException", o);
    }

    [Fact]
    public void Build_report_shows_the_command_output_when_msbuild_only_says_exec_failed()
    {
        var exec = new Diagnostic("error", "MSB3073", "/p/Alice.targets", 85, 5, "The command \"dotnet alice.dll gen\" exited with code 70.");
        var r = new DotNet.Result(70, [exec], ["alice gen : error : internal error: System.IO.IOException: boom [/p/.alice/p.alproj]",
            "/p/Alice.targets(85,5): error MSB3073: The command \"dotnet alice.dll gen\" exited with code 70."]);
        var w = new StringWriter();
        var saved = AnsiConsole.Console;
        var savedErr = Console.Error;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(w), ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        Console.SetError(w);
        try { CompileCommands.Report(r); }
        finally { AnsiConsole.Console = saved; Console.SetError(savedErr); }
        var text = w.ToString();
        Assert.Contains("System.IO.IOException: boom", text);
        Assert.Contains("could not compile due to 1 previous error(s): MSB3073", text);
    }
}
