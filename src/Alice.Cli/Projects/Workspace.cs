namespace Alice.Cli.Projects;

/// <summary>
/// Cargo-style workspace: a root alice.toml with `[workspace] members = ["geo", "apps/*"]`. Members resolve each
/// other by package name (`geo = { workspace = true }`) and are built dependencies-first. No solution file needed.
/// </summary>
public sealed class Workspace
{
    public sealed record Member(string Name, string Dir, string Kind, List<string> WorkspaceDeps);

    public string Root = "";
    public List<Member> Members = new();

    public static bool IsWorkspaceRoot(string dir) =>
        File.Exists(Path.Combine(dir, Manifest.FileName)) && TomlLite.Parse(File.ReadAllText(Path.Combine(dir, Manifest.FileName))).ContainsKey("workspace");

    /// <summary>The nearest enclosing workspace root (the directory itself included), or null.</summary>
    public static string? FindRoot(string dir)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d != null; d = d.Parent)
            if (IsWorkspaceRoot(d.FullName)) return d.FullName;
        return null;
    }

    public static Workspace Load(string root)
    {
        root = Path.GetFullPath(root);
        var toml = TomlLite.Parse(File.ReadAllText(Path.Combine(root, Manifest.FileName)));
        var patterns = toml["workspace"].TryGetValue("members", out var m) && m is List<object> l ? l.Cast<string>().ToList() : new List<string>();
        var dirs = new List<string>();
        foreach (var p in patterns)
        {
            if (p.EndsWith("/*") || p.EndsWith("\\*"))
            {
                var parent = Path.Combine(root, p[..^2]);
                if (Directory.Exists(parent))
                    dirs.AddRange(Directory.GetDirectories(parent).Where(d => File.Exists(Path.Combine(d, Manifest.FileName))).OrderBy(d => d, StringComparer.Ordinal));
            }
            else dirs.Add(Path.Combine(root, p));
        }
        var members = new List<Member>();
        foreach (var d in dirs.Select(Path.GetFullPath).Distinct())
        {
            var mt = TomlLite.Parse(File.ReadAllText(Path.Combine(d, Manifest.FileName)));
            var pkg = mt.GetValueOrDefault("package") ?? new();
            string name = pkg.TryGetValue("name", out var n) ? (string)n : Path.GetFileName(d);
            string kind = pkg.TryGetValue("kind", out var k) ? (string)k : "exe";
            var deps = (mt.GetValueOrDefault("dependencies") ?? new())
                .Where(kv => kv.Value is Dictionary<string, object> t && t.TryGetValue("workspace", out var w) && w is true)
                .Select(kv => kv.Key).ToList();
            members.Add(new Member(name, d, kind, deps));
        }
        return new Workspace { Root = root, Members = TopoSort(members) };
    }

    /// <summary>Dependencies first; otherwise keeps manifest order. Cycles are reported.</summary>
    static List<Member> TopoSort(List<Member> members)
    {
        var byName = members.ToDictionary(m => m.Name);
        var done = new List<Member>();
        var state = new Dictionary<string, int>();
        void Visit(Member m, Stack<string> path)
        {
            if (state.TryGetValue(m.Name, out var st))
            {
                if (st == 1) throw new InvalidOperationException($"workspace dependency cycle: {string.Join(" -> ", path.Reverse().Append(m.Name))}");
                return;
            }
            state[m.Name] = 1;
            path.Push(m.Name);
            foreach (var d in m.WorkspaceDeps)
                if (byName.TryGetValue(d, out var dm)) Visit(dm, path);
            path.Pop();
            state[m.Name] = 2;
            done.Add(m);
        }
        foreach (var m in members) Visit(m, new Stack<string>());
        return done;
    }
}
