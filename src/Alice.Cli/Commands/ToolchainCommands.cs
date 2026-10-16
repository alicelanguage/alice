using System.Diagnostics;
using Alice.Cli.Toolchains;
using Spectre.Console;

namespace Alice.Cli.Commands;

/// <summary>`alice toolchain ...`: install, list, switch and pin Alice versions side by side (rustup-style).</summary>
public static class ToolchainCommands
{
    static readonly string[] Subcommands = ["list", "available", "install", "uninstall", "default", "pin", "unpin", "which", "show"];

    public static int Run(List<string> a)
    {
        if (a.Count == 0 || a[0] is "-h" or "--help") { Help(); return a.Count == 0 ? 2 : 0; }
        var rest = a.Skip(1).ToList();
        var home = ToolchainHome.Current();
        return a[0] switch
        {
            "list" or "ls" => List(home),
            "available" or "remote" => Available(home),
            "install" or "add" => Install(home, rest),
            "uninstall" or "remove" or "rm" => Uninstall(home, rest),
            "default" or "use" => Default(home, rest),
            "pin" or "override" => Pin(rest),
            "unpin" => Unpin(),
            "which" => Which(home),
            "show" => Show(home),
            _ => throw new UsageException($"unknown toolchain command '{a[0]}'",
                UsageException.Closest(a[0], Subcommands) is { } near ? $"did you mean '{near}'?" : "alice toolchain --help"),
        };
    }

