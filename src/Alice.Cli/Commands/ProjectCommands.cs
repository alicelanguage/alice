using Alice.Cli.Projects;
using Alice.Compiler;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>new / add / clean / sln — project lifecycle, cargo style.</summary>
public static class ProjectCommands
{
    public static int New(List<string> a)
    {
        string kind = a.Contains("--lib") ? "lib" : a.Contains("--game") ? "game" : "exe";
        string? name = a.FirstOrDefault(x => !x.StartsWith("--"));
        if (name == null) throw new CompileException(T("no.files"));
        string dir = Path.GetFullPath(name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) throw new CompileException(T("exists", name));
        string proj = Path.GetFileName(dir);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        File.WriteAllText(Path.Combine(dir, proj + ".alproj"), ProjectFiles.ProjectXml(kind));
        File.WriteAllText(Path.Combine(dir, "src", kind == "lib" ? "lib.al" : "main.al"), ProjectFiles.MainSource(proj, kind));
        File.WriteAllText(Path.Combine(dir, "test", "basics_test.al"), ProjectFiles.TestSource(proj));
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n");
        UiH.Status(T("created"), $"{kind} `{proj}`");
        UiH.Status("", T("created.next", name), "grey");
        return 0;
    }

    public static int Add(List<string> a)
    {
        var proj = ProjectFiles.Find(".") ?? throw new CompileException(T("no.project"));
        var pkg = a.FirstOrDefault(x => !x.StartsWith("-")) ?? throw new CompileException(T("no.files"));
        var args = new List<string> { "add", proj, "package", pkg };
        int vi = a.IndexOf("--version");
        if (vi >= 0 && vi + 1 < a.Count) { args.Add("--version"); args.Add(a[vi + 1]); }
        UiH.Status(T("adding"), pkg);
        var r = UiH.Spin($"{T("restoring")}…", () => DotNet.Run(args));
        if (r.ExitCode != 0) { foreach (var l in r.Output.TakeLast(15)) Console.Error.WriteLine(l); return r.ExitCode; }
        UiH.Status(T("added"), $"{pkg} → {Path.GetFileName(proj)}");
        return 0;
    }

    public static int Clean(List<string> a)
    {
        string root = a.FirstOrDefault() ?? ".";
        foreach (var d in new[] { "bin", "obj" })
        {
            var p = Path.Combine(root, d);
            if (Directory.Exists(p)) Directory.Delete(p, true);
        }
        UiH.Status(T("cleaned"), Path.GetFullPath(root));
        return 0;
    }

    public static int Sln(List<string> a)
    {
        if (a.Count < 3 || a[0] != "add") throw new CompileException(T("cmd.sln"));
        foreach (var p in a.Skip(2))
        {
            ProjectFiles.AddToSolution(a[1], p);
            UiH.Status(T("added"), $"{p} → {a[1]}");
        }
        return 0;
    }
}
