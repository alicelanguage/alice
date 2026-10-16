using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>GC-free lowering: iterator chains with literal blocks must compile to plain loops that allocate nothing.</summary>
[Collection("console")]
public class AllocationTests
{
    static MethodInfo Compile(string src, string method)
    {
        var asm = AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "alloc", exe: false));
        return asm.GetType("Program")!.GetMethod(method)!;
    }

    /// <summary>Calls through a typed delegate so the measurement itself does not box arguments or results.</summary>
    static long AllocatedPerCall(MethodInfo m, object?[] args)
    {
        var ta = m.GetParameters()[0].ParameterType;
        var del = m.CreateDelegate(typeof(Func<,>).MakeGenericType(ta, m.ReturnType));
        var measure = typeof(AllocationTests).GetMethod(nameof(Measure), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(ta, m.ReturnType);
        return (long)measure.Invoke(null, [del, args[0]])!;
    }

    static long Measure<TA, TR>(Func<TA, TR> f, TA a)
    {
        for (int i = 0; i < 50; i++) f(a);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) f(a);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
    }

    public static TheoryData<string, string, object, object> Kernels => new()
    {
        { "fused map/select/sum", "def f(xs : Array(Int32))\n  xs.map { |x| x * 2 }.select { |x| x > 2 }.sum\nend", new[] { 1, 2, 3 }, 10 },
        { "sum with block", "def f(xs : Array(Float64))\n  xs.sum { |x| x * x }\nend", new[] { 1.0, 2.0 }, 5.0 },
        { "count with block", "def f(xs : Array(Int32))\n  xs.count { |x| x.even? }\nend", new[] { 1, 2, 4 }, 2 },
        { "each accumulates", "def f(xs : Array(Int32))\n  s = 0\n  xs.each { |x| s += x }\n  s\nend", new[] { 1, 2, 3 }, 6 },
        { "times", "def f(n : Int32)\n  s = 0\n  n.times { |i| s += i }\n  s\nend", 5, 10 },
        { "range reduce", "def f(n : Int32)\n  (1..n).reduce(0) { |a, b| a + b }\nend", 10, 55 },
        { "any? / find", "def f(xs : Array(Int32))\n  xs.any? { |x| x > 2 } ? xs.find { |x| x > 1 } : 0\nend", new[] { 1, 2, 3 }, 2 },
        { "list each_with_index", "def f(xs : List(Int32))\n  s = 0\n  xs.each_with_index { |x, i| s += x * i }\n  s\nend", new List<int> { 5, 6, 7 }, 20 },
    };

    [Theory, MemberData(nameof(Kernels))]
    public void Iterator_kernels_allocate_nothing(string name, string src, object arg, object expected)
    {
        _ = name;
        var m = Compile(src, "f");
        Assert.Equal(expected, m.Invoke(null, [arg]));
        Assert.Equal(0, AllocatedPerCall(m, [arg]));
    }

    [Fact]
    public void Inlined_chains_emit_no_lambdas()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Int32))\n  xs.map { |x| x * 2 }.select { |x| x > 2 }.sum\nend\n");
        int start = cs.IndexOf("int f(int[] xs)");
        string body = cs.Substring(start, cs.IndexOf("\n}\n", start) - start);
        Assert.DoesNotContain("=>", body);   // the serial path is a plain loop; only the parallel helper holds a lambda
    }

    [Fact]
    public void Capture_free_lambdas_are_static()
    {
        var cs = AliceCompiler.Transpile("sq = ->(x : Int32) { x * x }\nputs sq.call(3)\n");
        Assert.Contains("static (int x_1) => x_1 * x_1", cs);
    }

    [Fact]
    public void Map_over_array_preallocates_exact_size()
    {
        var m = Compile("def f(xs : Array(Int32))\n  xs.map { |x| x + 1 }\nend", "f");
        Assert.Equal(new[] { 2, 3 }, m.Invoke(null, [new[] { 1, 2 }]));
        Assert.Equal(typeof(int[]), m.ReturnType);
        Assert.True(AllocatedPerCall(m, [new[] { 1, 2 }]) <= 40, "only the result array is allocated");
    }
}
