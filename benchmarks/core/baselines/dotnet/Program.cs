// .NET comparators for benchmarks/core. Same protocol as baselines/rust and baselines/simdjson:
//   RESULT <engine> <case> <input> <median_ms> <MB_per_s> <alloc_bytes_per_call> <checksum>
// Warm-up >= WARM_MS ms (default 1000) and >= WARM_N calls (default 50) (tier-1 + PGO, benchmarks/README.md); TIMED calls (default 15),
// median reported. Allocation = GC.GetAllocatedBytesForCurrentThread delta, mean over the timed calls. ONCE=1: cold.
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoreBaselines;

static void Measure(string engine, string kase, string input, long bytes, Func<long> f)
{
    bool once = Environment.GetEnvironmentVariable("ONCE") is not null;
    double warmMs = double.TryParse(Environment.GetEnvironmentVariable("WARM_MS"), out var w) ? w : 1000;
    int warmN = int.TryParse(Environment.GetEnvironmentVariable("WARM_N"), out var wn) ? wn : 50;
    int timed = int.TryParse(Environment.GetEnvironmentVariable("TIMED"), out var t) ? t : 15;
    long reference = f();
    if (!once)
    {
        var sw0 = Stopwatch.StartNew();
        for (int n = 0; n < warmN || sw0.Elapsed.TotalMilliseconds < warmMs; n++)
            if (f() != reference) throw new Exception("checksum changed in warm-up");
    }
    int runs = once ? 1 : timed;
    var ts = new double[runs];
    long alloc = 0;
    for (int i = 0; i < runs; i++)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        long s = Stopwatch.GetTimestamp();
        long c = f();
        ts[i] = Stopwatch.GetElapsedTime(s).TotalMilliseconds;
        alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
        if (c != reference) throw new Exception("checksum changed");
    }
    Array.Sort(ts);
    double med = ts[runs / 2];
    Console.WriteLine($"RESULT {engine} {kase} {input} {med:F4} {bytes / 1e6 / (med / 1e3):F1} {alloc / runs} {reference}");
}

static string NameOf(string path) => Path.GetFileNameWithoutExtension(path);

static void JsonDom(string path)
{
    byte[] data = File.ReadAllBytes(path);
    string name = NameOf(path);
    Measure("stj/JsonDocument", "json-dom", name, data.Length, () =>
    {
        using var doc = JsonDocument.Parse(data);
        var r = doc.RootElement;
        return r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : r.ValueKind == JsonValueKind.Object ? r.EnumerateObject().Count() : 1;
    });
    Measure("stj/JsonNode", "json-dom", name, data.Length, () =>
    {
        var n = System.Text.Json.Nodes.JsonNode.Parse(data)!;
        return n is System.Text.Json.Nodes.JsonArray a ? a.Count : n is System.Text.Json.Nodes.JsonObject o ? o.Count : 1;
    });
    Measure("stj/Utf8JsonReader", "json-walk", name, data.Length, () =>
    {
        var rd = new Utf8JsonReader(data);
        long tokens = 0;
        while (rd.Read()) tokens++;
        return tokens;
    });
}

static void JsonTyped(string kind, string path)
{
    byte[] data = File.ReadAllBytes(path);
    string name = NameOf(path);
    var buf = new ArrayBufferWriter<byte>(data.Length * 2);
    void Ser<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
    {
        buf.ResetWrittenCount();
        using var wr = new Utf8JsonWriter(buf);
        JsonSerializer.Serialize(wr, value, ti);
        long outLen = buf.WrittenCount;
        Measure("stj/sourcegen", "json-serialize", name, outLen, () =>
        {
            buf.ResetWrittenCount();
            wr.Reset(buf);
            JsonSerializer.Serialize(wr, value, ti);
            wr.Flush();
            return buf.WrittenCount;
        });
    }
    switch (kind)
    {
        case "twitter":
            Measure("stj/sourcegen", "json-typed", name, data.Length, () =>
            {
                var v = JsonSerializer.Deserialize(data, SnakeCtx.Default.Twitter)!;
                long s = v.Statuses.Count;
                foreach (var st in v.Statuses) s += (long)st.User.FollowersCount;
                return s;
            });
            Ser(JsonSerializer.Deserialize(data, SnakeCtx.Default.Twitter)!, SnakeCtx.Default.Twitter);
            break;
        case "canada":
            Measure("stj/sourcegen", "json-typed", name, data.Length, () =>
            {
                var v = JsonSerializer.Deserialize(data, CamelCtx.Default.Canada)!;
                long s = 0;
                foreach (var f in v.Features) foreach (var ring in f.Geometry.Coordinates) s += ring.Count;
                return s;
            });
            Ser(JsonSerializer.Deserialize(data, CamelCtx.Default.Canada)!, CamelCtx.Default.Canada);
            break;
        case "citm_catalog":
            Measure("stj/sourcegen", "json-typed", name, data.Length, () =>
            {
                var v = JsonSerializer.Deserialize(data, CamelCtx.Default.Citm)!;
                return v.Performances.Count + v.Events.Count;
            });
            Ser(JsonSerializer.Deserialize(data, CamelCtx.Default.Citm)!, CamelCtx.Default.Citm);
            break;
        default: throw new ArgumentException(kind);
    }
}

