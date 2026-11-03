// Alice.Core.Json benchmarks. Protocol of benchmarks/core (README "Method"):
//   RESULT <engine> <case> <input> <median_ms> <MB_per_s> <alloc_bytes_per_call> <checksum>
// Usage: alice-json-bench <data-dir> [twitter canada ...]   (ALICE_JSON_TIER=avx512|avx2|vector128|scalar forces stage 1)
// Comparators (System.Text.Json) run in the same process on the same input.
using System.Buffers;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alice.Core.Json;
using AliceJsonBench;
using CoreBaselines;
using AJsonDocument = Alice.Core.Json.JsonDocument;

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

if (args.ElementAtOrDefault(0) == "--loop")
{
    // profiling aid: alice-json-bench --loop <typed|dom|walk|validate|write> <file> <seconds>
    byte[] d = File.ReadAllBytes(args[2]);
    var sw = Stopwatch.StartNew();
    long n = 0, sink = 0;
    var cbuf = new ArrayBufferWriter<byte>(d.Length * 2);
    var canada = args[1] == "write" ? JsonMap.Parse(d, Read.Canada) : null;
    while (sw.Elapsed.TotalSeconds < double.Parse(args[3]))
    {
        switch (args[1])
        {
            case "typed": sink += JsonMap.Parse(d, Read.Twitter).Statuses.Count; break;
            case "dom": { using var doc = AJsonDocument.Parse(d); sink += doc.Root.GetPropertyCount(); break; }
            case "walk": { var it = new JsonIterator(d); sink += Walk.Count(ref it); it.Dispose(); break; }
            case "validate": { var it = new JsonIterator(d); it.Dispose(); break; }
            case "walk-str": { var it = new JsonIterator(d); Walk.Strings = true; sink += Walk.Count(ref it); it.Dispose(); break; }
            case "write": { cbuf.ResetWrittenCount(); var w = new JsonWriter(cbuf); WriteA.Canada(ref w, canada!); w.Flush(); break; }
        }
        n++;
    }
    Console.WriteLine($"{n} iterations, {sw.Elapsed.TotalMilliseconds / n:F4} ms each ({sink})");
    return 0;
}

if (args.ElementAtOrDefault(0) == "--suite")
{
    // JSONTestSuite summary: alice-json-bench --suite tests/Alice.Tests/Runtime/JsonTestSuite/test_parsing
    int ok = 0, bad = 0;
    foreach (var f in Directory.GetFiles(args[1], "*.json").OrderBy(x => x, StringComparer.Ordinal))
    {
        string fn = Path.GetFileName(f);
        bool accepted;
        try { using var doc = AJsonDocument.Parse(File.ReadAllBytes(f)); accepted = true; }
        catch (JsonParseException) { accepted = false; }
        bool right = fn[0] == 'i' || (fn[0] == 'y') == accepted;
        if (right) ok++; else bad++;
        if (fn[0] == 'i' || !right) Console.WriteLine($"{(accepted ? "accept" : "reject")} {fn}{(right ? "" : "  WRONG")}");
    }
    Console.WriteLine($"y/n correct {ok - Directory.GetFiles(args[1], "i_*.json").Length}, wrong {bad}");
    return 0;
}

string dir = args.ElementAtOrDefault(0) ?? "benchmarks/core/data";
string[] names = args.Length > 1 ? args[1..] : ["twitter", "canada", "citm_catalog", "github_events", "gsoc-2018", "mesh"];
string tier = Environment.GetEnvironmentVariable("ALICE_JSON_TIER") ?? "auto";
Console.Error.WriteLine($"stage 1 tier: {tier}; Avx512BW {System.Runtime.Intrinsics.X86.Avx512BW.IsSupported}, Pclmulqdq {System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported}");

foreach (var name in names)
{
    string path = Path.Combine(dir, name + ".json");
    if (!File.Exists(path)) { Console.Error.WriteLine("missing " + path); continue; }
    byte[] data = File.ReadAllBytes(path);
    string sfx = tier == "auto" ? "" : "-" + tier;

    // validate: UTF-8 + stage 1 (structural index, string/escape checks); then a full skip of the root (grammar of brackets)
    Measure("alice/stage1" + sfx, "json-validate", name, data.Length, () =>
    {
        var it = new JsonIterator(data);
        long o = it.Offset;
        it.Dispose();
        return o;
    });
    Measure("bcl/Utf8.IsValid", "json-validate", name, data.Length, () => System.Text.Unicode.Utf8.IsValid(data) ? 1 : 0);
    Measure("alice/validate" + sfx, "json-validate", name, data.Length, () =>
    {
        var it = new JsonIterator(data);
        it.Skip();
        long o = it.Offset;
        it.Dispose();
        return o;
    });
    if (tier != "auto") continue;

    Measure("alice/dom", "json-dom", name, data.Length, () =>
    {
        using var doc = AJsonDocument.Parse(data);
        var r = doc.Root;
        return r.Kind == JsonKind.Array ? r.GetArrayLength() : r.Kind == JsonKind.Object ? r.GetPropertyCount() : 1;
    });
    Measure("stj/JsonDocument", "json-dom", name, data.Length, () =>
    {
        using var doc = System.Text.Json.JsonDocument.Parse(data);
        var r = doc.RootElement;
        return r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : r.ValueKind == JsonValueKind.Object ? r.EnumerateObject().Count() : 1;
    });
    Measure("alice/ondemand-walk", "json-walk", name, data.Length, () =>
    {
        var it = new JsonIterator(data);
        try { return Walk.Count(ref it); }
        finally { it.Dispose(); }
    });
    Measure("stj/Utf8JsonReader", "json-walk", name, data.Length, () =>
    {
        var rd = new Utf8JsonReader(data);
        long tokens = 0;
        while (rd.Read()) tokens++;
        return tokens;
    });

    if (name == "twitter")
    {
        Measure("alice/ondemand", "json-field-sum", name, data.Length, () => Walk.SumFollowers(data));
        Measure("alice/dom", "json-field-sum", name, data.Length, () =>
        {
            using var doc = AJsonDocument.Parse(data);
            long s = 0;
            foreach (var st in doc.Root["statuses"u8].EnumerateArray()) s += st["user"u8]["followers_count"u8].GetInt64();
            return s;
        });
        Measure("stj/JsonDocument", "json-field-sum", name, data.Length, () =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            long s = 0;
            foreach (var st in doc.RootElement.GetProperty("statuses"u8).EnumerateArray()) s += st.GetProperty("user"u8).GetProperty("followers_count"u8).GetInt64();
            return s;
        });
    }

    switch (name)
    {
        case "twitter":
            Typed(name, data, Read.Twitter, SnakeCtx.Default.Twitter, WriteA.Twitter, WriteS.Twitter,
                v => { long s = v.Statuses.Count; foreach (var st in v.Statuses) s += (long)st.User.FollowersCount; return s; });
            break;
        case "canada":
            Typed(name, data, Read.Canada, CamelCtx.Default.Canada, WriteA.Canada, WriteS.Canada,
                v => { long s = 0; foreach (var f in v.Features) foreach (var ring in f.Geometry.Coordinates) s += ring.Count; return s; });
            break;
        case "citm_catalog":
            Typed(name, data, Read.Citm, CamelCtx.Default.Citm, WriteA.Citm, WriteS.Citm, v => v.Performances.Count + v.Events.Count);
            break;
    }
}
return 0;

