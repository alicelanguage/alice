using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Auto-vectorization of numeric array pipelines: same results as scalar code, emitted as Vector&lt;T&gt; loops.</summary>
public class VectorizeTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "vec", exe: false)).GetType("Program")!.GetMethod("f")!;

    static int[] Ints(int n) => Enumerable.Range(0, n).Select(i => (i * 7919) % 101 - 50).ToArray();
    static double[] Doubles(int n) => Enumerable.Range(0, n).Select(i => ((i * 7919) % 101 - 50) * 0.25).ToArray();

    public static TheoryData<string, int> Lengths()
    {
        var d = new TheoryData<string, int>();
        foreach (var n in new[] { 0, 1, 3, 7, 8, 9, 31, 33, 1000, 1027 }) d.Add("", n);
        return d;
    }

    [Theory, MemberData(nameof(Lengths))]
    public void Int_map_select_sum(string _, int n)
    {
        var m = Compile("def f(xs : Array(Int32))\n  xs.map { |x| x * 3 + 1 }.select { |x| x > 10 && x < 100 }.sum\nend");
        var xs = Ints(n);
        Assert.Equal(xs.Select(x => x * 3 + 1).Where(x => x > 10 && x < 100).Sum(), m.Invoke(null, [xs]));
    }

    [Theory, MemberData(nameof(Lengths))]
    public void Double_sum_count_max_min(string _, int n)
    {
        var xs = Doubles(n);
        var sum = Compile("def f(xs : Array(Float64))\n  xs.sum { |x| x * x }\nend");
        Assert.Equal(xs.Sum(x => x * x), (double)sum.Invoke(null, [xs])!, 6);
        var cnt = Compile("def f(xs : Array(Float64))\n  xs.count { |x| x > 1.5 }\nend");
        Assert.Equal(xs.Count(x => x > 1.5), cnt.Invoke(null, [xs]));
        if (n == 0) return;
        var max = Compile("def f(xs : Array(Float64))\n  xs.map { |x| x * 2.0 - 1.0 }.max\nend");
        Assert.Equal(xs.Max(x => x * 2.0 - 1.0), max.Invoke(null, [xs]));
        var min = Compile("def f(xs : Array(Int32))\n  xs.map { |x| -x }.min\nend");
        Assert.Equal(Ints(n).Min(x => -x), min.Invoke(null, [Ints(n)]));
    }

    [Theory, MemberData(nameof(Lengths))]
    public void Map_with_captured_scalar(string _, int n)
    {
        var m = Compile("def f(xs : Array(Float64))\n  k = 2.5\n  xs.map { |x| x * k + 1.0 }\nend");
        var xs = Doubles(n);
        Assert.Equal(xs.Select(x => x * 2.5 + 1.0).ToArray(), (double[])m.Invoke(null, [xs])!);
    }

    [Fact]
    public void Numeric_pipelines_emit_vector_code()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Float64))\n  xs.map { |x| x * 1.5 }.select { |x| x > 10.0 }.sum\nend\n");
        Assert.Contains("global::System.Numerics.Vector<double>", cs);
        Assert.Contains("ConditionalSelect", cs);
    }

    [Fact]
    public void Non_vectorizable_blocks_stay_scalar()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Float64))\n  xs.sum { |x| Math.Sin(x) }\nend\n");
        Assert.DoesNotContain("Vector<", cs);
    }
}
