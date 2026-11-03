using System.Text;
using Alice.Core.Json;
using Alice.Runtime;
using JsonDocument = Alice.Core.Json.JsonDocument;
using static Alice.Tests.Runtime.JsonTestUtil;

namespace Alice.Tests.Runtime;

/// <summary>JsonDocument / JsonAny / JsonProperty, the STJ differential on the corpus, Prelude JSON rows.</summary>
public class JsonDomTests
{
    const string Sample = """
        {"id": 7, "name": "Alé \"q\"", "tags": ["a", "b", "c"], "nested": {"x": [1, 2.5, -3e2], "ok": true, "no": null},
         "big": 18446744073709551615, "dup": 1, "dup": 2, "": "empty", "esc\nkey": "v"}
        """;

    [Fact]
    public void Indexers_lookups_and_kinds()
    {
        using var doc = JsonDocument.Parse(Sample);
        var r = doc.Root;
        Assert.Equal(JsonKind.Object, r.Kind);
        Assert.Equal(7, r["id"].GetInt64());
        Assert.Equal(7, r["id"u8].GetInt64());
        Assert.Equal("Alé \"q\"", r["name"].GetString());
        Assert.Equal("Alé \"q\"", Encoding.UTF8.GetString(r["name"].GetUtf8()));
        Assert.Equal(3, r["tags"].GetArrayLength());
        Assert.Equal("c", r["tags"][2].GetString());
        Assert.Equal("c", r["tags"][-1].GetString());
        Assert.True(r["tags"][3].IsUndefined);
        Assert.True(r["missing"].IsUndefined);
        Assert.True(r["id"]["x"].IsUndefined);
        Assert.True(r["id"][0].IsUndefined);
        Assert.Equal(-300.0, r["nested"]["x"][2].GetDouble());
        Assert.Equal(2.5, r["nested"]["x"][1].GetDouble());
        Assert.True(r["nested"]["ok"].GetBoolean());
        Assert.True(r["nested"]["no"].IsNull);
        Assert.Equal(JsonKind.UInt64, r["big"].Kind);
        Assert.Equal(2, r["dup"].GetInt64()); // last wins
        Assert.Equal("empty", r[""].GetString());
        Assert.Equal("v", r["esc\nkey"].GetString());
        Assert.True(r.TryGetProperty("esc\nkey".AsSpan(), out var v) && v.GetString() == "v");
        Assert.False(r.TryGetProperty("nope"u8, out _));
        Assert.Equal(9, r.GetPropertyCount());
        Assert.Equal(JsonKind.Undefined, default(JsonAny).Kind);
        Assert.Throws<InvalidOperationException>(() => r["id"].GetString());
    }

    [Fact]
    public void Enumerators_follow_document_order()
    {
        using var doc = JsonDocument.Parse(Sample);
        var names = new List<string>();
        foreach (var p in doc.Root.EnumerateObject()) names.Add(p.Name);
        Assert.Equal(["id", "name", "tags", "nested", "big", "dup", "dup", "", "esc\nkey"], names);
        var tags = new List<string>();
        foreach (var e in doc.Root["tags"].EnumerateArray()) tags.Add(e.GetString());
        Assert.Equal(["a", "b", "c"], tags);
        foreach (var p in doc.Root.EnumerateObject())
            if (p.NameEquals("tags"u8)) Assert.Equal(JsonKind.Array, p.Value.Kind);
    }

    [Fact]
    public void Ruby_shapes()
    {
        using var doc = JsonDocument.Parse(Sample);
        var h = doc.Root.ToDictionary();
        Assert.Equal(7L, h["id"]);
        Assert.Equal(2L, h["dup"]);
        Assert.Equal(ulong.MaxValue, h["big"]);
        var nested = (Dictionary<string, object?>)h["nested"]!;
        Assert.Equal(new object?[] { 1L, 2.5, -300.0 }, (object?[])nested["x"]!);
        Assert.Null(nested["no"]);
        Assert.Equal(true, nested["ok"]);
        Assert.Equal(new object?[] { "a", "b", "c" }, doc.Root["tags"].ToArray());
    }

    [Fact]
    public void ToJson_reserializes_minified()
    {
        using var doc = JsonDocument.Parse(" { \"a\" : [ 1 , 2.50 , \"x\\ny\" , {} , [] , null , true ] , \"b\\u0041\" : -0 } ");
        Assert.Equal("{\"a\":[1,2.50,\"x\\ny\",{},[],null,true],\"b\\u0041\":-0}", doc.Root.ToJson());
        Assert.Equal("[1,2.50,\"x\\ny\",{},[],null,true]", doc.Root["a"].ToJson());
        Assert.Equal("x\ny", doc.Root["a"][2].ToString());
        Assert.Equal(-0, doc.Root["bA"].GetInt64());
    }

