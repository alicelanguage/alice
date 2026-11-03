using System.Text;
using Alice.Core.Json;
using static Alice.Tests.Runtime.JsonTestUtil;

namespace Alice.Tests.Runtime;

/// <summary>JsonIterator (on-demand) and the JsonMap mapping pieces.</summary>
public class JsonIteratorTests
{
    sealed record Point(long X, double Y, string? Label, List<long> Tags);

    static Point ReadPoint(ref JsonIterator it)
    {
        long x = 0; double y = 0; string? label = null; List<long> tags = [];
        it.ReadStartObject();
        while (it.NextProperty(out var name))
        {
            if (name.SequenceEqual("x"u8)) x = it.ReadInt64();
            else if (name.SequenceEqual("y"u8)) y = it.ReadDouble();
            else if (name.SequenceEqual("label"u8)) label = JsonMap.ReadOrNull(ref it, JsonMap.ReadString);
            else if (name.SequenceEqual("tags"u8)) tags = JsonMap.ReadList(ref it, JsonMap.ReadInt64);
            else it.Skip();
        }
        return new Point(x, y, label, tags);
    }

    static void WritePoint(ref JsonWriter w, Point p)
    {
        w.WriteStartObject();
        w.WritePropertyName("x"u8); w.WriteNumber(p.X);
        w.WritePropertyName("y"u8); w.WriteNumber(p.Y);
        w.WritePropertyName("label"u8); JsonMap.WriteString(ref w, p.Label);
        w.WritePropertyName("tags"u8); JsonMap.WriteList(ref w, p.Tags, JsonMap.WriteInt64);
        w.WriteEndObject();
    }

    [Fact]
    public void Typed_read_any_field_order_unknown_fields_skipped()
    {
        var p = JsonMap.Parse("{\"skip\":{\"deep\":[1,{\"a\":[]}]},\"tags\":[3,4],\"label\":null,\"y\":2.5,\"x\":-7,\"z\":\"q\"}", ReadPoint);
        Assert.Equal(new Point(-7, 2.5, null, p.Tags), p);
        Assert.Equal([3L, 4L], p.Tags);
        string json = JsonMap.ToJson(p, WritePoint);
        Assert.Equal("{\"x\":-7,\"y\":2.5,\"label\":null,\"tags\":[3,4]}", json);
        Assert.Equal(json, JsonMap.ToJson(JsonMap.Parse(json, ReadPoint), WritePoint));
        var list = JsonMap.Parse("[{\"x\":1,\"y\":1,\"label\":\"a\\u00e9\",\"tags\":[]},{\"x\":2,\"y\":0,\"label\":\"b\",\"tags\":[9]}]",
            (ref JsonIterator it) => JsonMap.ReadArray(ref it, ReadPoint));
        Assert.Equal("aé", list[0].Label);
        Assert.Equal(2, list.Length);
        var map = JsonMap.Parse("{\"a\":1,\"b\\n\":2,\"a\":3}", (ref JsonIterator it) => JsonMap.ReadHash(ref it, JsonMap.ReadInt32));
        Assert.Equal(3, map["a"]);
        Assert.Equal(2, map["b\n"]);
        Assert.Equal("{\"a\":3,\"b\\n\":2}", JsonMap.ToJson(map, (ref JsonWriter w, Dictionary<string, int> m) => JsonMap.WriteHash(ref w, m, JsonMap.WriteInt32)));
        Assert.Null(JsonMap.Parse("null", (ref JsonIterator it) => JsonMap.ReadNullable(ref it, JsonMap.ReadInt64)));
        Assert.Equal(5, JsonMap.Parse("5", (ref JsonIterator it) => JsonMap.ReadNullable(ref it, JsonMap.ReadInt64)));
    }

    abstract record Shape;
    sealed record Circle(double R) : Shape;
    sealed record Rect(double W, double H) : Shape;

    static Shape ReadShape(ref JsonIterator it)
    {
        var tag = JsonMap.PeekTag(ref it, "type"u8);
        bool circle = tag.SequenceEqual("Circle"u8);
        if (!circle && !tag.SequenceEqual("Rect"u8)) throw new JsonParseException("unknown Shape", it.Offset);
        double r = 0, w = 0, h = 0;
        it.ReadStartObject();
        while (it.NextProperty(out var name))
        {
            if (name.SequenceEqual("r"u8)) r = it.ReadDouble();
            else if (name.SequenceEqual("w"u8)) w = it.ReadDouble();
            else if (name.SequenceEqual("h"u8)) h = it.ReadDouble();
            else it.Skip();
        }
        return circle ? new Circle(r) : new Rect(w, h);
    }

    static void WriteShape(ref JsonWriter w, Shape s)
    {
        w.WriteStartObject();
        switch (s)
        {
            case Circle c: JsonMap.WriteTag(ref w, "type"u8, "Circle"u8); w.WritePropertyName("r"u8); w.WriteNumber(c.R); break;
            case Rect r: JsonMap.WriteTag(ref w, "type"u8, "Rect"u8); w.WritePropertyName("w"u8); w.WriteNumber(r.W); w.WritePropertyName("h"u8); w.WriteNumber(r.H); break;
        }
        w.WriteEndObject();
    }

