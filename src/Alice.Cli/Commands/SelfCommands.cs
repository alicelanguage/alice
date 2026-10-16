using Alice.Cli.Toolchains;
using Spectre.Console;

namespace Alice.Cli.Commands;

/// <summary>`alice self update|check|uninstall`: keep the installation itself current.</summary>
public static class SelfCommands
{
    public static int Run(List<string> a)
    {
        var home = ToolchainHome.Current();
        return a.FirstOrDefault() switch
        {
            "update" or "upgrade" => Update(home, a.Skip(1).ToList()),
            "check" => Check(home),
            "uninstall" => Uninstall(home, a.Contains("--yes") || a.Contains("-y")),
            _ => throw new UsageException("usage: alice self <update|check|uninstall>", "alice self update [--nightly]"),
        };
    }

    static int Update(ToolchainHome home, List<string> a)
    {
        string channel = a.Remove("--nightly") ? "nightly" : (home.Default is { } d && ToolchainVersion.TryParse(d)?.Channel == "nightly" ? "nightly" : "stable");
        int rc = ToolchainCommands.Install(home, [channel], selfUpdate: true);
        UpdateNotifier.Save(home, home.Default ?? App.Version, DateTimeOffset.UtcNow);
        var keep = home.Installed().Where(v => v.Channel == channel).Skip(3).ToList();
        if (keep.Count > 0) AnsiConsole.MarkupLine($"  [grey]older {channel} toolchains: {string.Join(", ", keep)} (alice toolchain uninstall VERSION)[/]");
        return rc;
    }

    static int Check(ToolchainHome home)
    {
        var releases = Ui.Ui.Spin("Checking for updates", () => new GitHubReleaseSource().List());
        var stable = ReleaseFeed.Pick(releases, "stable");
        var nightly = ReleaseFeed.Pick(releases, "nightly");
        UpdateNotifier.Save(home, stable?.Version.ToString() ?? App.Version, DateTimeOffset.UtcNow);
        AnsiConsole.MarkupLine($"  running  [bold]{App.Version}[/]");
        AnsiConsole.MarkupLine($"  stable   [bold]{stable?.Version.ToString() ?? "—"}[/]");
        AnsiConsole.MarkupLine($"  nightly  [bold]{nightly?.Version.ToString() ?? "—"}[/]");
        if (UpdateNotifier.Notice(home, App.Version) is { } n) AnsiConsole.MarkupLine($"\n[yellow]{Markup.Escape(n)}[/]");
        else AnsiConsole.MarkupLine("\n[green]up to date[/]");
        return 0;
    }

    static int Uninstall(ToolchainHome home, bool yes)
    {
        if (!Directory.Exists(home.Root)) throw new UsageException($"nothing installed at {home.Root}");
        if (!yes && !(AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Confirm($"Remove {home.Root} and every toolchain in it?", false)))
            throw new UsageException("uninstall cancelled", "alice self uninstall --yes");
        Directory.Delete(home.Root, true);
        Ui.Ui.Status("Removed", home.Root);
        AnsiConsole.MarkupLine($"  [grey]remove {Markup.Escape(home.BinDir)} from PATH in your shell profile[/]");
        return 0;
    }
}
