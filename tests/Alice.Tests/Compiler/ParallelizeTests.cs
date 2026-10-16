using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>Automatic parallelization of pure pipelines over large arrays and SoA stores.</summary>
public class ParallelizeTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "par", exe: false)).GetType("Program")!.GetMethod("f")!;

    public static TheoryData<int> Sizes => new() { 10, 70_000, 1_000_003 };

    [Theory, MemberData(nameof(Sizes))]
    public void Pure_sum_is_parallel_and_correct(int n)
    {
        var m = Compile("def f(xs : Array(Int64))\n  xs.map { |x| x * 3 }.select { |x| x % 2 == 0 }.sum { |x| x + 1 }\nend");
        var xs = Enumerable.Range(0, n).Select(i => (long)i).ToArray();
        Assert.Equal(xs.Select(x => x * 3).Where(x => x % 2 == 0).Sum(x => x + 1), m.Invoke(null, [xs]));
    }

    [Theory, MemberData(nameof(Sizes))]
    public void Math_heavy_sum_count_max(int n)
    {
        var xs = Enumerable.Range(0, n).Select(i => i * 0.001).ToArray();
        var sum = Compile("def f(xs : Array(Float64))\n  xs.sum { |x| Math.Sqrt(x) * Math.Sin(x) }\nend");
        Assert.Equal(xs.Sum(x => Math.Sqrt(x) * Math.Sin(x)), (double)sum.Invoke(null, [xs])!, 1e-6 * n);
        var cnt = Compile("def f(xs : Array(Float64))\n  xs.count { |x| Math.Sin(x) > 0.5 }\nend");
        Assert.Equal(xs.Count(x => Math.Sin(x) > 0.5), cnt.Invoke(null, [xs]));
        var max = Compile("def f(xs : Array(Float64))\n  xs.map { |x| Math.Cos(x) * x }.max\nend");
        Assert.Equal(xs.Max(x => Math.Cos(x) * x), max.Invoke(null, [xs]));
    }

    [Fact]
    public void Large_pure_pipelines_emit_a_parallel_path()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Float64))\n  xs.sum { |x| Math.Sqrt(x) }\nend\n");
        Assert.Contains("Par.Reduce", cs);
    }

    [Fact]
    public void Impure_blocks_stay_serial()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Float64))\n  t = 0.0\n  xs.each { |x| t += x }\n  t\nend\n");
        Assert.DoesNotContain("Par.", cs);
        var cs2 = AliceCompiler.Transpile("def f(xs : Array(Float64))\n  xs.sum { |x| puts x\n  x }\nend\n");
        Assert.DoesNotContain("Par.", cs2);
    }

    [Fact]
    public void Soa_each_update_runs_in_parallel_and_is_correct()
    {
        var src = "@[SoA]\nrecord struct P(x : Float64, v : Float64)\ndef f(n : Int32)\n  ps = SoA(P).new\n  n.times { |i| ps.add(P.new(i.to_f, 2.0)) }\n  ps.each { |p| p.x += p.v * Math.Sqrt(p.v) }\n  ps.sum { |p| p.x }\nend";
        var m = Compile(src);
        int n = 200_000;
        double expected = Enumerable.Range(0, n).Sum(i => i + 2.0 * Math.Sqrt(2.0));
        double got = (double)m.Invoke(null, [n])!;
        Assert.True(Math.Abs(got - expected) <= Math.Abs(expected) * 1e-9, $"{got} vs {expected}");   // parallel sums reassociate
        Assert.Contains("Par.Range", AliceCompiler.Transpile(src + "\n"));
    }
}
