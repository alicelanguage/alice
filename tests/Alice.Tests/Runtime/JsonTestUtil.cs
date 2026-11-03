using System.Reflection;
using System.Text.Json;
using Alice.Core.Json;
using JsonDocument = Alice.Core.Json.JsonDocument;

namespace Alice.Tests.Runtime;

/// <summary>Shared helpers of the Json*Tests: paths, the System.Text.Json oracle, internal hooks.</summary>
static class JsonTestUtil
{
    public static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Alice.sln not found");
        }
    }

    /// <summary>tests/Alice.Tests/Runtime/JsonTestSuite/test_parsing (nst/JSONTestSuite, MIT).</summary>
    public static string SuiteDir => Path.Combine(RepoRoot, "tests", "Alice.Tests", "Runtime", "JsonTestSuite", "test_parsing");

    /// <summary>benchmarks/core/data (fetch-corpus.sh; gitignored, may be absent).</summary>
    public static string CorpusDir => Path.Combine(RepoRoot, "benchmarks", "core", "data");

    public static readonly string[] CorpusNames = ["twitter", "canada", "citm_catalog", "github_events", "gsoc-2018", "mesh"];

    public static byte[]? Corpus(string name)
    {
        var p = Path.Combine(CorpusDir, name + ".json");
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    /// <summary>Small documents always available: the y_ files of the suite plus hand-written ones.</summary>
    public static IEnumerable<(string Name, byte[] Bytes)> Accepted()
    {
        foreach (var f in Directory.GetFiles(SuiteDir, "y_*.json").OrderBy(x => x, StringComparer.Ordinal))
            yield return (Path.GetFileName(f), File.ReadAllBytes(f));
    }

    public static bool StjAccepts(byte[] bytes)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 1024 });
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Asserts our value equals the STJ element: kinds, numbers bit for bit, strings, member order.</summary>
    public static void AssertSame(JsonAny ours, JsonElement stj, string path = "$")
    {
        switch (stj.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.True(ours.Kind == JsonKind.Object, $"{path}: {ours.Kind} vs object");
                var a = ours.EnumerateObject().GetEnumerator();
                foreach (var p in stj.EnumerateObject())
                {
                    Assert.True(a.MoveNext(), $"{path}: fewer properties");
                    Assert.Equal(p.Name, a.Current.Name);
                    AssertSame(a.Current.Value, p.Value, path + "." + p.Name);
                }
                Assert.False(a.MoveNext(), $"{path}: more properties");
                Assert.Equal(stj.EnumerateObject().Count(), ours.GetPropertyCount());
                break;
            case JsonValueKind.Array:
                Assert.True(ours.Kind == JsonKind.Array, $"{path}: {ours.Kind} vs array");
                int i = 0;
                var e = ours.EnumerateArray().GetEnumerator();
                foreach (var x in stj.EnumerateArray())
                {
                    Assert.True(e.MoveNext(), $"{path}: fewer elements");
                    AssertSame(e.Current, x, $"{path}[{i++}]");
                }
                Assert.False(e.MoveNext(), $"{path}: more elements");
                Assert.Equal(stj.GetArrayLength(), ours.GetArrayLength());
                break;
            case JsonValueKind.String:
                Assert.True(ours.Kind == JsonKind.String, $"{path}: {ours.Kind} vs string");
                string s;
                try { s = stj.GetString()!; }
                catch (InvalidOperationException) { return; } // lone surrogate: STJ refuses to decode, we give U+FFFD
                Assert.Equal(s, ours.GetString());
                break;
            case JsonValueKind.Number:
                switch (ours.Kind)
                {
                    case JsonKind.Int64: Assert.True(stj.TryGetInt64(out long l) && l == ours.GetInt64(), $"{path}: int64"); break;
                    case JsonKind.UInt64: Assert.True(stj.TryGetUInt64(out ulong u) && u == ours.GetUInt64(), $"{path}: uint64"); break;
                    case JsonKind.Float64:
                        double d = double.Parse(stj.GetRawText(), System.Globalization.CultureInfo.InvariantCulture);
                        Assert.True(BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(ours.GetDouble()), $"{path}: {stj.GetRawText()} -> {ours.GetDouble():R}");
                        break;
                    default: Assert.Fail($"{path}: {ours.Kind} vs number"); break;
                }
                break;
            case JsonValueKind.True: Assert.True(ours.Kind == JsonKind.Boolean && ours.GetBoolean(), path); break;
            case JsonValueKind.False: Assert.True(ours.Kind == JsonKind.Boolean && !ours.GetBoolean(), path); break;
            case JsonValueKind.Null: Assert.True(ours.IsNull, path); break;
        }
    }

    /// <summary>Visits every value with the on-demand iterator; returns a count of values (forces full validation of
    /// the grammar, which the iterator otherwise checks lazily).</summary>
    public static long Walk(ref JsonIterator it)
    {
        switch (it.PeekKind())
        {
            case JsonKind.Object:
                {
                    long n = 1;
                    it.ReadStartObject();
                    while (it.NextProperty(out _)) n += Walk(ref it);
                    return n;
                }
            case JsonKind.Array:
                {
                    long n = 1;
                    it.ReadStartArray();
                    while (it.NextElement()) n += Walk(ref it);
                    return n;
                }
            case JsonKind.String: it.ReadString(); return 1;
            case JsonKind.Boolean: it.ReadBoolean(); return 1;
            case JsonKind.Null: it.TryReadNull(); return 1;
            case JsonKind.Int64 or JsonKind.UInt64 or JsonKind.Float64: it.ReadDouble(); return 1;
            default: throw new JsonParseException("not a value", it.Offset);
        }
    }

    /// <summary>Full on-demand walk of a document plus the end check; throws JsonParseException when invalid.</summary>
    public static long WalkDocument(byte[] bytes, JsonParseOptions options = default) => JsonMap.Parse<long>(bytes, Walk, options);

    // ---- internal hooks (no InternalsVisibleTo: reflection) ----

    static readonly Type Index = typeof(JsonDocument).Assembly.GetType("Alice.Core.Json.StructuralIndex", true)!;
    static readonly FieldInfo ForceField = Index.GetField("Force", BindingFlags.NonPublic | BindingFlags.Static)!;

    public delegate int BuildIndex(ReadOnlySpan<byte> input, Span<uint> positions);

    public static readonly BuildIndex Build = Index.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "Build" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 2).CreateDelegate<BuildIndex>();

    /// <summary>Tier names in StructuralIndex.Tier order (Auto, Avx512, Avx2, Vector128, Scalar).</summary>
    public static readonly string[] Tiers = ["Avx512", "Avx2", "Vector128", "Scalar"];

    /// <summary>Runs <paramref name="body"/> with stage 1 forced to <paramref name="tier"/> (all tiers give identical
    /// results, so concurrent tests are unaffected).</summary>
    public static T WithTier<T>(string tier, Func<T> body)
    {
        lock (ForceField)
        {
            var old = ForceField.GetValue(null);
            ForceField.SetValue(null, Enum.Parse(ForceField.FieldType, tier));
            try { return body(); }
            finally { ForceField.SetValue(null, old); }
        }
    }

    public static bool TierSupported(string tier) => tier switch
    {
        "Avx512" => System.Runtime.Intrinsics.X86.Avx512BW.IsSupported && System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated,
        "Avx2" => System.Runtime.Intrinsics.X86.Avx2.IsSupported,
        "Vector128" => System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated,
        _ => true,
    };
}