string[] variants =
[
    "agggtaaa|tttaccct", "[cgt]gggtaaa|tttaccc[acg]", "a[act]ggtaaa|tttacc[agt]t", "ag[act]gtaaa|tttac[agt]ct",
    "agg[act]taaa|ttta[agt]cct", "aggg[acg]aaa|ttt[cgt]ccct", "agggt[cgt]aa|tt[acg]accct", "agggta[cgt]a|t[acg]taccct",
    "agggtaa[cgt]|[acg]ttaccct",
];
(string, string)[] subst = [("tHa[Nt]", "<4>"), ("aND|caN|Ha[DS]|WaS", "<3>"), ("a[NSt]|BY", "<2>"), ("<[^>]*>", "|"), ("\\|[^|][^|]*\\|", "-")];
(string Name, string Pattern)[] rebar =
[
    ("literal", "Sherlock Holmes"),
    ("literal-alt", "Sherlock Holmes|John Watson|Irene Adler|Inspector Lestrade|Professor Moriarty"),
    ("literal-i", "(?i)Sherlock Holmes"),
    ("words", "\\b\\w+\\b"),
    ("ing-suffix", "[a-zA-Z]+ing"),
    ("aws-keys", "(?:AKIA|ASIA)[A-Z0-9]{16}"),
    ("bounded-repeat", "[A-Za-z]{8,13}"),
    ("unicode-upper", "\\p{Lu}\\p{Ll}+"),
    ("time", "[0-9]{1,2}:[0-9]{2}"),
];
(string Name, RegexOptions Options)[] engines =
[
    ("dotnet/interpreted", RegexOptions.None), // what Prelude.regex uses today (Prelude.Syntax.cs)
    ("dotnet/compiled", RegexOptions.Compiled),
    ("dotnet/nonbacktracking", RegexOptions.NonBacktracking),
];

void RegexRedux(string path, string only)
{
    byte[] bytes = File.ReadAllBytes(path);
    string input = Encoding.Latin1.GetString(bytes);
    foreach (var (eng, opt) in engines)
    {
        if (only != "" && !eng.EndsWith(only)) continue;
        var strip = new Regex(">.*\n|\n", opt);
        var vs = variants.Select(p => new Regex(p, opt)).ToArray();
        var ss = subst.Select(p => (new Regex(p.Item1, opt), p.Item2)).ToArray();
        bool printed = false;
        Measure(eng, "regex-redux", "fasta-5M", bytes.Length, () =>
        {
            string seq = strip.Replace(input, "");
            var sb = new StringBuilder();
            long sum = 0;
            for (int i = 0; i < vs.Length; i++) { int c = vs[i].Count(seq); sum += c; sb.Append(variants[i]).Append(' ').Append(c).Append('\n'); }
            string s = seq;
            foreach (var (r, rep) in ss) s = r.Replace(s, rep);
            sb.Append($"\n{input.Length}\n{seq.Length}\n{s.Length}\n");
            if (!printed) { Console.Error.Write(sb); printed = true; }
            return sum * 1_000_000_000 + s.Length;
        });
    }
}

void Rebar(string path)
{
    byte[] bytes = File.ReadAllBytes(path);
    string hay = Encoding.UTF8.GetString(bytes);
    string name = NameOf(path);
    foreach (var (eng, opt) in engines)
        foreach (var (kase, pat) in rebar)
        {
            var r = new Regex(pat, opt);
            Measure(eng, "count:" + kase, name, bytes.Length, () => r.Count(hay.AsSpan()));
        }
}

switch (args.ElementAtOrDefault(0))
{
    case "json-dom": JsonDom(args[1]); break;
    case "json-typed": JsonTyped(args[1], args[2]); break;
    case "regex-redux": RegexRedux(args[1], args.ElementAtOrDefault(2) ?? ""); break;
    case "rebar": Rebar(args[1]); break;
    default:
        Console.Error.WriteLine("usage: core-baselines json-dom <file> | json-typed <twitter|canada|citm_catalog> <file> | regex-redux <file> [engine] | rebar <file>");
        return 2;
}
return 0;
