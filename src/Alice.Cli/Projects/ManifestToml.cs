using Alice.Core.Toml;

namespace Alice.Cli.Projects;

/// <summary>
/// alice.toml through the TOML 1.0 parser of Alice Core (dogfooding <c>Alice.Core.Toml</c>), returned in the shape
/// the manifest code reads: top-level tables by name (`[package]`, `[dependencies]`; the root's own keys under ""),
/// values as string, long, double, bool, <c>List&lt;object&gt;</c> or <c>Dictionary&lt;string, object&gt;</c>.
/// Compatibility with the former subset parser: a sub-table is also listed flat under its dotted name
/// (`[a.b]` as "a.b"), and in `[dependencies]` an unquoted dotted package id (`Newtonsoft.Json = "13.0.3"`, which
/// TOML reads as nested tables) is folded back into one key.
/// </summary>
public static class ManifestToml
{
    static readonly string[] DependencyFields = ["path", "workspace", "version"];

    /// <summary>Parses manifest text; syntax errors are <see cref="FormatException"/>s that name the line.</summary>
    public static Dictionary<string, Dictionary<string, object>> Parse(string text)
    {
        TomlTable doc;
        try { doc = Toml.Parse(text); }
        catch (TomlParseException e) { throw new FormatException($"alice.toml line {e.Line}, column {e.Column}: {e.Reason}", e); }
        var root = new Dictionary<string, Dictionary<string, object>> { [""] = [] };
        foreach (var (key, value) in doc)
        {
            if (value.AsTable() is not { } table) { root[""][key] = value.ToObject()!; continue; }
            var d = table.ToDictionary();
            if (key == "dependencies") d = FoldDottedIds(d);
            root[key] = d;
            AddFlat(root, key, table);
        }
        return root;
    }

    static void AddFlat(Dictionary<string, Dictionary<string, object>> root, string prefix, TomlTable t)
    {
        foreach (var (k, v) in t)
            if (v.AsTable() is { } sub)
            {
                string name = prefix + "." + k;
                root.TryAdd(name, sub.ToDictionary());
                AddFlat(root, name, sub);
            }
    }

    static Dictionary<string, object> FoldDottedIds(Dictionary<string, object> deps)
    {
        var r = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in deps) Fold(r, k, v);
        return r;
    }

    static void Fold(Dictionary<string, object> into, string name, object v)
    {
        if (v is Dictionary<string, object> t && !DependencyFields.Any(t.ContainsKey))
        {
            foreach (var (k, sub) in t) Fold(into, name + "." + k, sub);
            return;
        }
        into[name] = v;
    }
}
