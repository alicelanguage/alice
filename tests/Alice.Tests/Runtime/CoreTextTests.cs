using System.Text;
using Alice.Core.Text;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Text: SIMD byte-set kernels against a scalar reference, Utf8String, Ruby format/sprintf.</summary>
public class CoreTextTests
{
    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    static string Apply(string s, Func<byte[], byte[], int> k)
    {
        var src = U(s);
        var dst = new byte[src.Length];
        return Encoding.UTF8.GetString(dst, 0, k(src, dst));
    }

    [Theory]
    [InlineData("hello world", "lo", 5)]
    [InlineData("hello world", "a-y", 10)]
    [InlineData("hello world", "^l", 8)]
    [InlineData("héllo wörld", "^l", 8)]
    [InlineData("héllo", "a-z", 4)]
    [InlineData("a-b-c", "\\-", 2)]
    [InlineData("^^x", "\\^", 2)]
    public void Count_follows_ruby(string s, string spec, int expected)
        => Assert.Equal(expected, Utf8Text.Count(U(s), spec));

    [Theory]
    [InlineData("hello", "l", "heo")]
    [InlineData("héllo wörld", "^l", "lll")]
    [InlineData("héllo", "a-z", "é")]
    public void Delete_follows_ruby(string s, string spec, string expected)
        => Assert.Equal(expected, Apply(s, (a, d) => Utf8Text.Delete(a, Utf8ByteSet.Parse(spec), d)));

    [Theory]
    [InlineData("aaabbbccc", null, "abc")]
    [InlineData("aaabbbccc", "a", "abbbccc")]
    [InlineData("ééé  xx", null, "é x")]
    [InlineData("ééé  xx", "^x", "é xx")]
    [InlineData("mississippi", "sp", "misisipi")]
    public void Squeeze_follows_ruby(string s, string? spec, string expected)
        => Assert.Equal(expected, Apply(s, (a, d) => Utf8Text.Squeeze(a, spec is null ? null : Utf8ByteSet.Parse(spec), d)));

    [Theory]
    [InlineData("hello", "el", "ip", "hippo")]
    [InlineData("hello", "a-y", "b-z", "ifmmp")]
    [InlineData("hello", "^l", "*", "**ll*")]
    [InlineData("héllo", "^l", "*", "**ll*")]
    [InlineData("hello", "elo", "x", "hxxxx")]
    [InlineData("hello", "l", "", "heo")]
    public void Tr_follows_ruby(string s, string from, string to, string expected)
    {
        var t = new Utf8Translation(from, to);
        Assert.Equal(expected, Apply(s, (a, d) => Utf8Text.Tr(a, t, d)));
    }

    // scalar models of the Ruby semantics, character by character
    static int RefCount(string s, Func<Rune, bool> member) => s.EnumerateRunes().Count(member);

