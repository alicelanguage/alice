using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Idiom rewrites that make common Alice code faster than the equivalent idiomatic C#.</summary>
public class PatternOptTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "pat", exe: false)).GetType("Program")!.GetMethod("f")!;

    const string WordCount = "def f(text : String) : Int32\n  counts = Dictionary(String, Int32).new\n  for w in text.Split(' ')\n    counts[w] = counts.GetValueOrDefault(w) + 1\n  end\n  counts[\"a\"] * 100 + counts.Count\nend";

    [Fact]
    public void Word_count_is_correct()
    {
        Assert.Equal(3 * 100 + 3, Compile(WordCount).Invoke(null, ["a b a c a b"]));
        Assert.Equal(1 * 100 + 2, Compile(WordCount).Invoke(null, ["a "]));   // empty entries kept like string.Split
    }

    [Fact]
    public void Word_count_uses_spans_and_one_probe()
    {
        var cs = AliceCompiler.Transpile(WordCount + "\n");
        Assert.Contains("AsSpan().Split(", cs);
        Assert.Contains("GetAlternateLookup<global::System.ReadOnlySpan<char>>", cs);
        Assert.Contains("GetValueRefOrAddDefault", cs);
        Assert.DoesNotContain("GetValueOrDefault(", cs);
    }

    [Fact]
    public void Counting_known_words_allocates_only_the_dictionary()
    {
        var m = Compile(WordCount);
        var text = string.Join(' ', Enumerable.Repeat("a b c d", 2000));
        var del = m.CreateDelegate<Func<string, int>>();
        for (int i = 0; i < 50; i++) del(text);
        long before = GC.GetAllocatedBytesForCurrentThread();
        del(text);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 2000, $"allocated {bytes} bytes for 8000 words");   // C# Split allocates ~8000 strings + an array
    }

    [Theory]
    [InlineData("def f(text : String) : Int32\n  d = Dictionary(String, Int32).new\n  d[\"x\"] = 5\n  d[\"x\"] += 2\n  d[\"x\"]\nend", 7)]
    [InlineData("def f(text : String) : Int32\n  d = Dictionary(Char, Int32).new\n  for ch in text\n    d[ch] = d.GetValueOrDefault(ch) + 1\n  end\n  d['l']\nend", 3)]
    public void Dictionary_increment_single_probe(string src, int expected)
    {
        Assert.Equal(expected, Compile(src).Invoke(null, ["hello world"]));
        Assert.Contains("CollectionsMarshal", AliceCompiler.Transpile(src + "\n"));
    }

    [Fact]
    public void Missing_key_compound_assignment_still_throws()
    {
        var m = Compile("def f(text : String) : Int32\n  d = Dictionary(String, Int32).new\n  d[text] += 1\n  d[text]\nend");
        var ex = Assert.Throws<TargetInvocationException>(() => m.Invoke(null, ["k"]));
        Assert.IsType<KeyNotFoundException>(ex.InnerException);
    }
}

public class ParallelWordCountTests
{
    [Fact]
    public void Large_text_parallel_counts_match_serial_including_order()
    {
        var src = "def f(text : String) : String\n  counts = Dictionary(String, Int32).new\n  for w in text.Split(' ')\n    counts[w] = counts.GetValueOrDefault(w) + 1\n  end\n  out = StringBuilder.new\n  for k, v in counts\n    out.Append(k).Append('=').Append(v).Append(';')\n  end\n  out.ToString()\nend";
        Assert.Contains("__parwords", AliceCompiler.Transpile(src + "\n"));
        var m = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "pw", exe: false)).GetType("Program")!.GetMethod("f")!;
        var rnd = new Random(7);
        var words = Enumerable.Range(0, 120_000).Select(_ => "w" + rnd.Next(5000)).ToList();
        words.Insert(500, "");   // empty entries (double spaces) must be kept like string.Split
        var text = string.Join(' ', words) + " ";
        var expected = new Dictionary<string, int>();
        foreach (var w in text.Split(' ')) expected[w] = expected.GetValueOrDefault(w) + 1;
        Assert.Equal(string.Concat(expected.Select(kv => $"{kv.Key}={kv.Value};")), m.Invoke(null, [text]));
    }
}

public class BoundsCheckTests
{
    [Fact]
    public void Size_lowers_to_length_and_loop_bounds_use_it()
    {
        var src = "def f(xs : Array(Float64)) : Float64\n  n = xs.size\n  s = 0.0\n  for i in 0...n\n    for j in (i + 1)...n\n      s += xs[i] * xs[j]\n    end\n  end\n  s + \"abc\".size + [1, 2].to_list.size\nend\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.Contains("var n = xs.Length", cs);
        Assert.Contains("< xs.Length;", cs);             // bound rewritten: the JIT can drop xs[j] range checks
        Assert.DoesNotContain(".size()", cs);
        var m = AliceCompiler.Load(AliceCompiler.Emit([cs], "bc", exe: false)).GetType("Program")!.GetMethod("f")!;
        Assert.Equal(1.0 * 2 + 1 * 3 + 2 * 3 + 3.0 + 2, m.Invoke(null, [new[] { 1.0, 2.0, 3.0 }]));
    }

    [Fact]
    public void Reassigned_bound_is_not_rewritten()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Int32)) : Int32\n  n = xs.size\n  n -= 1\n  s = 0\n  for i in 0...n\n    s += xs[i]\n  end\n  s\nend\n");
        Assert.DoesNotContain("< xs.Length;", cs);
    }
}