    [Fact]
    public void Corpus_equals_System_Text_Json()
    {
        int seen = 0;
        foreach (var name in CorpusNames)
        {
            var bytes = Corpus(name);
            if (bytes is null) continue;
            seen++;
            using var ours = JsonDocument.Parse(bytes);
            using var stj = System.Text.Json.JsonDocument.Parse(bytes);
            AssertSame(ours.Root, stj.RootElement, name);
            // re-serialized text parses back to the same value
            using var again = System.Text.Json.JsonDocument.Parse(ours.Root.ToJson());
            AssertSame(ours.Root, again.RootElement, name + " (round trip)");
            Assert.Equal(WalkDocument(bytes), CountValues(stj.RootElement));
        }
        // seen == 0: benchmarks/core/data is missing (benchmarks/core/fetch-corpus.sh); the suite files still run elsewhere
    }

    static long CountValues(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => 1 + e.EnumerateObject().Sum(p => CountValues(p.Value)),
        System.Text.Json.JsonValueKind.Array => 1 + e.EnumerateArray().Sum(CountValues),
        _ => 1,
    };

    [Fact]
    public void Mutated_documents_agree_with_System_Text_Json_on_accept_and_reject()
    {
        var seeds = Accepted().Select(a => a.Bytes).Where(b => b.Length < 4000).ToList();
        seeds.Add(Encoding.UTF8.GetBytes(Sample));
        var rng = new Random(42);
        const string pool = "{}[]:,\"\\ 0123456789-+.eEtrufalsn\t\n/*x";
        for (int round = 0; round < 20000; round++)
        {
            var s = seeds[rng.Next(seeds.Count)].ToList();
            int edits = rng.Next(1, 4);
            for (int k = 0; k < edits && s.Count > 0; k++)
            {
                int at = rng.Next(s.Count);
                switch (rng.Next(3))
                {
                    case 0: s.RemoveAt(at); break;
                    case 1: s.Insert(at, (byte)pool[rng.Next(pool.Length)]); break;
                    default: s[at] = (byte)pool[rng.Next(pool.Length)]; break;
                }
            }
            var bytes = s.ToArray();
            // System.Text.Json does not validate UTF-8 inside strings; RFC 8259 requires it and we check it
            bool stj = System.Text.Unicode.Utf8.IsValid(bytes) && StjAccepts(bytes);
            bool ours;
            try { using var d = JsonDocument.Parse(bytes); ours = true; }
            catch (JsonParseException) { ours = false; }
            if (stj != ours) Assert.Fail($"round {round}: STJ {(stj ? "accepts" : "rejects")}, ours {(ours ? "accepts" : "rejects")}: {Encoding.UTF8.GetString(bytes)}");
        }
    }

