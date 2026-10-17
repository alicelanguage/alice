using Alice.Compiler.Semantics;
using System.Text.RegularExpressions;

namespace Alice.Cli.Lsp;

/// <summary>Shared compiler metadata per restored reference set, with bounded retention.</summary>
public sealed class Project
{
    public IReadOnlyList<string> Refs { get; }
    public ClrTypes Clr { get; }
    TypeIndex? index;
    readonly ReferenceContext references;
    Project(IReadOnlyList<string> refs)
    {
        Refs = refs;
        references = new ReferenceContext(refs);
        Clr = new ClrTypes(refs, references.Read);
    }
    public TypeIndex Index => index ??= TypeIndex.For(Refs.ToList());
    static readonly Dictionary<string, Project> ByKey = new();
    public static void Invalidate()
    {
        lock (ByKey) { foreach (var project in ByKey.Values) project.references.Unload(); ByKey.Clear(); }
    }
    public static Project For(string filePath)
    {
        var refs = FindRefs(filePath);
        var key = string.Join('|', refs.Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks));
        lock (ByKey)
        {
            if (!ByKey.TryGetValue(key, out var project))
            {
                if (ByKey.Count >= 8) Invalidate();
                ByKey[key] = project = new Project(refs);
            }
            return project;
        }
    }

    public static string? Root(string filePath)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, Projects.Manifest.FileName)) || (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.alproj").Any())) return dir;
        return null;
    }

    static List<string> FindRefs(string filePath)
    {
        try
        {
            var root = Root(filePath);
            if (root == null) return [];
            var obj = Path.Combine(root, "obj");
            if (!Directory.Exists(obj)) return [];
            var refsFile = Directory.GetFiles(obj, "refs.txt", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (refsFile == null) return [];
            // Keep framework packs: ASP.NET and other framework references are needed as well as NuGet.
            return File.ReadAllLines(refsFile).Select(l => l.Trim()).Where(l => l.Length > 0 && File.Exists(l))
                .Where(l => Path.GetFileName(l) != "Alice.Runtime.dll").Distinct().Order().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static readonly Regex Require = new("^\\s*require\\s+\"([^\"]+)\"", RegexOptions.Multiline);
    public static List<Document> Sources(Document doc, IEnumerable<Document> open)
    {
        var result = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(doc.Path)] = doc };
        var overlays = open.Where(d => d.Path.EndsWith(".al")).ToDictionary(d => Path.GetFullPath(d.Path), StringComparer.OrdinalIgnoreCase);
        var root = Root(doc.Path);
        if (root != null)
        {
            var src = Path.Combine(root, "src");
            if (Directory.Exists(src))
                foreach (var file in Directory.EnumerateFiles(src, "*.al", SearchOption.AllDirectories)) Load(file);
        }
        for (int i = 0; i < result.Count; i++)
        {
            var source = result.Values.ElementAt(i);
            foreach (Match match in Require.Matches(source.Text))
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source.Path)!, match.Groups[1].Value));
                if (!Path.HasExtension(path)) path += ".al";
                Load(path);
            }
        }
        return result.Values.ToList();

        void Load(string file)
        {
            file = Path.GetFullPath(file);
            if (result.ContainsKey(file)) return;
            if (overlays.TryGetValue(file, out var overlay)) result[file] = overlay;
            else if (File.Exists(file)) result[file] = new Document(new Uri(file).AbsoluteUri, File.ReadAllText(file), 0);
        }
    }
}