    [Fact]
    public void Sum_types_use_a_type_tag_anywhere_in_the_object()
    {
        var shapes = JsonMap.Parse("[{\"r\":2,\"type\":\"Circle\"},{\"type\":\"Rect\",\"w\":1,\"h\":3}]", (ref JsonIterator it) => JsonMap.ReadList(ref it, ReadShape));
        Assert.Equal([new Circle(2), new Rect(1, 3)], shapes);
        Assert.Equal("[{\"type\":\"Circle\",\"r\":2.0},{\"type\":\"Rect\",\"w\":1.0,\"h\":3.0}]",
            JsonMap.ToJson(shapes, (ref JsonWriter w, List<Shape> l) => JsonMap.WriteList(ref w, l, WriteShape)));
        Assert.Throws<JsonParseException>(() => JsonMap.Parse("{\"type\":\"Hex\"}", ReadShape));
    }

    [Fact]
    public void Peek_depth_offset_raw_strings()
    {
        var bytes = "{\"a\":[1,-2,3.5,18446744073709551615,\"s\\n\",true,null]}"u8.ToArray();
        var it = new JsonIterator(bytes);
        try
        {
            Assert.Equal(JsonKind.Object, it.PeekKind());
            Assert.Equal(0, it.Depth);
            it.ReadStartObject();
            Assert.Equal(1, it.Depth);
            Assert.True(it.NextProperty(out var name));
            Assert.Equal("a", Encoding.UTF8.GetString(name));
            Assert.Equal(5, it.Offset);
            it.ReadStartArray();
            Assert.True(it.NextElement()); Assert.Equal(JsonKind.Int64, it.PeekKind()); Assert.Equal(1, it.ReadInt64());
            Assert.True(it.NextElement()); Assert.Equal(-2, it.ReadInt64());
            Assert.True(it.NextElement()); Assert.Equal(JsonKind.Float64, it.PeekKind()); Assert.Equal(3.5, it.ReadDouble());
            Assert.True(it.NextElement()); Assert.Equal(JsonKind.UInt64, it.PeekKind()); Assert.Equal(ulong.MaxValue, it.ReadUInt64());
            Assert.True(it.NextElement());
            var raw = it.ReadRawString(out bool esc);
            Assert.True(esc);
            Assert.Equal("s\\n", Encoding.UTF8.GetString(raw));
            Assert.True(it.NextElement()); Assert.True(it.ReadBoolean());
            Assert.True(it.NextElement()); Assert.True(it.TryReadNull());
            Assert.False(it.NextElement());
            Assert.Equal(1, it.Depth);
            Assert.False(it.NextProperty(out _));
            Assert.Equal(0, it.Depth);
        }
        finally { it.Dispose(); }
    }

    [Theory]
    [InlineData("[1,]")]
    [InlineData("[,1]")]
    [InlineData("[1 2]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{,\"a\":1}")]
    [InlineData("{\"a\" 1}")]
    [InlineData("{\"a\":1 \"b\":2}")]
    [InlineData("[1]]")]
    [InlineData("[1} ")]
    [InlineData("[tru]")]
    [InlineData("[01]")]
    [InlineData("[1] [2]")]
    [InlineData("")]
    public void Grammar_errors_surface_when_read(string text) =>
        Assert.Throws<JsonParseException>(() => WalkDocument(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Type_mismatches_are_parse_errors()
    {
        Assert.Throws<JsonParseException>(() => JsonMap.Parse("\"x\"", JsonMap.ReadInt64));
        Assert.Throws<JsonParseException>(() => JsonMap.Parse("1.5", JsonMap.ReadInt64));
        Assert.Throws<JsonParseException>(() => JsonMap.Parse("-1", JsonMap.ReadUInt64));
        Assert.Throws<JsonParseException>(() => JsonMap.Parse("4294967296", JsonMap.ReadUInt32));
        Assert.Throws<JsonParseException>(() => JsonMap.Parse("[1]", JsonMap.ReadString));
        Assert.Equal(3, JsonMap.Parse("3.0", JsonMap.ReadInt64));
    }

    [Fact]
    public void Twitter_followers_sum_matches_the_DOM()
    {
        var bytes = Corpus("twitter");
        if (bytes is null) return;
        using var doc = Alice.Core.Json.JsonDocument.Parse(bytes);
        long expected = 0;
        foreach (var s in doc.Root["statuses"].EnumerateArray()) expected += s["user"]["followers_count"].GetInt64();
        Assert.Equal(expected, JsonOnDemand.SumFollowers(bytes));
        Assert.Equal(WalkDocument(bytes), WalkDocument(bytes));
    }
}

/// <summary>On-demand workloads shared with the allocation tests.</summary>
static class JsonOnDemand
{
    /// <summary>Sum of statuses[].user.followers_count, reading nothing else.</summary>
    public static long SumFollowers(ReadOnlySpan<byte> bytes)
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
