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
