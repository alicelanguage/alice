using System.Buffers;
using System.Text;
using Alice.Core.Json;
using Xunit.Abstractions;
using static Alice.Tests.Runtime.JsonTestUtil;

namespace Alice.Tests.Runtime;

/// <summary>0 bytes per call once pools are warm: validate (stage 1 + full skip), on-demand reads, the writer.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class JsonAllocationTests(ITestOutputHelper output)
{
    static readonly byte[] Doc = Corpus("twitter") ?? Synthetic();

    static byte[] Synthetic()
    {
        var sb = new StringBuilder("{\"statuses\":[");
        for (int i = 0; i < 2000; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(i).Append(",\"text\":\"t\\u00e9xt ").Append(i).Append("\",\"user\":{\"name\":\"n\",\"followers_count\":")
              .Append(i * 3).Append(",\"f\":[1.5,true,null]}}");
        }
        return Encoding.UTF8.GetBytes(sb.Append("]}").ToString());
    }

    static long Validate(byte[] bytes)
    {
        var it = new JsonIterator(bytes);
        try
        {
            it.Skip();
            return it.Offset;
        }
        finally { it.Dispose(); }
    }

    long Measure(Action body)
    {
        for (int i = 0; i < 20; i++) body(); // warm the pools and tier-1 code
        return AllocationProbe.Least(() => { for (int i = 0; i < 10; i++) body(); }, output.WriteLine);
    }

    [Fact]
    public void Validate_allocates_nothing() => Assert.Equal(0, Measure(() => Validate(Doc)));

    [Fact]
    public void On_demand_field_sum_allocates_nothing() => Assert.Equal(0, Measure(() => JsonOnDemand.SumFollowers(Doc)));

    [Fact]
    public void Full_on_demand_walk_without_strings_allocates_nothing()
    {
        static long Walk(byte[] bytes)
        {
            var it = new JsonIterator(bytes);
            try { return Visit(ref it); }
            finally { it.Dispose(); }
        }
        Assert.Equal(0, Measure(() => Walk(Doc)));
    }

    static long Visit(ref JsonIterator it)
    {
        switch (it.PeekKind())
        {
            case JsonKind.Object:
                {
                    long n = 0;
                    it.ReadStartObject();
                    while (it.NextProperty(out var name)) n += name.Length + Visit(ref it);
                    return n;
                }
            case JsonKind.Array:
                {
                    long n = 0;
                    it.ReadStartArray();
                    while (it.NextElement()) n += Visit(ref it);
                    return n;
                }
            case JsonKind.String: return it.ReadRawString(out _).Length;
            case JsonKind.Boolean: return it.ReadBoolean() ? 1 : 0;
            case JsonKind.Null: it.TryReadNull(); return 0;
            default: return (long)it.ReadDouble();
        }
    }

    [Fact]
    public void Writer_into_a_reused_buffer_allocates_nothing()
    {
        var buf = new ArrayBufferWriter<byte>(1 << 20);
        using var doc = Alice.Core.Json.JsonDocument.Parse(Doc);
        var root = doc.Root;
        void Body()
        {
            buf.ResetWrittenCount();
            var w = new JsonWriter(buf);
            w.WriteStartObject();
            w.WritePropertyName("n"u8); w.WriteNumber(123456789L); w.WriteNumber(2.718281828459045);
            w.WritePropertyName("s"u8); w.WriteString("quote \" and é".AsSpan()); w.WriteString("utf8 \n"u8);
            w.WritePropertyName("u"u8); w.WriteNumber(ulong.MaxValue); w.WriteBoolean(true); w.WriteNull();
            w.WritePropertyName("doc"u8); root.WriteTo(ref w);
            w.WriteEndObject();
            w.Flush();
        }
        Assert.Equal(0, Measure(Body));
        byte[] span = new byte[Doc.Length * 2];
        Assert.Equal(0, Measure(() =>
        {
            var w = new JsonWriter(span, indented: true);
            root.WriteTo(ref w);
            if (w.Overflowed) throw new InvalidOperationException();
        }));
    }

    [Fact]
    public void Dom_parse_allocates_only_the_document_object()
    {
        long bytes = Measure(() => Alice.Core.Json.JsonDocument.Parse(Doc).Dispose());
        Assert.True(bytes <= 10 * 128, $"{bytes} B for 10 parses");
    }

    [Fact]
    public void Dom_lookups_allocate_nothing()
    {
        using var doc = Alice.Core.Json.JsonDocument.Parse(Doc);
        var root = doc.Root;
        Assert.Equal(0, Measure(() =>
        {
            long s = 0;
            foreach (var st in root["statuses"u8].EnumerateArray())
                if (st["user"u8].TryGetProperty("followers_count"u8, out var f)) s += f.GetInt64();
            if (s < 0) throw new InvalidOperationException();
        }));
    }
}
