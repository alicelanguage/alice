using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Alice.Core.Json;
using JsonDocument = Alice.Core.Json.JsonDocument;

namespace Alice.Tests.Runtime;

/// <summary>JsonWriter: separators, escaping, numbers, pretty printing, span overflow, differential vs Utf8JsonWriter.</summary>
public class JsonWriterTests
{
    delegate void Body(ref JsonWriter w);

    static string Write(Body body, bool indented = false)
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new JsonWriter(buf, indented);
        body(ref w);
        w.Flush();
        Assert.Equal(buf.WrittenCount, w.BytesWritten);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    [Fact]
    public void Separators_and_scalars()
    {
        Assert.Equal("{\"a\":1,\"b\":[true,false,null,\"s\"],\"c\":{},\"d\":[]}", Write((ref JsonWriter w) =>
        {
            w.WriteStartObject();
            w.WritePropertyName("a"u8); w.WriteNumber(1L);
            w.WritePropertyName("b".AsSpan());
            w.WriteStartArray(); w.WriteBoolean(true); w.WriteBoolean(false); w.WriteNull(); w.WriteString("s".AsSpan()); w.WriteEndArray();
            w.WritePropertyName("c"u8); w.WriteStartObject(); w.WriteEndObject();
            w.WritePropertyName("d"u8); w.WriteStartArray(); w.WriteEndArray();
            w.WriteEndObject();
        }));
        Assert.Equal("[-9223372036854775808,18446744073709551615,[1,2],3]", Write((ref JsonWriter w) =>
        {
            w.WriteStartArray(); w.WriteNumber(long.MinValue); w.WriteNumber(ulong.MaxValue);
            w.WriteStartArray(); w.WriteNumber(1L); w.WriteNumber(2L); w.WriteEndArray(); w.WriteNumber(3L); w.WriteEndArray();
        }));
        Assert.Equal("1", Write((ref JsonWriter w) => w.WriteRawValue("1"u8)));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e20, "1E+20")]
    [InlineData(1.5e-7, "1.5E-07")]
    [InlineData(123456789012.0, "123456789012.0")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "5E-324")]
    public void Doubles_are_shortest_round_trip_and_keep_a_fraction(double value, string text)
    {
        Assert.Equal(text, Write((ref JsonWriter w) => w.WriteNumber(value)));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(JsonKind.Float64, doc.Root.Kind);
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(doc.Root.GetDouble()));
    }

    [Fact]
    public void Non_finite_doubles_are_errors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Write((ref JsonWriter w) => w.WriteNumber(double.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Write((ref JsonWriter w) => w.WriteNumber(double.PositiveInfinity)));
    }

    [Fact]
    public void Random_doubles_round_trip()
    {
        var rng = new Random(7);
        var sb = new StringBuilder();
        var values = new double[100_000];
        var buf = new ArrayBufferWriter<byte>();
        var w = new JsonWriter(buf);
        w.WriteStartArray();
        for (int i = 0; i < values.Length; i++)
        {
            double d;
            do d = BitConverter.Int64BitsToDouble(rng.NextInt64()); while (!double.IsFinite(d));
            values[i] = d;
            w.WriteNumber(d);
        }
        w.WriteEndArray();
        w.Flush();
        using var doc = JsonDocument.Parse(buf.WrittenSpan);
        int k = 0;
        foreach (var e in doc.Root.EnumerateArray())
        {
            Assert.Equal(JsonKind.Float64, e.Kind);
            Assert.Equal(BitConverter.DoubleToInt64Bits(values[k++]), BitConverter.DoubleToInt64Bits(e.GetDouble()));
        }
    }

    [Fact]
    public void Doubles_match_ToString_on_bit_patterns_and_decimal_strings()
    {
        var rng = new Random(2026);
        Span<byte> dst = stackalloc byte[64];
        for (int i = 0; i < 2_000_000; i++)
        {
            double d = i % 2 == 0
                ? BitConverter.Int64BitsToDouble(rng.NextInt64())
                : double.Parse(rng.Next(1, 999999).ToString(CultureInfo.InvariantCulture) + "e" + rng.Next(-330, 310), CultureInfo.InvariantCulture);
            if (i < 64) d = i switch { 0 => 1e15, 1 => 1e14, 2 => 1e-5, 3 => 1e-4, 4 => 123456789012345.0, 5 => 1234567890123456.0, 6 => 0.1, 7 => 2.0, 8 => double.Epsilon,
                9 => double.MaxValue, 10 => -double.MinValue, 11 => 2.2250738585072014e-308, 12 => 5e-324 * 3, 13 => 9007199254740992.0, 14 => 0.000123, _ => Math.Pow(10, i - 40) };
            if (!double.IsFinite(d)) continue;
            string r = d.ToString(CultureInfo.InvariantCulture);
            if (r.IndexOfAny(['.', 'E']) < 0) r += ".0";
            var w = new JsonWriter(dst);
            w.WriteNumber(d);
            string got = Encoding.ASCII.GetString(dst.Slice(0, (int)w.BytesWritten));
            if (got != r) Assert.Fail($"{BitConverter.DoubleToInt64Bits(d):X}: expected {r}, got {got}");
        }
    }

    static void WriteLocalSpans(ref JsonWriter w)
    {
        // ADR 0036 amendment 1: spans over locals pass to a writer reached by ref (CS8350 without scoped)
        Span<byte> name = stackalloc byte[1];
        name[0] = (byte)'k';
        Span<char> text = stackalloc char[2];
        text[0] = 'v'; text[1] = '!';
        w.WriteStartObject();
        w.WritePropertyName(name);
        w.WriteString(text);
        w.WritePropertyName("r"u8);
        w.WriteRawValue(stackalloc byte[] { (byte)'1' });
        w.WriteEndObject();
    }

    [Fact]
    public void Scoped_spans_over_locals_are_accepted()
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        var w = new JsonWriter(buf);
        WriteLocalSpans(ref w);
        w.Flush();
        Assert.Equal("""{"k":"v!","r":1}""", Encoding.UTF8.GetString(buf.WrittenSpan));
    }

    [Fact]
    public void Strings_escape_only_quote_backslash_and_controls()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\\u001f é 日本 😀 </>&'\"", Write((ref JsonWriter w) => w.WriteString("a\"b\\c\n\r\t\b\f\u0001\u001f é 日本 😀 </>&'".AsSpan())));
        Assert.Equal("\"a\\\"b\"", Write((ref JsonWriter w) => w.WriteString("a\"b"u8)));
        Assert.Equal("{\"k\\\"1\":\"v\"}", Write((ref JsonWriter w) => { w.WriteStartObject(); w.WritePropertyName("k\"1".AsSpan()); w.WriteString("v"u8); w.WriteEndObject(); }));
        string longText = new string('x', 10000) + "\"" + new string('y', 10000);
        string got = Write((ref JsonWriter w) => w.WriteString(longText.AsSpan()));
        using var stj = System.Text.Json.JsonDocument.Parse(got);
        Assert.Equal(longText, stj.RootElement.GetString());
    }

    [Fact]
    public void Indented_output_matches_Utf8JsonWriter()
    {
        string ours = Write((ref JsonWriter w) =>
        {
            w.WriteStartObject();
            w.WritePropertyName("a"u8); w.WriteStartArray(); w.WriteNumber(1L); w.WriteStartObject(); w.WriteEndObject(); w.WriteNumber(2L); w.WriteEndArray();
            w.WritePropertyName("b"u8); w.WriteStartObject(); w.WritePropertyName("c"u8); w.WriteNull(); w.WriteEndObject();
            w.WritePropertyName("d"u8); w.WriteStartArray(); w.WriteEndArray();
            w.WriteEndObject();
        }, indented: true);
        var buf = new ArrayBufferWriter<byte>();
        using (var s = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            s.WriteStartObject();
            s.WritePropertyName("a"); s.WriteStartArray(); s.WriteNumberValue(1); s.WriteStartObject(); s.WriteEndObject(); s.WriteNumberValue(2); s.WriteEndArray();
            s.WritePropertyName("b"); s.WriteStartObject(); s.WriteNull("c"); s.WriteEndObject();
            s.WritePropertyName("d"); s.WriteStartArray(); s.WriteEndArray();
            s.WriteEndObject();
        }
        Assert.Equal(Encoding.UTF8.GetString(buf.WrittenSpan), ours);
    }

    [Fact]
    public void Span_mode_reports_overflow()
    {
        Span<byte> small = stackalloc byte[8];
        var w = new JsonWriter(small);
        w.WriteStartArray();
        w.WriteNumber(12345L);
        Assert.False(w.Overflowed);
        w.WriteString("toolong".AsSpan());
        Assert.True(w.Overflowed);
        w.WriteNumber(1L);
        w.WriteEndArray();
        Assert.True(w.Overflowed);
        Assert.Equal("[12345", Encoding.UTF8.GetString(small.Slice(0, (int)w.BytesWritten)));

        Span<byte> fits = stackalloc byte[64];
        var v = new JsonWriter(fits);
        v.WriteStartObject(); v.WritePropertyName("n"u8); v.WriteNumber(2.5); v.WriteEndObject();
        Assert.False(v.Overflowed);
        Assert.Equal("{\"n\":2.5}", Encoding.UTF8.GetString(fits.Slice(0, (int)v.BytesWritten)));
    }

    [Fact]
    public void Random_token_streams_equal_Utf8JsonWriter_semantically()
    {
        var rng = new Random(11);
        for (int round = 0; round < 300; round++)
        {
            var ours = new ArrayBufferWriter<byte>();
            var theirs = new ArrayBufferWriter<byte>();
            var w = new JsonWriter(ours, indented: round % 2 == 0);
            using (var s = new Utf8JsonWriter(theirs))
            {
                Emit(rng, ref w, s, 0);
            }
            w.Flush();
            using var a = System.Text.Json.JsonDocument.Parse(ours.WrittenMemory);
            using var b = System.Text.Json.JsonDocument.Parse(theirs.WrittenMemory);
            Assert.True(JsonElement.DeepEquals(a.RootElement, b.RootElement), Encoding.UTF8.GetString(ours.WrittenSpan));
        }
    }

    static void Emit(Random rng, ref JsonWriter w, Utf8JsonWriter s, int depth)
    {
        int pick = depth > 4 ? rng.Next(2, 8) : rng.Next(8);
        switch (pick)
        {
            case 0:
                w.WriteStartObject(); s.WriteStartObject();
                for (int i = rng.Next(5); i > 0; i--)
                {
                    string name = RandomString(rng);
                    w.WritePropertyName(name.AsSpan()); s.WritePropertyName(name);
                    Emit(rng, ref w, s, depth + 1);
                }
                w.WriteEndObject(); s.WriteEndObject();
                break;
            case 1:
                w.WriteStartArray(); s.WriteStartArray();
                for (int i = rng.Next(5); i > 0; i--) Emit(rng, ref w, s, depth + 1);
                w.WriteEndArray(); s.WriteEndArray();
                break;
            case 2: { long v = rng.NextInt64(long.MinValue, long.MaxValue); w.WriteNumber(v); s.WriteNumberValue(v); break; }
            case 3: { double v = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-30, 30)); w.WriteNumber(v); s.WriteNumberValue(v); break; }
            case 4: { string v = RandomString(rng); w.WriteString(v.AsSpan()); s.WriteStringValue(v); break; }
            case 5: { byte[] v = Encoding.UTF8.GetBytes(RandomString(rng)); w.WriteString(v); s.WriteStringValue(v); break; }
            case 6: { bool v = rng.Next(2) == 0; w.WriteBoolean(v); s.WriteBooleanValue(v); break; }
            default: w.WriteNull(); s.WriteNullValue(); break;
        }
    }

    static string RandomString(Random rng)
    {
        const string chars = "abc \"\\/\n\t\u0001\u001féü日😀<>&'";
        var sb = new StringBuilder();
        for (int i = rng.Next(12); i > 0; i--)
        {
            int k = rng.Next(chars.Length);
            if (char.IsHighSurrogate(chars[k])) sb.Append(chars, k, 2);
            else if (!char.IsLowSurrogate(chars[k])) sb.Append(chars[k]);
        }
        return sb.ToString();
    }

    [Fact]
    public void Document_WriteTo_preserves_number_text_and_escapes()
    {
        const string text = "{\"a\":1.50,\"b\":\"\\u00e9\\n\",\"c\":[1e400,-0,12345678901234567890123]}";
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(text, doc.Root.ToJson());
        _ = doc.Root["b"].GetString(); // decoded into the arena: written escaped again
        Assert.Equal("{\"a\":1.50,\"b\":\"é\\n\",\"c\":[1e400,-0,12345678901234567890123]}", doc.Root.ToJson());
        var buf = new ArrayBufferWriter<byte>();
        var w = new JsonWriter(buf, indented: true);
        doc.Root.WriteTo(ref w);
        w.Flush();
        Assert.Contains("\n  \"a\": 1.50", Encoding.UTF8.GetString(buf.WrittenSpan));
        Assert.Equal(1.5, double.Parse("1.50", CultureInfo.InvariantCulture));
    }
}
