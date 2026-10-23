using System.Diagnostics;
using Alice.Cli.Projects;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>
/// alice pack / alice publish: a package (lib or tool) becomes a .nupkg via `dotnet pack` on the generated .alproj,
/// then `dotnet nuget push` sends it to nuget.org, a private feed, or a local folder.
/// </summary>
public static class PublishCommands
{
    const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    sealed record Options(string Dir, string? Out, string? Source, string? ApiKey, bool DryRun);

    static Options Parse(List<string> a, string usage)
    {
        string? dir = null, outDir = null, source = null, key = null;
        bool dry = false;
        for (int i = 0; i < a.Count; i++)
        {
            string Next() => i + 1 < a.Count ? a[++i] : throw UsageException.Usage(usage);
            switch (a[i])
            {
                case "-o" or "--output": outDir = Next(); break;
                case "--source" or "-s": source = Next(); break;
                case "--api-key" or "-k": key = Next(); break;
                case "--dry-run": dry = true; break;
                case var x when x.StartsWith('-'): throw UsageException.Usage(usage);
                default: dir = a[i]; break;
            }
        }
        dir ??= ".";
        if (!File.Exists(Path.Combine(dir, Manifest.FileName))) throw UsageException.NoProject();
        return new Options(Path.GetFullPath(dir), outDir, source, key, dry);
    }

    public static int Pack(List<string> a)
    {
        var o = Parse(a, "alice pack [dir] [-o out]");
        return PackTo(o, o.Out ?? Path.Combine(o.Dir, "bin", "package"), out _);
    }

    public static int Publish(List<string> a)
    {
        var o = Parse(a, "alice publish [dir] [--source url|dir] [--api-key key] [--dry-run]");
        string source = o.Source ?? NuGetOrg;
        bool local = !source.StartsWith("http://") && !source.StartsWith("https://");
        string? key = o.ApiKey ?? Environment.GetEnvironmentVariable("NUGET_API_KEY");
        if (!local && string.IsNullOrEmpty(key))
            throw new UsageException(T("publish.no.key", source), T("hint.publish.key"));
        int rc = PackTo(o, o.Out ?? Path.Combine(o.Dir, "bin", "package"), out var nupkg);
        if (rc != 0 || o.DryRun) return rc;
        if (local) Directory.CreateDirectory(source);
        var args = new List<string> { "nuget", "push", nupkg!, "--source", local ? Path.GetFullPath(source) : source, "--skip-duplicate" };
        if (!local) args.AddRange(["--api-key", key!]);
        var r = UiH.Spin($"{T("publishing")} {Path.GetFileName(nupkg)}…", () => DotNet.Run(args));
        if (r.ExitCode != 0)
        {
            // never echo the push command line: it carries the API key
            foreach (var l in r.Output.TakeLast(10)) Console.Error.WriteLine(key is { Length: > 0 } ? l.Replace(key, "***") : l);
            return r.ExitCode;
        }
        UiH.Status(T("published"), $"{Path.GetFileName(nupkg)} → {source}");
        return 0;
    }

    static int PackTo(Options o, string outDir, out string? nupkg)
    {
        nupkg = null;
        string proj = Manifest.Sync(o.Dir);
        if (File.ReadAllText(proj).Contains("<IsPackable>false</IsPackable>"))
            throw new UsageException(T("pack.not.packable", Path.GetFileNameWithoutExtension(proj)), T("hint.pack.kind"));
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(outDir);
        var before = Directory.GetFiles(outDir, "*.nupkg").ToDictionary(f => f, File.GetLastWriteTimeUtc);
        var r = UiH.Spin($"{T("packing")} {Path.GetFileNameWithoutExtension(proj)}…",
            () => DotNet.Run(["pack", proj, "-c", "Release", "-o", outDir, "-v", "q", "-nologo", "-clp:NoSummary"]));
        if (r.ExitCode != 0 || r.Diagnostics.Any(d => d.Severity == "error"))
        {
            CompileCommands.Report(r);
            return r.ExitCode == 0 ? 1 : r.ExitCode;
        }
        nupkg = Directory.GetFiles(outDir, "*.nupkg")
            .Where(f => !f.EndsWith(".symbols.nupkg") && (!before.TryGetValue(f, out var t) || File.GetLastWriteTimeUtc(f) > t))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? Directory.GetFiles(outDir, "*.nupkg").OrderByDescending(File.GetLastWriteTimeUtc).First();
        UiH.Status(T("packed"), $"{nupkg} ({UiH.Elapsed(sw)})");
        return 0;
    }
}
