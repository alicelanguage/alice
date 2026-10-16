using Alice.Cli.Projects;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>
/// NuGet: remove / search / list / install / cache. Restores go through `dotnet restore`, so packages land in the
/// standard .NET global package cache (~/.nuget/packages or $NUGET_PACKAGES) shared with every C# project.
/// </summary>
public static class PackageCommands
{
    static string ManifestDir() =>
        File.Exists(Manifest.FileName) ? "." : throw UsageException.NoProject();

    public static int Remove(List<string> a)
    {
        var pkg = a.FirstOrDefault(x => !x.StartsWith('-')) ?? throw UsageException.Usage("alice remove <package>");
        string dir = ManifestDir();
        if (!Manifest.RemoveDependency(dir, pkg))
        {
            var near = UsageException.Closest(pkg, Manifest.Dependencies(dir).Select(d => d.Name));
            throw new UsageException(T("not.dependency", pkg), near != null ? T("did.you.mean", near) : null, 1);
        }
        UiH.Status(T("removed"), $"{pkg} ← {Manifest.FileName}");
        return a.Contains("--no-restore") ? 0 : Restore(dir);
    }

    public static int List(List<string> a)
    {
        string dir = ManifestDir();
        var deps = Manifest.Dependencies(dir);
        if (deps.Count == 0) { AnsiConsole.MarkupLine($"[grey]{T("no.deps")}[/]"); return 0; }
        var t = new Table().Border(TableBorder.Rounded).AddColumn("package").AddColumn("version / source");
        foreach (var (n, v) in deps) t.AddRow($"[green]{Markup.Escape(n)}[/]", Markup.Escape(v));
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"[grey]cache: {Markup.Escape(GlobalPackages())}[/]");
        return 0;
    }

    public static int Search(List<string> a)
    {
        var term = a.FirstOrDefault(x => !x.StartsWith('-')) ?? throw UsageException.Usage("alice search <term>");
        var r = UiH.Spin($"searching NuGet for {term}…", () => DotNet.Run(["package", "search", term, "--take", "15", "--format", "json"]));
        if (r.ExitCode != 0) throw new UsageException("NuGet search failed", string.Join(" ", r.Output.TakeLast(2)), 1);
        var t = new Table().Border(TableBorder.Rounded).AddColumn("package").AddColumn("latest").AddColumn("downloads");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.Join("\n", r.Output.SkipWhile(l => !l.TrimStart().StartsWith('{'))));
            foreach (var src in doc.RootElement.GetProperty("searchResult").EnumerateArray())
                foreach (var p in src.GetProperty("packages").EnumerateArray())
                    t.AddRow($"[green]{Markup.Escape(p.GetProperty("id").GetString() ?? "")}[/]",
                        Markup.Escape(p.GetProperty("latestVersion").GetString() ?? ""),
                        p.TryGetProperty("totalDownloads", out var dl) ? dl.GetInt64().ToString("N0") : "");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            foreach (var l in r.Output) Console.WriteLine(l);
            return 0;
        }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"[grey]alice add <package> [[--version <v>]][/]");
        return 0;
    }

    /// <summary>`alice install`: restore every dependency into the shared cache and refresh alice.lock;
    /// `alice install --tool <name>`: install a .NET global tool.</summary>
    public static int Install(List<string> a)
    {
        int ti = a.IndexOf("--tool");
        if (ti >= 0)
        {
            string tool = ti + 1 < a.Count ? a[ti + 1] : throw UsageException.Usage("alice install --tool <name>");
            var r = UiH.Spin($"installing {tool}…", () => DotNet.Run(["tool", "update", "--global", tool]));
            foreach (var l in r.Output.TakeLast(3)) Console.WriteLine(l);
            return r.ExitCode;
        }
        return Restore(ManifestDir());
    }

    public static int Cache(List<string> a)
    {
        if (a.Contains("--clear"))
        {
            var r = DotNet.Run(["nuget", "locals", "global-packages", "--clear"]);
            UiH.Status("Cleared", GlobalPackages());
            return r.ExitCode;
        }
        AnsiConsole.MarkupLine($"global packages: [green]{Markup.Escape(GlobalPackages())}[/]");
        AnsiConsole.MarkupLine("[grey]shared with every .NET project; set NUGET_PACKAGES to move it; alice cache --clear empties it[/]");
        return 0;
    }

    static string GlobalPackages()
    {
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } env) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    static int Restore(string dir)
    {
        var proj = Manifest.Sync(dir);
        var r = UiH.Spin($"{T("restoring")}…", () => DotNet.Run(["restore", proj]));
        if (r.ExitCode != 0) { foreach (var l in r.Output.TakeLast(15)) Console.Error.WriteLine(l); return r.ExitCode; }
        UiH.Status(T("finished"), $"{Manifest.Dependencies(dir).Count} dependencies → {GlobalPackages()}");
        return 0;
    }
}