    [Fact]
    public void Kernels_match_a_scalar_model_on_long_mixed_inputs()
    {
        var rng = new Random(7);
        string alphabet = "aabbcc  xyzé€😀\n,\"";
        foreach (int len in new[] { 0, 1, 31, 63, 64, 65, 127, 200, 1000, 4097 })
        {
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++)
            {
                char c = alphabet[rng.Next(alphabet.Length)];
                if (char.IsHighSurrogate(c)) { sb.Append("😀"); continue; }
                if (char.IsLowSurrogate(c)) continue;
                sb.Append(c);
            }
            string s = sb.ToString();
            var bytes = U(s);
            Assert.Equal(RefCount(s, r => r.Value is >= 'a' and <= 'c'), Utf8Text.Count(bytes, "a-c"));
            Assert.Equal(RefCount(s, r => r.Value != ' '), Utf8Text.Count(bytes, "^ "));
            Assert.Equal(s.EnumerateRunes().Count(), Utf8Text.CharCount(bytes));
            Assert.Equal(string.Concat(s.EnumerateRunes().Where(r => r.Value is not ('a' or 'b')).Select(r => r.ToString())),
                Apply(s, (a, d) => Utf8Text.Delete(a, Utf8ByteSet.Parse("ab"), d)));
            Assert.Equal(new string(s.Select(c => c is 'a' ? 'A' : c is 'b' ? 'B' : c).ToArray()),
                Apply(s, (a, d) => Utf8Text.Tr(a, new Utf8Translation("ab", "AB"), d)));
            var squeezed = new StringBuilder();
            Rune? prev = null;
            foreach (var r in s.EnumerateRunes()) { if (prev != r) squeezed.Append(r.ToString()); prev = r; }
            Assert.Equal(squeezed.ToString(), Apply(s, (a, d) => Utf8Text.Squeeze(a, null, d)));
        }
    }

    [Fact]
    public void Utf8String_has_ruby_methods()
    {
        Utf8String s = "  Hello, World  ";
        Assert.Equal("Hello, World", s.Strip().ToString());
        Assert.Equal("  HELLO, WORLD  ", s.Upcase().ToString());
        Assert.Equal(16, s.ByteSize);
        Assert.Equal(5, s.Count("lo"));
        Assert.Equal(4, s.Index("llo"u8) - 0);
        Assert.Equal(2, ((Utf8String)"héllo").Length - 3);
        Assert.True(((Utf8String)"abc").Equals("abc"));
        Assert.Equal("ab" + "cd", ((Utf8String)"ab" + (Utf8String)"cd").ToString());
        var lines = new List<string>();
        foreach (var l in ((Utf8String)"a\nbb\r\nccc").EachLine(chomp: true)) lines.Add(Encoding.UTF8.GetString(l));
        Assert.Equal(["a", "bb", "ccc"], lines);
        var words = new List<string>();
        foreach (var w in ((Utf8String)"  x yy\tzzz ").Split()) words.Add(Encoding.UTF8.GetString(w));
        Assert.Equal(["x", "yy", "zzz"], words);
        var fields = new List<string>();
        foreach (var f in ((Utf8String)"a,,b").Split((byte)',')) fields.Add(Encoding.UTF8.GetString(f));
        Assert.Equal(["a", "", "b"], fields);
        Assert.Throws<ArgumentException>(() => Utf8String.FromUtf8([0xFF]));
    }

    [Theory]
    [InlineData("%05d", 42, "00042")]
    [InlineData("%-5d|", 42, "42   |")]
    [InlineData("%+d", 5, "+5")]
    [InlineData("% d", 5, " 5")]
    [InlineData("%x", 255, "ff")]
    [InlineData("%#x", 255, "0xff")]
    [InlineData("%X", 255, "FF")]
    [InlineData("%#o", 8, "010")]
    [InlineData("%b", 5, "101")]
    [InlineData("%#b", 5, "0b101")]
    [InlineData("%.3d", 7, "007")]
    [InlineData("%d", -42, "-42")]
    [InlineData("%05d", -42, "-0042")]
    [InlineData("%+x", -255, "-ff")]
    [InlineData("%x", -255, "ffffffffffffff01")]
    [InlineData("%c", 65, "A")]
    [InlineData("%s", 12, "12")]
    public void Format_integers(string fmt, long v, string expected) => Assert.Equal(expected, Format.Sprintf(fmt, v));

    [Theory]
    [InlineData("%f", 1.5, "1.500000")]
    [InlineData("%.2f", 3.14159, "3.14")]
    [InlineData("%08.3f", 3.14159, "0003.142")]
    [InlineData("%+.1f", 2.0, "+2.0")]
    [InlineData("%.2e", 12345.678, "1.23e+04")]
    [InlineData("%e", 0.0, "0.000000e+00")]
    [InlineData("%E", 1.5e-300, "1.500000E-300")]
    [InlineData("%g", 0.0001, "0.0001")]
    [InlineData("%g", 123456789.0, "1.23457e+08")]
    [InlineData("%g", 100000.0, "100000")]
    [InlineData("%g", 1000000.0, "1e+06")]
    [InlineData("%G", 1e-10, "1E-10")]
    [InlineData("%#.0f", 3.0, "3.")]
    [InlineData("%d", 3.99, "3")]
    [InlineData("%d", -3.99, "-3")]
    [InlineData("%s", 1.5, "1.5")]
    [InlineData("%f", double.PositiveInfinity, "Inf")]
    [InlineData("%5.1f", double.NegativeInfinity, " -Inf")]
    [InlineData("%f", double.NaN, "NaN")]
    public void Format_floats(string fmt, double v, string expected) => Assert.Equal(expected, Format.Sprintf(fmt, v));

    [Fact]
    public void Format_strings_chars_and_specs()
    {
        Assert.Equal("abc", Format.Sprintf("%.3s", "abcdef"));
        Assert.Equal("   ab|ab   |", Format.Sprintf("%5s|%-5s|", "ab", "ab"));
        Assert.Equal("h", Format.Sprintf("%c", "hello"));
        Assert.Equal("100%", Format.Sprintf("%d%%", 100));
        Assert.Equal("[]", Format.Sprintf("[%s]", FormatArg.Null));
        Assert.Equal("nil \"a\"", Format.Sprintf("%p %p", FormatArg.Null, "a"));
        Assert.Equal("   42|42   |", Format.Sprintf("%*d|%-*d|", 5, 42, 5, 42));
        Assert.Equal("true", Format.Sprintf("%s", true));
        Assert.Equal("é!", Format.Sprintf("%s!", (Utf8String)"é"));
        Assert.Equal("%q", Format.Sprintf("%q"));
        Assert.Equal("7-a-3.14-ff-00042", Format.Sprintf("%d-%s-%.2f-%x-%05d", 7, "a", 3.14159, 255, 42));
        Assert.Throws<FormatException>(() => Format.Sprintf("%d %d", 1));
        Assert.Equal("x=5", Format.Sprintf("x=%d", FormatArg.From((object)5)));
    }

    [Fact]
    public void TryFormat_reports_a_short_destination()
    {
        Span<char> small = stackalloc char[3];
        Assert.False(Format.TryFormat(small, out int n, "%05d", 42));
        Assert.Equal(0, n);
        Span<byte> utf8 = stackalloc byte[16];
        Assert.True(Format.TryFormatUtf8(utf8, out int b, "é%03d", 7));
        Assert.Equal("é007", Encoding.UTF8.GetString(utf8[..b]));
    }
}

