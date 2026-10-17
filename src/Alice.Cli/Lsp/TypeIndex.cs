using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Alice.Cli.Lsp;

/// <summary>
/// Public .NET type names -> namespaces, for completion and `using` fixes. Built lazily from assembly metadata
/// (System.Reflection.Metadata: nothing is loaded into the runtime), once per set of references, then shared.
/// </summary>
public sealed class TypeIndex
{
    readonly Dictionary<string, string[]> byName = new(StringComparer.Ordinal);
    readonly string[] names;
    readonly string[] namespaces;

    TypeIndex(IEnumerable<string> assemblies)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in assemblies)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var h in md.TypeDefinitions)
                {
                    var t = md.GetTypeDefinition(h);
                    var vis = t.Attributes & System.Reflection.TypeAttributes.VisibilityMask;
                    if (vis != System.Reflection.TypeAttributes.Public) continue;
                    var ns = md.GetString(t.Namespace);
                    if (ns.Length == 0 || ns.Contains("Internal") || ns.StartsWith("System.Runtime.CompilerServices")) continue;
                    var name = md.GetString(t.Name);
                    int tick = name.IndexOf('`');
                    if (tick >= 0) name = name[..tick];
                    if (name.Length == 0 || name[0] == '<') continue;
                    if (!map.TryGetValue(name, out var set)) map[name] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(string.Intern(ns));
                }
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException) { }
        }
        foreach (var (k, v) in map) byName[k] = v.Order().ToArray();
        names = byName.Keys.Order(StringComparer.Ordinal).ToArray();
        namespaces = map.Values.SelectMany(v => v).Distinct().Order(StringComparer.Ordinal).ToArray();
    }

    static readonly object Gate = new();
    static (string Key, TypeIndex Index)? cached;

    /// <summary>Index over the runtime's trusted assemblies plus project references (cached by reference set).</summary>
    public static TypeIndex For(IReadOnlyCollection<string> extraRefs)
    {
        var key = string.Join('|', extraRefs.Order().Select(p => p + ":" + System.IO.File.GetLastWriteTimeUtc(p).Ticks));
        lock (Gate)
        {
            if (cached is { } c && c.Key == key) return c.Index;
            var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(System.IO.Path.PathSeparator)
                .Where(p => p.Length > 0 && (System.IO.Path.GetFileName(p) is var f && (f.StartsWith("System") || f.StartsWith("Microsoft") || f.StartsWith("Alice") || f == "netstandard.dll")));
            var index = new TypeIndex(tpa.Concat(extraRefs));
            cached = (key, index);
            return index;
        }
    }

    public IEnumerable<string> NamespacePrefix(string prefix) => namespaces.Where(n => n.StartsWith(prefix, StringComparison.Ordinal));

    public string[] Namespaces(string name) => byName.TryGetValue(name, out var ns) ? ns : [];

    /// <summary>Type names starting with a prefix (binary search over the sorted names).</summary>
    public IEnumerable<string> WithPrefix(string prefix)
    {
        int lo = 0, hi = names.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (string.CompareOrdinal(names[mid], prefix) < 0) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < names.Length && names[i].StartsWith(prefix, StringComparison.Ordinal); i++) yield return names[i];
    }
}
