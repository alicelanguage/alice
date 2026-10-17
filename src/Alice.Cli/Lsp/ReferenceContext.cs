using System.Reflection;
using System.Runtime.Loader;

namespace Alice.Cli.Lsp;

/// <summary>Collectible reflection references read into memory, leaving rebuilt DLLs unlocked.</summary>
sealed class ReferenceContext(IReadOnlyList<string> paths) : AssemblyLoadContext(isCollectible: true)
{
    readonly Dictionary<string, Assembly> loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> files = paths.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Shared = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator).Select(Path.GetFileNameWithoutExtension).Where(n => n != null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Assembly Read(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (Shared.Contains(name)) return Default.LoadFromAssemblyName(new AssemblyName(name));
        if (loaded.TryGetValue(name, out var found)) return found;
        // Framework references point at packs; reflect over their installed runtime counterpart.
        if ((path.Replace('\\', '/').Contains("/packs/") || path.Replace('\\', '/').Contains("/ref/")))
        {
            string shared = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "shared"));
            if (Directory.Exists(shared))
                path = Directory.EnumerateFiles(shared, Path.GetFileName(path), SearchOption.AllDirectories).OrderDescending().FirstOrDefault() ?? path;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return loaded[name] = LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName name) =>
        name.Name is { } simple && files.TryGetValue(simple, out var path) ? Read(path) : null;
}