/// <summary>Zero-allocation contract of Alice.Core.Text (runs alone: see <see cref="AllocationProbe"/>).</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class CoreTextAllocationTests
{
    [Fact]
    public void Kernels_and_span_formatting_allocate_nothing()
    {
        var src = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world, héllo wörld\n", 50)));
        var dst = new byte[src.Length];
        var set = Utf8ByteSet.Parse("a-m");
        var tr = new Utf8Translation("a-y", "b-z");
        var chars = new char[128];
        int sink = 0;
        void Body()
        {
            for (int i = 0; i < 100; i++)
            {
                sink += Utf8Text.Count(src, set);
                sink += Utf8Text.Delete(src, set, dst);
                sink += Utf8Text.Squeeze(src, null, dst);
                sink += Utf8Text.Tr(src, tr, dst);
                sink += Utf8Text.CharCount(src);
                foreach (var line in Utf8Text.EnumerateLines(src)) sink += line.Length;
                Format.TryFormat(chars, out int n, "%05d %-8s %.3f %x %e", i, "name", 3.14159, 255L, 1e10);
                sink += n;
            }
        }
        Body();
        Assert.Equal(0, AllocationProbe.Least(Body));
        Assert.NotEqual(0, sink);
    }

    [Fact]
    public void Sprintf_allocates_only_its_result()
    {
        string s = Format.Sprintf("%05d|%s|%.2f", 42, "ab", 2.5);
        long expected = AllocationProbe.Least(() => _ = new string('x', s.Length));
        Assert.Equal(expected, AllocationProbe.Least(() => _ = Format.Sprintf("%05d|%s|%.2f", 42, "ab", 2.5)));
    }
}
