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
        // an owned hash (pooled) iterated with each { |k, v| }: inlined to foreach, no closure
        { "owned hash each", "def f(n : Int32) : Int32\n  h = Dictionary(Int32, Int32).new\n  h[1] = n\n  h[2] = n * 2\n  best = 0\n  h.each { |k, v| best = v if v > best }\n  best\nend\n", 5, 10 },
        { "numeric conversions", "def f(x : Float64)\n  x.to_i32 + x.round.to_i64.to_i32 + x.floor.to_u8.to_i\nend", 2.6, 2 + 3 + 2 },
        { "floored div and mod", "def f(x : Int32)\n  x / 3 + x % 5 + x / 8 + x % 16\nend", -7, -3 + 3 + -1 + 9 },
        { "bits and sign", "def f(x : Int32)\n  x.popcount + x.bit_length + x.trailing_zeros_count + x.sign + (x ^ 3) + (x & 6) + (x << 1)\nend", 12, 2 + 4 + 2 + 1 + 15 + 4 + 24 },
        { "float helpers", "def f(x : Float64)\n  x.round(2) + x.floor(1) + x.abs + x.sqrt + x.fdiv(2) + x.clamp(0.0, 1.0)\nend", 2.25, 2.25 + 2.2 + 2.25 + 1.5 + 1.125 + 1.0 },
        { "integer helpers", "def f(x : Int32)\n  x.abs + x.gcd(6) + x.clamp(0, 3) + (x.even? ? 1 : 0) + (x.between?(1, 9) ? 1 : 0) + x.tdiv(2) + x.remainder(3)\nend", 4, 4 + 2 + 3 + 1 + 1 + 2 + 1 },
        { "wrapping ops", "def f(x : Int64)\n  (x &* 31) &+ 7 &- 1\nend", 2L, 68L },
        { "divmod destructure", "def f(x : Int32)\n  q, r = x.divmod(3)\n  q * 10 + r\nend", 7, 21 },
        { "upto and downto", "def f(n : Int32)\n  s = 0\n  1.upto(n) { |i| s += i }\n  n.downto(1) { |i| s += i }\n  s\nend", 4, 20 },
        { "step", "def f(n : Int32)\n  s = 0\n  0.step(n, 2) { |i| s += i }\n  s\nend", 6, 12 },
        { "range each", "def f(n : Int32)\n  s = 0\n  (0...n).each { |i| s += i }\n  s\nend", 5, 10 },
        { "array max min sum", "def f(xs : Array(Int32))\n  xs.max + xs.min + xs.sum\nend", new[] { 3, 1, 2 }, 3 + 1 + 6 },
        { "string size and index", "def f(s : String)\n  s.size + (s[0] == 'a' ? 1 : 0) + (s.starts_with?(\"ab\") ? 1 : 0)\nend", "abc", 5 },
        { "local fill array in a loop", "def f(n : Int32)\n  s = 0.0\n  for i in 0...n\n    rgb = Array(Float64).new(3, 0.0)\n    rgb[0] = i * 1.0\n    rgb[2] = rgb[0] + 1.0\n    s += rgb[0] + rgb[1] + rgb[2] + rgb.size\n  end\n  s\nend", 3, 18.0 },   // per i: i + 0 + (i + 1) + 3
        { "local literal array", "def f(x : Int32)\n  p = [x, x * 2, 7]\n  t = 0\n  for v in p\n    t += v\n  end\n  t + p[1]\nend", 5, 5 + 10 + 7 + 10 },
        { "literal array min", "def f(x : Int32)\n  [x + 1, x * 2, 7].min\nend", 5, 6 },
        { "literal array max", "def f(x : Float64)\n  [x, 2.5].max\nend", 1.0, 2.5 },
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

    [Fact]
    public void Non_escaping_small_arrays_live_in_one_stack_buffer()
    {
        var cs = AliceCompiler.Transpile("def f(n : Int32) : Float64\n  s = 0.0\n  for i in 0...n\n    rgb = Array(Float64).new(3, 0.0)\n    rgb[1] = 2.0\n    s += rgb[1]\n  end\n  s\nend\n");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, @"stackalloc double\[3\]"));   // hoisted: not one per iteration
        Assert.Contains("global::System.Span<double> rgb", cs);
    }

    [Theory]
    [InlineData("def f(x : Int32) : Array(Int32)\n  p = [x, 1]\n  p\nend\n")]                                       // returned
    [InlineData("def g(a : Array(Int32)) : Int32\n  a[0]\nend\ndef f(x : Int32) : Int32\n  p = [x, 1]\n  g(p)\nend\n")]   // passed to a call
    [InlineData("def f(x : Int32) : Int32\n  p = [x, 1]\n  q = p\n  q[0]\nend\n")]                                  // aliased
    [InlineData("def f(x : Int32) : Int32\n  p = [x, 1]\n  p.map { |v| v * 2 }.sum\nend\n")]                          // used by a block method
    [InlineData("def f(n : Int32) : Int32\n  p = [0, 0]\n  for i in 0...n\n    p[0] += i\n  end\n  p[0]\nend\n")]       // declared outside the loop that uses it
    [InlineData("def f(x : Int32) : Int32\n  p = [x, 1]\n  p = [2, 3]\n  p[0]\nend\n")]                             // reassigned
    [InlineData("def f(n : Int32) : Int32\n  p = Array(Int32).new(n)\n  p.size\nend\n")]                             // size not a literal
    public void Escaping_or_unbounded_arrays_stay_on_the_heap(string src)
        => Assert.DoesNotContain("stackalloc", AliceCompiler.Transpile(src));
}
