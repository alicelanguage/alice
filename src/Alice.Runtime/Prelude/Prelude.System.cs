using System.Text.Json.Nodes;

namespace Alice.Runtime;

/// <summary>Files, time, environment, JSON as free functions (no `File` module: it would clash with System.IO.File).</summary>
public static partial class Prelude
{
    public static string read_file(string path) => System.IO.File.ReadAllText(path);
    public static void write_file(string path, string text) => System.IO.File.WriteAllText(path, text);
    public static bool file_exists_q(string path) => System.IO.File.Exists(path);
    public static string[] read_lines(string path) => System.IO.File.ReadAllLines(path);
    public static void sleep(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));
    public static DateTime now() => DateTime.Now;
    public static string? env(string name) => Environment.GetEnvironmentVariable(name);
    /// <summary>AOT-safe JSON: primitives, strings, IDictionary, IEnumerable; other objects serialize via ToString.</summary>
    public static string to_json<T>(T value) => ToNode(value)?.ToJsonString() ?? "null";

    static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        string s => JsonValue.Create(s),
        char c => JsonValue.Create(c.ToString()),
        bool b => JsonValue.Create(b),
        int or long or short or sbyte or byte or ushort or uint => JsonValue.Create(Convert.ToInt64(v, Inv)),
        ulong u => JsonValue.Create(u),
        float or double or decimal => JsonValue.Create(Convert.ToDouble(v, Inv)),
        System.Collections.IDictionary d => DictNode(d),
        System.Collections.IEnumerable e => new JsonArray(e.Cast<object?>().Select(ToNode).ToArray()),
        _ => JsonValue.Create(str(v)),
    };

    static JsonObject DictNode(System.Collections.IDictionary d)
    {
        var o = new JsonObject();
        foreach (System.Collections.DictionaryEntry kv in d) o[str(kv.Key)] = ToNode(kv.Value);
        return o;
    }

    public static JsonNode parse_json(string text) => JsonNode.Parse(text) ?? throw new FormatException("JSON null is not a document");
}
