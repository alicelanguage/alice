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
        if (a.Contains("--workspace"))
        {
            string wname = a.FirstOrDefault(x => !x.StartsWith("--")) ?? throw new CompileException(T("no.files"));
            Directory.CreateDirectory(wname);
            if (File.Exists(Path.Combine(wname, Manifest.FileName))) throw new CompileException(T("exists", wname));
            File.WriteAllText(Path.Combine(wname, Manifest.FileName), "[workspace]\nmembers = []\n");
            File.WriteAllText(Path.Combine(wname, ".gitignore"), "bin/\nobj/\n.alice/\n");
            UiH.Status(T("created"), $"workspace `{wname}`");
            return 0;
        }
        string kind = a.Contains("--lib") ? "lib" : a.Contains("--game") ? "game" : "exe";
        string? name = a.FirstOrDefault(x => !x.StartsWith("--"));
        if (name == null) throw new CompileException(T("no.files"));
        string dir = Path.GetFullPath(name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) throw new CompileException(T("exists", name));
        string proj = Path.GetFileName(dir);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        File.WriteAllText(Path.Combine(dir, Manifest.FileName), $"[package]\nname = \"{proj}\"\nversion = \"0.1.0\"\nkind = \"{kind}\"\n\n[dependencies]\n");
        Manifest.Sync(dir);
        File.WriteAllText(Path.Combine(dir, "src", kind == "lib" ? "lib.al" : "main.al"), ProjectFiles.MainSource(proj, kind));
        File.WriteAllText(Path.Combine(dir, "test", "basics_test.al"), ProjectFiles.TestSource(proj));
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n.alice/\n");
        RegisterMember(dir);
        UiH.Status(T("created"), $"{kind} `{proj}`");
        UiH.Status("", T("created.next", name), "grey");
        return 0;
    }

    /// <summary>New projects created inside a workspace become members automatically.</summary>
    static void RegisterMember(string dir)
    {
        var root = Workspace.FindRoot(Path.GetDirectoryName(dir)!);
        if (root == null) return;
        if (Workspace.Load(root).Members.Any(m => m.Dir == dir)) return;
        string rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
        string file = Path.Combine(root, Manifest.FileName);
        string text = File.ReadAllText(file);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"members\s*=\s*\[(?<items>[^\]]*)\]");
        if (!m.Success) return;
        string items = m.Groups["items"].Value.Trim();
        string updated = items.Length == 0 ? $"\"{rel}\"" : items.TrimEnd(',') + $", \"{rel}\"";
        File.WriteAllText(file, text.Remove(m.Groups["items"].Index, m.Groups["items"].Length).Insert(m.Groups["items"].Index, updated));
        UiH.Status(T("workspace"), $"+ {rel}", "blue");
    }

    /// <summary>`alice update`: drop pinned versions, restore, and lock the newly resolved ones.</summary>
    public static int Update(List<string> a)
    {
        string dir = Workspace.FindRoot(".") ?? ".";
        var lockPath = Path.Combine(dir, Lockfile.FileName);
        if (File.Exists(lockPath)) File.Delete(lockPath);
        return Workspace.IsWorkspaceRoot(dir) ? CompileCommands.BuildWorkspace(dir, false) : CompileCommands.Build(new List<string>());
    }

    public static int Add(List<string> a)
    {
        var proj = ProjectFiles.Find(".") ?? throw new CompileException(T("no.project"));
        var pkg = a.FirstOrDefault(x => !x.StartsWith("-")) ?? throw new CompileException(T("no.files"));
        int pi = a.IndexOf("--path");
        int vv = a.IndexOf("--version");
        if (File.Exists(Manifest.FileName))
        {
            // manifest projects: record the dependency in alice.toml, regenerate, restore
            Manifest.AddDependency(".", pkg, vv >= 0 && vv + 1 < a.Count ? a[vv + 1] : "*", pi >= 0 && pi + 1 < a.Count ? a[pi + 1] : null);
            proj = Manifest.Sync(".");
            UiH.Status(T("adding"), pkg);
            var rr = UiH.Spin($"{T("restoring")}…", () => DotNet.Run(["restore", proj]));
            if (rr.ExitCode != 0) { foreach (var l in rr.Output.TakeLast(15)) Console.Error.WriteLine(l); return rr.ExitCode; }
            UiH.Status(T("added"), $"{pkg} → {Manifest.FileName}");
            return 0;
        }
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
}
