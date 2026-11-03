using System.Text;
using Alice.Core.Json;
using JsonDocument = Alice.Core.Json.JsonDocument;
using static Alice.Tests.Runtime.JsonTestUtil;

namespace Alice.Tests.Runtime;

/// <summary>Stage 1 (structural index): every SIMD tier equals a byte-at-a-time reference, on fuzz and the corpus.</summary>
public class JsonStage1Tests
{
    /// <summary>The reference: structural offsets or the error, one byte at a time.</summary>
    static string Reference(byte[] s)
    {
        var o = new List<uint>();
        bool inString = false, esc = false, prevScalar = false;
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            bool escaped = esc;
            esc = b == '\\' && !escaped;
            bool quote = b == '"' && !escaped;
            if (inString)
            {
                if (b < 0x20) return "error";
                if (escaped)
                {
                    bool valid = b is (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t'
                                 || (b == 'u' && i + 4 < s.Length + 0 && Enumerable.Range(1, 4).All(k => Uri.IsHexDigit((char)s[i + k])));
                    if (!valid) return "error";
                }
                if (quote) { inString = false; prevScalar = false; }
                continue;
            }
            bool op = b < 0x80 && "\0\0\0\0\0\0\0\0\0\0:{,}\0\0"[b & 15] == (b | 0x20);
            bool space = b < 0x80 && " ddd\u0011dq\u0002d\t\npd\rdd"[b & 15] == b;
            if (quote) { o.Add((uint)i); inString = true; prevScalar = false; }
            else if (op) { o.Add((uint)i); prevScalar = false; }
            else if (space || b == '"') prevScalar = false;
            else
            {
                if (!prevScalar) o.Add((uint)i);
                prevScalar = true;
            }
        }
        if (inString) return "error";
        return string.Join(",", o);
    }

    static string Ours(byte[] s)
    {
        var pos = new uint[s.Length + 24];
        try
        {
            int n = Build(s, pos);
            Assert.Equal((uint)s.Length, pos[n]);
            return string.Join(",", pos.Take(n));
        }
        catch (JsonParseException)
        {
            return "error";
        }
    }

    static readonly byte[] Alphabet = Encoding.Latin1.GetBytes("\"\"\"\\\\\\{}[]:,     \t\n\r01-.eE+tfnulabcxyz/u0Af\u000c\u001a\u0001\u00c3\u00a9\u00ff");

    [Fact]
    public void Tiers_equal_the_reference_on_fuzz()
    {
        var rng = new Random(20261102);
        for (int round = 0; round < 4000; round++)
        {
            int len = rng.Next(0, round < 2000 ? 80 : 400);
            var s = new byte[len];
            for (int i = 0; i < len; i++) s[i] = Alphabet[rng.Next(Alphabet.Length)];
            string expected = Reference(s);
            foreach (var tier in Tiers.Where(TierSupported))
            {
                string got = WithTier(tier, () => Ours(s));
                if (got != expected)
                    Assert.Fail($"[{tier}] round {round}: {Convert.ToHexString(s)}\nexpected {expected}\ngot      {got}");
            }
        }
    }

    [Fact]
    public void Backslash_runs_across_block_boundaries()
    {
        for (int run = 0; run <= 130; run++)
            for (int at = 0; at < 70; at++)
            {
                // "<at spaces>"<run backslashes>"..."  : even runs close the string at the next quote
                var sb = new StringBuilder();
                sb.Append(' ', at).Append('"').Append('\\', run).Append("\"x\"");
                var s = Encoding.ASCII.GetBytes(sb.ToString());
                string expected = Reference(s);
                foreach (var tier in Tiers.Where(TierSupported))
                    Assert.Equal(expected, WithTier(tier, () => Ours(s)));
            }
    }

    [Fact]
    public void Corpus_and_suite_index_is_tier_independent()
    {
        var docs = Accepted().Select(a => a.Bytes).ToList();
        foreach (var name in CorpusNames) if (Corpus(name) is { } c) docs.Add(c);
        foreach (var d in docs)
        {
            string expected = WithTier("Scalar", () => Ours(d));
            Assert.Equal(Reference(d), expected);
            foreach (var tier in Tiers.Where(TierSupported)) Assert.Equal(expected, WithTier(tier, () => Ours(d)));
        }
    }

    [Fact]
    public void Prefix_xor_and_simple_documents()
    {
        Assert.Equal("0,1,5,6,7,8,9", Ours("{\"ab\":1,2}"u8.ToArray()));
        Assert.Equal("0,2,3,4,9", Ours("[ 1,\"a,b\"]"u8.ToArray()));
        Assert.Equal("", Ours("   "u8.ToArray()));
        Assert.Equal("error", Ours("[\"abc]"u8.ToArray()));
        Assert.Equal("error", Ours("[\"a\tb\"]"u8.ToArray()));
        Assert.Equal("error", Ours("[\"a\\xb\"]"u8.ToArray()));
        Assert.Equal("error", Ours("[\"\\u12G4\"]"u8.ToArray()));
    }

    [Fact]
    public void Invalid_utf8_is_rejected_with_its_offset()
    {
        var e = Assert.Throws<JsonParseException>(() => JsonDocument.Parse(new byte[] { (byte)'[', (byte)'"', 0xC3, (byte)'"', (byte)']' }));
        Assert.Equal(2, e.Offset);
    }
}