    static void Help()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/] alice toolchain [green]<command>[/]\n");
        var t = new Table().NoBorder().HideHeaders().AddColumn("").AddColumn("");
        t.AddRow("  [green]list[/]", "Installed toolchains (the default is marked)");
        t.AddRow("  [green]available[/]", "Releases on GitHub you can install (stable and nightly)");
        t.AddRow("  [green]install[/] [blue]<stable|nightly|VERSION>[/]", "Download, verify and install (--no-default keeps the current default)");
        t.AddRow("  [green]uninstall[/] [blue]<VERSION>[/]", "Remove an installed toolchain");
        t.AddRow("  [green]default[/] [blue]<VERSION>[/]", "Make a toolchain the global default");
        t.AddRow("  [green]pin[/] [blue]<VERSION>[/]", "Pin this directory with an alice-toolchain file");
        t.AddRow("  [green]unpin[/]", "Remove the alice-toolchain file here");
        t.AddRow("  [green]which[/]", "Path of the binary that runs here");
        t.AddRow("  [green]show[/]", "Active toolchain and why it was chosen");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("\n[grey]One-off: alice +0.10.1 run app.al   ·   env: ALICE_TOOLCHAIN, ALICE_HOME[/]");
    }

    static int List(ToolchainHome home)
    {
        var installed = home.Installed();
        if (installed.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]no toolchains in {Markup.Escape(home.ToolchainsDir)}[/]");
            AnsiConsole.MarkupLine("  [cyan]hint[/]: alice toolchain install stable");
            return 0;
        }
        var (active, _) = home.Resolve(Environment.CurrentDirectory, Environment.GetEnvironmentVariable("ALICE_TOOLCHAIN"));
        string? def = home.Default;
        foreach (var v in installed)
        {
            string s = v.ToString();
            var tags = new List<string>();
            if (s == def) tags.Add("[green]default[/]");
            if (s == active) tags.Add("[magenta]active[/]");
            if (v.Channel != "stable") tags.Add($"[yellow]{v.Channel}[/]");
            AnsiConsole.MarkupLine($"  {(s == active ? "[bold magenta]›[/]" : " ")} [bold]{Markup.Escape(s)}[/] {string.Join(" ", tags)}");
        }
        return 0;
    }

    static int Available(ToolchainHome home)
    {
        var releases = Ui.Ui.Spin("Fetching releases", () => new GitHubReleaseSource().List());
        var installed = home.Installed().Select(v => v.ToString()).ToHashSet();
        string rid = Platform.CurrentRid();
        var t = new Table().Border(TableBorder.Rounded).AddColumn("version").AddColumn("channel").AddColumn("published").AddColumn(rid);
        foreach (var r in releases.OrderByDescending(r => r.Version).Take(25))
        {
            string v = r.Version.ToString();
            t.AddRow(installed.Contains(v) ? $"[green]{v} ✓[/]" : v, r.Version.Channel, r.Published == DateTimeOffset.MinValue ? "" : r.Published.ToString("yyyy-MM-dd"),
                r.Asset(rid) != null ? "[green]yes[/]" : "[grey]—[/]");
        }
        AnsiConsole.Write(t);
        return 0;
    }

    public static int Install(ToolchainHome home, List<string> a, bool selfUpdate = false)
    {
        bool noDefault = a.Remove("--no-default");
        bool force = a.Remove("--force");
        string spec = a.FirstOrDefault() ?? "stable";
        var sw = Stopwatch.StartNew();
        var installer = new Installer(home, new GitHubReleaseSource()) { Progress = (verb, detail) => Ui.Ui.Status(verb, detail, "cyan") };
        var v = installer.Install(spec, Platform.CurrentRid(), makeDefault: !noDefault, force: force);
        Ui.Ui.Status(selfUpdate ? "Updated" : "Installed", $"alice {v} ({v.Channel}) in {Ui.Ui.Elapsed(sw)}");
        if (home.Default == v.ToString()) Ui.Ui.Status("Default", v.ToString());
        PathHint(home);
        return 0;
    }

    static int Uninstall(ToolchainHome home, List<string> a)
    {
        if (a.Count == 0) throw UsageException.Usage("alice toolchain uninstall <VERSION>");
        foreach (var v in a) { home.Uninstall(v); Ui.Ui.Status("Removed", "alice " + v); }
        if (home.Default is { } d) Ui.Ui.Status("Default", d);
        return 0;
    }

    static int Default(ToolchainHome home, List<string> a)
    {
        if (a.Count == 0) { AnsiConsole.WriteLine(home.Default ?? "(none)"); return 0; }
        string v = Spec(home, a[0]);
        home.Default = v;
        Ui.Ui.Status("Default", "alice " + v);
        PathHint(home);
        return 0;
    }

    static string Spec(ToolchainHome home, string spec)
    {
        var installed = home.Installed();
        var pick = spec switch
        {
            "stable" => installed.FirstOrDefault(v => v.Pre == null),
            "nightly" => installed.FirstOrDefault(v => v.Channel == "nightly"),
            _ => installed.FirstOrDefault(v => v.ToString() == ToolchainVersion.TryParse(spec)?.ToString()),
        };
        return pick?.ToString() ?? throw new UsageException($"toolchain {spec} is not installed", $"alice toolchain install {spec}");
    }

    static int Pin(List<string> a)
    {
        if (a.Count == 0) throw UsageException.Usage("alice toolchain pin <VERSION>");
        if (ToolchainVersion.TryParse(a[0]) == null) throw new UsageException($"'{a[0]}' is not a version", "e.g. alice toolchain pin 0.10.1");
        File.WriteAllText(ToolchainHome.OverrideFile, a[0].TrimStart('v') + "\n");
        Ui.Ui.Status("Pinned", $"{Environment.CurrentDirectory} → alice {a[0].TrimStart('v')}");
        return 0;
    }

    static int Unpin()
    {
        if (!File.Exists(ToolchainHome.OverrideFile)) throw new UsageException("no alice-toolchain file in this directory");
        File.Delete(ToolchainHome.OverrideFile);
        Ui.Ui.Status("Unpinned", Environment.CurrentDirectory);
        return 0;
    }

    static int Which(ToolchainHome home)
    {
        var (v, _) = home.Resolve(Environment.CurrentDirectory, Environment.GetEnvironmentVariable("ALICE_TOOLCHAIN"));
        AnsiConsole.WriteLine(home.IsInstalled(v) ? home.ExePath(v) : Environment.ProcessPath ?? "alice");
        return 0;
    }

    static int Show(ToolchainHome home)
    {
        var (v, why) = home.Resolve(Environment.CurrentDirectory, Environment.GetEnvironmentVariable("ALICE_TOOLCHAIN"));
        AnsiConsole.MarkupLine($"[bold magenta]{Markup.Escape(v)}[/] [grey](from {Markup.Escape(why)})[/]");
        AnsiConsole.MarkupLine($"[grey]running {Markup.Escape(App.Version)} · home {Markup.Escape(home.Root)}[/]");
        return 0;
    }

    static void PathHint(ToolchainHome home)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (path.Split(Path.PathSeparator).Any(p => Path.GetFullPath(p.Length > 0 ? p : ".").TrimEnd(Path.DirectorySeparatorChar) == home.BinDir.TrimEnd(Path.DirectorySeparatorChar))) return;
        AnsiConsole.MarkupLine($"  [cyan]hint[/]: add [bold]{Markup.Escape(home.BinDir)}[/] to PATH (the install script does this for you)");
    }

    /// <summary>
    /// `alice +VERSION ...`, `ALICE_TOOLCHAIN` or an `alice-toolchain` file: re-run the command with that toolchain.
    /// Returns null when this binary should handle the command itself.
    /// </summary>
    public static int? Dispatch(List<string> args, IReadOnlyList<string> argv)
    {
        if (Environment.GetEnvironmentVariable("ALICE_TOOLCHAIN_ACTIVE") is { Length: > 0 }) { if (args.Count > 0 && args[0].StartsWith('+')) args.RemoveAt(0); return null; }
        var home = ToolchainHome.Current();
        string? explicitSpec = args.Count > 0 && args[0].StartsWith('+') && args[0].Length > 1 ? args[0][1..] : null;
        if (explicitSpec != null) args.RemoveAt(0);
        if (args.Count > 0 && args[0] is "toolchain" or "self" or "doctor" or "completions" && explicitSpec == null) return null;
        var (spec, why) = explicitSpec != null ? (explicitSpec, "+" + explicitSpec)
            : home.Resolve(Environment.CurrentDirectory, Environment.GetEnvironmentVariable("ALICE_TOOLCHAIN"));
        if (why is "default" or "running binary") return null;
        string version = spec is "stable" or "nightly" ? Spec(home, spec) : ToolchainVersion.TryParse(spec)?.ToString() ?? spec;
        if (version == App.Version) return null;
        if (!home.IsInstalled(version))
            throw new UsageException($"toolchain {version} (from {why}) is not installed", $"alice toolchain install {version}");
        var psi = new ProcessStartInfo(home.ExePath(version)) { UseShellExecute = false };
        // forward the original command line (global flags included), minus the `+VERSION` selector
        bool dropped = false;
        foreach (var x in argv)
        {
            if (!dropped && explicitSpec != null && x == "+" + explicitSpec) { dropped = true; continue; }
            psi.ArgumentList.Add(x);
        }
        psi.Environment["ALICE_TOOLCHAIN_ACTIVE"] = version;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