static void Typed<T>(string name, byte[] data, JsonRead<T> read, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti,
    JsonWrite<T> writeA, Action<Utf8JsonWriter, T> writeS, Func<T, long> check)
{
    Measure("alice/typed", "json-typed", name, data.Length, () => check(JsonMap.Parse(data, read)));
    Measure("stj/sourcegen", "json-typed", name, data.Length, () => check(JsonSerializer.Deserialize(data, ti)!));

    T value = JsonSerializer.Deserialize(data, ti)!;
    if (check(JsonMap.Parse(data, read)) != check(value)) throw new Exception("typed checksums differ");
    var buf = new ArrayBufferWriter<byte>(data.Length * 2);

    long Ours()
    {
        buf.ResetWrittenCount();
        var w = new JsonWriter(buf);
        writeA(ref w, value);
        w.Flush();
        return buf.WrittenCount;
    }
    long ourLen = Ours();
    Measure("alice/writer", "json-serialize", name, ourLen, Ours);

    var stjOut = new ArrayBufferWriter<byte>(data.Length * 2);
    var sw = new Utf8JsonWriter(stjOut);
    long Stj(Utf8JsonWriter wr, Action<Utf8JsonWriter, T> body)
    {
        stjOut.ResetWrittenCount();
        wr.Reset(stjOut);
        body(wr, value);
        wr.Flush();
        return stjOut.WrittenCount;
    }
    Measure("stj/Utf8JsonWriter", "json-serialize", name, Stj(sw, writeS), () => Stj(sw, writeS));
    var relaxed = new Utf8JsonWriter(stjOut, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true });
    Measure("stj/Utf8JsonWriter-relaxed", "json-serialize", name, Stj(relaxed, writeS), () => Stj(relaxed, writeS));
    Measure("stj/sourcegen", "json-serialize", name, Stj(sw, (wr, v) => JsonSerializer.Serialize(wr, v, ti)), () => Stj(sw, (wr, v) => JsonSerializer.Serialize(wr, v, ti)));

    // round trip: our output reads back to the same checksum through STJ and through us
    if (check(JsonSerializer.Deserialize(buf.WrittenSpan, ti)!) != check(value)) throw new Exception("alice output differs through STJ");
}

static class Walk
{
    public static bool Strings;

    public static long Count(ref JsonIterator it)
    {
        switch (it.PeekKind())
        {
            case JsonKind.Object:
                {
                    long n = 1;
                    it.ReadStartObject();
                    while (it.NextProperty(out _)) n += Count(ref it);
                    return n;
                }
            case JsonKind.Array:
                {
                    long n = 1;
                    it.ReadStartArray();
                    while (it.NextElement()) n += Count(ref it);
                    return n;
                }
            case JsonKind.String: if (Strings) return it.ReadString().Length; it.ReadRawString(out _); return 1;
            case JsonKind.Boolean: it.ReadBoolean(); return 1;
            case JsonKind.Null: it.TryReadNull(); return 1;
            default: it.ReadDouble(); return 1;
        }
    }

    public static long SumFollowers(byte[] bytes)
    {
        var it = new JsonIterator(bytes);
        try
        {
            long sum = 0;
            it.ReadStartObject();
            while (it.NextProperty(out var name))
            {
                if (!name.SequenceEqual("statuses"u8)) { it.Skip(); continue; }
                it.ReadStartArray();
                while (it.NextElement())
                {
                    it.ReadStartObject();
                    while (it.NextProperty(out var f))
                    {
                        if (!f.SequenceEqual("user"u8)) { it.Skip(); continue; }
                        it.ReadStartObject();
                        while (it.NextProperty(out var u))
                        {
                            if (u.SequenceEqual("followers_count"u8)) sum += it.ReadInt64();
                            else it.Skip();
                        }
                    }
                }
            }
            return sum;
        }
        finally { it.Dispose(); }
    }
}
