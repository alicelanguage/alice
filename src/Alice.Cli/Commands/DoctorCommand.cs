using System.Diagnostics;
using Alice.Cli.Toolchains;
using Spectre.Console;

namespace Alice.Cli.Commands;

/// <summary>`alice doctor`: checks everything Alice needs and says how to fix what is missing.</summary>
public static class DoctorCommand
{
    public static int Run()
    {
        var home = ToolchainHome.Current();
        int problems = 0;
        void Ok(string what, string detail) => AnsiConsole.MarkupLine($"  [green]✓[/] {what,-14} [grey]{Markup.Escape(detail)}[/]");
        void Warn(string what, string detail, string fix) { AnsiConsole.MarkupLine($"  [yellow]![/] {what,-14} {Markup.Escape(detail)}\n    [cyan]fix[/]: {Markup.Escape(fix)}"); }
        void Bad(string what, string detail, string fix) { problems++; AnsiConsole.MarkupLine($"  [red]✗[/] {what,-14} {Markup.Escape(detail)}\n    [cyan]fix[/]: {Markup.Escape(fix)}"); }

        AnsiConsole.MarkupLine($"[bold magenta]alice doctor[/] [grey]{App.Version} · {Platform.CurrentRid()}[/]\n");
        Ok("alice", Environment.ProcessPath ?? "alice");

        var sdks = Capture("dotnet", "--list-sdks");
        var ten = sdks?.Split('\n').Select(l => l.Split(' ')[0]).Where(v => v.StartsWith("10.")).LastOrDefault();
        if (sdks == null) Bad(".NET SDK", "dotnet not found", "rerun the install script (it installs .NET 10), or https://dot.net/download");
        else if (ten == null) Bad(".NET SDK", "no 10.x SDK", "install the .NET 10 SDK: https://dot.net/download");
        else Ok(".NET SDK", ten);

        Ok("ALICE_HOME", home.Root);
        var installed = home.Installed();
        if (installed.Count == 0) Warn("toolchains", "none managed", "alice toolchain install stable");
        else Ok("toolchains", string.Join(", ", installed) + (home.Default is { } d ? $" (default {d})" : ""));

        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        if (path.Any(p => p.TrimEnd('/', '\\') == home.BinDir.TrimEnd('/', '\\'))) Ok("PATH", home.BinDir);
        else Warn("PATH", $"{home.BinDir} not on PATH", OperatingSystem.IsWindows() ? "rerun install.ps1 or add it in System Properties" : $"echo 'export PATH=\"{home.BinDir}:$PATH\"' >> ~/.profile");

        var (v, why) = home.Resolve(Environment.CurrentDirectory, Environment.GetEnvironmentVariable("ALICE_TOOLCHAIN"));
        if (why is "default" or "running binary" || home.IsInstalled(v)) Ok("active", $"{v} ({why})");
        else Bad("active", $"{v} ({why}) not installed", $"alice toolchain install {v}");

        if (Capture("clang", "--version") is { } clang) Ok("clang", clang.Split('\n')[0] + " (optional: @[Native])");
        else Warn("clang", "not found (optional, only for @[Native] functions)", "install LLVM/clang if you use @[Native]");

        if (UpdateNotifier.Notice(home, App.Version) is { } n) Warn("updates", n, "alice self update");
        else Ok("updates", UpdateNotifier.Enabled(Environment.GetEnvironmentVariable) ? "up to date (checked daily)" : "checks disabled");

        AnsiConsole.MarkupLine(problems == 0 ? "\n[green]all good[/]" : $"\n[red]{problems} problem(s)[/]");
        return problems == 0 ? 0 : 1;
    }

    static string? Capture(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0 ? o.Trim() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