    [Fact]
    public void Comments_only_with_AllowComments()
    {
        const string text = "// head\n{\"a\": /* in */ 1, \"s\": \"// not a comment /* */\"} /* tail */";
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse(text));
        using var doc = JsonDocument.Parse(text, new JsonParseOptions(AllowComments: true));
        Assert.Equal(1, doc.Root["a"].GetInt64());
        Assert.Equal("// not a comment /* */", doc.Root["s"].GetString());
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse("[1 /* open", new JsonParseOptions(AllowComments: true)));
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse("[1 / 2]", new JsonParseOptions(AllowComments: true)));
        Assert.Equal(2, JsonMap.Parse("[1, // c\n 2]", (ref JsonIterator it) =>
        {
            it.ReadStartArray();
            long n = 0;
            while (it.NextElement()) n += it.ReadInt64() > 0 ? 1 : 0;
            return n;
        }, new JsonParseOptions(AllowComments: true)));
    }

    [Fact]
    public void Depth_limit()
    {
        string deep = new string('[', 1024) + new string(']', 1024);
        using (JsonDocument.Parse(deep)) { }
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse("[" + deep + "]"));
        using (JsonDocument.Parse("[[[1]]]", new JsonParseOptions(MaxDepth: 3))) { }
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse("[[[[1]]]]", new JsonParseOptions(MaxDepth: 3)));
        string deeper = new string('[', 5000) + new string(']', 5000);
        using (JsonDocument.Parse(deeper, new JsonParseOptions(MaxDepth: 5000))) { }
    }

    [Fact]
    public void Errors_carry_offsets()
    {
        Assert.Equal(4, Assert.Throws<JsonParseException>(() => JsonDocument.Parse("[1, ]")).Offset);
        Assert.Equal(3, Assert.Throws<JsonParseException>(() => JsonDocument.Parse("   ")).Offset);
        Assert.Equal(5, Assert.Throws<JsonParseException>(() => JsonDocument.Parse("{\"a\" 1}")).Offset);
        Assert.Equal(4, Assert.Throws<JsonParseException>(() => JsonDocument.Parse("[1] 2")).Offset);
    }

    [Fact]
    public void A_leading_byte_order_mark_is_skipped()
    {
        using (var d = JsonDocument.Parse(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'[', (byte)'1', (byte)']' })) Assert.Equal(1, d.Root[0].GetInt64());
        using (var d = JsonDocument.Parse("﻿{\"a\":2}")) Assert.Equal(2, d.Root["a"].GetInt64());
        Assert.Equal(1, WalkDocument(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'7' }));
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public void Disposed_document_refuses_access()
    {
        var doc = JsonDocument.Parse("[1]");
        var root = doc.Root;
        doc.Dispose();
        doc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => root.Kind);
        Assert.Throws<ObjectDisposedException>(() => doc.Root);
    }

    [Fact]
    public void Strings_longer_than_the_length_field()
    {
        string big = new string('x', 16_800_000); // longer than the tape's 24-bit length field
        using (var doc = JsonDocument.Parse("[\"" + big + "\",\"a\\nb" + big + "\"]"))
        {
            Assert.Equal(big, doc.Root[0].GetString());
            Assert.Equal("a\nb" + big, doc.Root[1].GetString());
        }
    }

    [Fact]
    public void Stj_interop()
    {
        using var doc = JsonDocument.Parse(Sample);
        var e = doc.Root.ToJsonElement();
        Assert.Equal(7, e.GetProperty("id").GetInt64());
        Assert.Equal("Alé \"q\"", e.GetProperty("name").GetString());
        Assert.Equal(7, Prelude.parse_json(Sample)["id"].GetInt64()); // duplicates: last wins (JsonNode.Parse would throw)
        const string plain = """{"a":[1,2.5,"x",{"b":null}],"c":true}""";
        Assert.Equal(2, Prelude.parse_json(Sample)["dup"].GetInt64());
        Assert.Equal(2, Prelude.parse_json(Sample).ToJsonElement().GetProperty("dup").GetInt64());
        Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(plain)!.ToJsonString(), System.Text.Json.Nodes.JsonNode.Parse(Prelude.parse_json(plain).ToJsonElement().GetRawText())!.ToJsonString());
        Assert.Equal(plain, Prelude.parse_json(plain).ToJson());
    }

    [Fact]
    public void Prelude_JSON_module_and_ruby_sugar()
    {
        var r = JSON.parse(Sample);
        Assert.Equal(7, r["id"].as_i());
        Assert.Equal(2.5, r["nested"]["x"][1].as_f());
        Assert.Equal("a", r.dig("tags", 0).as_s());
        Assert.Equal(-300.0, r.dig("nested", "x", 2L).as_f());
        Assert.True(r.dig("nested", "missing", 3).nil_q());
        Assert.True(r["nested"]["no"].nil_q());
        Assert.True(r["nested"]["ok"].as_bool());
        Assert.Equal(3, r["tags"].as_a().Length);
        Assert.Equal(3, r["tags"].size());
        Assert.Equal(["x", "ok", "no"], r["nested"].keys());
        Assert.Equal(2L, r["nested"].as_h()["x"].as_a()[0].as_i() + 1);
        var seen = new List<string>();
        r["tags"].each(t => seen.Add(t.as_s()));
        r["nested"].each_pair((k, _) => seen.Add(k));
        Assert.Equal(["a", "b", "c", "x", "ok", "no"], seen);
        Assert.Equal(1, JSON.parse("[1 /* c */]", allow_comments: true)[0].as_i());
        Assert.Equal("{\"a\":[1,2.5,null,\"é\\\"\"],\"b\":true}", Prelude.to_json(new Dictionary<string, object?> { ["a"] = new object?[] { 1, 2.5, null, "é\"" }, ["b"] = true }));
        Assert.Equal("[1.0,2]", Prelude.to_json(new object[] { 1.0, 2L }));
        Assert.Equal("{\n  \"a\": [\n    1\n  ],\n  \"b\": {}\n}", JSON.pretty_generate(new Dictionary<string, object> { ["a"] = new[] { 1 }, ["b"] = new Dictionary<string, int>() }));
        Assert.Equal("{\"id\":7}", Prelude.to_json(JSON.parse("{ \"id\" : 7 }")));
        Assert.Equal("\"x\"", Prelude.to_json('x'));
        Assert.Equal("null", Prelude.to_json<object?>(null));
    }
}
