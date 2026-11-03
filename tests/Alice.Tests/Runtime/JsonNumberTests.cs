using System.Globalization;
using System.Text;
using Alice.Core.Json;
using JsonDocument = Alice.Core.Json.JsonDocument;

namespace Alice.Tests.Runtime;

/// <summary>Numbers: kinds (Int64 -> UInt64 -> Float64, as Ruby), and every double equals double.Parse bit for bit.</summary>
public class JsonNumberTests
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static JsonAny One(string text) => JsonDocument.Parse(text).Root;

    [Theory]
    [InlineData("0", JsonKind.Int64)]
    [InlineData("-0", JsonKind.Int64)]
    [InlineData("9223372036854775807", JsonKind.Int64)]
    [InlineData("-9223372036854775808", JsonKind.Int64)]
    [InlineData("9223372036854775808", JsonKind.UInt64)]
    [InlineData("18446744073709551615", JsonKind.UInt64)]
    [InlineData("18446744073709551616", JsonKind.Float64)]
    [InlineData("-9223372036854775809", JsonKind.Float64)]
    [InlineData("123456789012345678901234567890", JsonKind.Float64)]
    [InlineData("1.0", JsonKind.Float64)]
    [InlineData("1e2", JsonKind.Float64)]
    [InlineData("1E+2", JsonKind.Float64)]
    [InlineData("-0.0", JsonKind.Float64)]
    public void Kinds_follow_Ruby_overflow_order(string text, JsonKind kind) => Assert.Equal(kind, One(text).Kind);

    [Fact]
    public void Integer_values_are_exact()
    {
        Assert.Equal(long.MaxValue, One("9223372036854775807").GetInt64());
        Assert.Equal(long.MinValue, One("-9223372036854775808").GetInt64());
        Assert.Equal(9223372036854775808UL, One("9223372036854775808").GetUInt64());
        Assert.Equal(ulong.MaxValue, One("18446744073709551615").GetUInt64());
        Assert.Equal(18446744073709551616.0, One("18446744073709551616").GetDouble());
        Assert.Equal(-9223372036854775809.0, One("-9223372036854775809").GetDouble());
        Assert.Equal(100, One("1e2").GetInt64());               // an exact float converts
        Assert.Throws<InvalidOperationException>(() => One("1.5").GetInt64());
        Assert.Throws<InvalidOperationException>(() => One("18446744073709551615").GetInt64());
        Assert.True(double.IsNegative(One("-0.0").GetDouble()));
    }

    [Theory]
    [InlineData("2.2250738585072011e-308")]
    [InlineData("2.2250738585072014e-308")]
    [InlineData("4.9e-324")]
    [InlineData("2.4703282292062327e-324")]
    [InlineData("2.4703282292062328e-324")]
    [InlineData("1.7976931348623157e308")]
    [InlineData("1.7976931348623158e308")]
    [InlineData("1e309")]
    [InlineData("-1e309")]
    [InlineData("1e-400")]
    [InlineData("0.1000000000000000055511151231257827021181583404541015625")]
    [InlineData("9007199254740993")]
    [InlineData("9007199254740993.0")]
    [InlineData("1.00000000000000011102230246251565404236316680908203125")]
    [InlineData("7.2057594037927933e16")]
    [InlineData("0.000000000000000000000000000000000000000000001e300")]
    [InlineData("123456789e-30")]
    [InlineData("1e23")]
    [InlineData("8.98846567431158e307")]
    [InlineData("0.3")]
    [InlineData("1448997445238699")]
    [InlineData("1e0000000000000000000000000000000000000000000000000000000000000002")]
    public void Hard_doubles_equal_double_Parse(string text)
    {
        double expected = double.Parse(text, NumberStyles.Float, Inv);
        double got = One("[" + text + "]")[0].GetDouble();
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(got));
    }

    [Theory]
    [InlineData("-")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("+1")]
    [InlineData("0x10")]
    [InlineData("1.e5")]
    [InlineData("-01")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1_000")]
    [InlineData("2.")]
    public void Invalid_numbers_are_rejected(string text) => Assert.Throws<JsonParseException>(() => JsonDocument.Parse("[" + text + "]"));

    /// <summary>Count of random doubles (env ALICE_JSON_DOUBLES; the default keeps the suite fast; the release check
    /// runs 100000000).</summary>
    static long RandomCount => long.TryParse(Environment.GetEnvironmentVariable("ALICE_JSON_DOUBLES"), out long n) ? n : 1_000_000;

    [Fact]
    public void Random_doubles_equal_double_Parse()
    {
        long total = RandomCount;
        const int perDoc = 4096;
        long chunks = (total + perDoc - 1) / perDoc;
        long checkedCount = 0;
        string? failure = null;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, c =>
        {
            if (failure != null) return;
            var rng = new Random(unchecked((int)(c * 7919 + 17)));
            var texts = new string[perDoc];
            var sb = new StringBuilder(perDoc * 26);
            sb.Append('[');
            for (int i = 0; i < perDoc; i++)
            {
                texts[i] = RandomNumberText(rng, i);
                if (i > 0) sb.Append(',');
                sb.Append(texts[i]);
            }
            sb.Append(']');
            using var doc = JsonDocument.Parse(sb.ToString());
            int k = 0;
            foreach (var e in doc.Root.EnumerateArray())
            {
                double expected = double.Parse(texts[k], NumberStyles.Float, Inv);
                double got = e.GetDouble();
                if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(got))
                {
                    failure = $"{texts[k]}: expected {expected:R}, got {got:R}";
                    return;
                }
                k++;
            }
            Interlocked.Add(ref checkedCount, k);
        });
        Assert.Null(failure);
        Assert.True(checkedCount >= total, $"{checkedCount} < {total}");
    }

    /// <summary>Shortest round-trip text of random bit patterns, 17-digit text, and random decimal strings.</summary>
    static string RandomNumberText(Random rng, int i)
    {
        switch (i % 4)
        {
            case 0:
            case 1:
                {
                    double d;
                    do d = BitConverter.Int64BitsToDouble(rng.NextInt64()); while (!double.IsFinite(d));
                    string s = i % 4 == 0 ? d.ToString("R", Inv) : d.ToString("E16", Inv);
                    return s.Contains('.') || s.Contains('E') ? s : s + ".0";
                }
            case 2:
                {
                    // random digits (1-19 significant) and exponent: the Eisel-Lemire path
                    int digits = rng.Next(1, 20);
                    var sb = new StringBuilder();
                    if (rng.Next(2) == 0) sb.Append('-');
                    sb.Append((char)('1' + rng.Next(9)));
                    for (int k = 1; k < digits; k++) sb.Append((char)('0' + rng.Next(10)));
                    if (rng.Next(2) == 0 && digits > 1) sb.Insert(sb.Length - rng.Next(1, digits), '.');
                    sb.Append('e').Append(rng.Next(-345, 312));
                    return sb.ToString();
                }
            default:
                {
                    // long mantissas (fallback path) and plain decimals
                    int digits = rng.Next(1, 40);
                    var sb = new StringBuilder();
                    sb.Append((char)('0' + rng.Next(1, 10)));
                    for (int k = 1; k < digits; k++) sb.Append((char)('0' + rng.Next(10)));
                    sb.Append('.').Append(rng.Next(0, 1000000));
                    if (rng.Next(3) == 0) sb.Append("e-").Append(rng.Next(0, 330));
                    return sb.ToString();
                }
        }
    }

    [Fact]
    public void Corpus_numbers_equal_double_Parse()
    {
        foreach (var name in new[] { "canada", "mesh", "twitter", "citm_catalog" })
        {
            var bytes = JsonTestUtil.Corpus(name);
            if (bytes is null) continue;
            using var doc = JsonDocument.Parse(bytes);
            using var stj = System.Text.Json.JsonDocument.Parse(bytes);
            JsonTestUtil.AssertSame(doc.Root, stj.RootElement, name);
        }
    }
}
