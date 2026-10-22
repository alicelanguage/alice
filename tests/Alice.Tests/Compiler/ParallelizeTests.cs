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
        Assert.Contains("global::Alice.Runtime.Par.", AliceCompiler.Transpile(src + "\n"));   // fused update+sum sweep runs in parallel
    }
}

[Collection("console")]   // some tests run programs and capture stdout
public class LoopParallelizeTests
{
    static System.Reflection.MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "lp", exe: false)).GetType("Program")!.GetMethod("f")!;

    const string Mandel = "def f(size : Int32)\n  inside : Int64 = 0\n  for py in 0...size\n    ci = 2.0 * py / size - 1.0\n    for px in 0...size\n      cr = 2.0 * px / size - 1.5\n      zr = 0.0\n      zi = 0.0\n      it = 0\n      while it < 50 && zr * zr + zi * zi <= 4.0\n        t = zr * zr - zi * zi + cr\n        zi = 2.0 * zr * zi + ci\n        zr = t\n        it += 1\n      end\n      inside += 1 if it == 50\n    end\n  end\n  inside\nend";

    static long MandelRef(int size)
    {
        long inside = 0;
        for (int py = 0; py < size; py++)
        {
            double ci = 2.0 * py / size - 1.0;
            for (int px = 0; px < size; px++)
            {
                double cr = 2.0 * px / size - 1.5, zr = 0, zi = 0;
                int it = 0;
                while (it < 50 && zr * zr + zi * zi <= 4.0) { double t = zr * zr - zi * zi + cr; zi = 2.0 * zr * zi + ci; zr = t; it++; }
                if (it == 50) inside++;
            }
        }
        return inside;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(300)]
    public void Reduction_loop_is_parallel_and_exact(int size)
    {
        Assert.Equal(MandelRef(size), Compile(Mandel).Invoke(null, [size]));
        Assert.Contains("Par.Reduce", AliceCompiler.Transpile(Mandel + "\n"));
    }

    [Fact]
    public void Disjoint_array_writes_parallelize()
    {
        var src = "def f(n : Int32)\n  out = Array(Int64).new(n)\n  for i in 0...n\n    s = 0L\n    for k in 0...100\n      s += k * i\n    end\n    out[i] = s\n  end\n  out.sum\nend";
        long expected = Enumerable.Range(0, 5000).Sum(i => Enumerable.Range(0, 100).Sum(k => (long)k * i));
        Assert.Equal(expected, Compile(src).Invoke(null, [5000]));
        Assert.Contains("Par.Range", AliceCompiler.Transpile(src + "\n"));
    }

    [Fact]
    public void Loops_calling_pure_user_methods_parallelize()
    {
        var src = "class Node\n  getter l : Node?\n  getter r : Node?\n  def initialize(@l : Node?, @r : Node?)\n  end\n  def count : Int32\n    a = l\n    b = r\n    if a.nil? || b.nil?\n      1\n    else\n      1 + a.count + b.count\n    end\n  end\nend\ndef build(d : Int32) : Node\n  d > 0 ? Node.new(build(d - 1), build(d - 1)) : Node.new(nil, nil)\nend\ndef f(n : Int32)\n  c = 0\n  k = 0\n  while k < 2\n    for i in 0...n\n      c += build(6).count\n    end\n    k += 1\n  end\n  c\nend";
        Assert.Equal(2 * 40 * 127, Compile(src).Invoke(null, [40]));
        Assert.Contains("Par.Reduce", AliceCompiler.Transpile(src + "\n"));
    }

    [Fact]
    public void Loops_calling_impure_methods_stay_serial()
    {
        var src = "class C\n  getter n : Int32 = 0\n  def bump : Int32\n    @n += 1\n    @n\n  end\nend\ndef f(k : Int32)\n  c = C.new\n  s = 0\n  for i in 0...k\n    s += c.bump\n  end\n  s\nend";
        Assert.DoesNotContain("Par.", AliceCompiler.Transpile(src + "\n"));
    }

    [Theory]
    [InlineData("def f(n : Int32)\n  xs = List(Int32).new\n  for i in 0...n\n    xs.Add(i)\n  end\n  xs.Count\nend")]
    [InlineData("def f(n : Int32)\n  last = 0\n  for i in 0...n\n    last = i\n  end\n  last\nend")]
    [InlineData("def f(n : Int32)\n  s = 0\n  for i in 0...n\n    s += 1\n    break if s > 10\n  end\n  s\nend")]
    [InlineData("def f(n : Int32)\n  s = 0\n  for i in 0...n\n    s += s + i\n  end\n  s\nend")]
    public void Order_dependent_loops_stay_serial(string src)
        => Assert.DoesNotContain("Par.", AliceCompiler.Transpile(src + "\n"));

    [Fact]
    public void Tiny_inner_stencil_loops_stay_serial()
    {
        // game-of-life neighbourhood: the 3-trip dy loop sits inside the per-cell loops; dispatching it per cell is pure overhead
        const string src = "def f(cells : Array(Int32), n : Int32) : Int32\n  total = 0\n  for y in 0...n\n    for x in 0...n\n      count = 0\n      for dy in -1..1\n        for dx in -1..1\n          count += cells[((y + dy + n) % n) * n + (x + dx + n) % n]\n        end\n      end\n      total += count\n    end\n  end\n  total\nend\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.DoesNotMatch(@"for \(var d[xy]_\d+ = __a", cs);   // the 3-trip neighbourhood loops are never the parallel loop
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, @">= 2 && global::Alice.Runtime.Par.Workers"));   // only the outermost loop is "heavy"
    }

    [Fact]
    public void Constant_short_trip_counts_emit_no_parallel_path()
    {
        var cs = AliceCompiler.Transpile("def f(xs : Array(Float64)) : Float64\n  s = 0.0\n  for i in 0...3\n    s += Math.sqrt(xs[i])\n  end\n  s\nend\n");
        Assert.DoesNotContain("Par.", cs);
    }

    const string Fib = "def fib(n : Int32) : Int64\n  if n < 2\n    n.to_i64\n  else\n    a = fib(n - 1)\n    b = fib(n - 2)\n    a + b\n  end\nend\n";

    [Fact]
    public void Independent_pure_recursive_calls_fork_with_lazy_task_creation()
    {
        var cs = AliceCompiler.Transpile(Fib);
        Assert.Contains("global::Alice.Runtime.Par.Forkable", cs);   // below the depth cutoff the calls run inline
        Assert.Contains("global::Alice.Runtime.Par.Fork(", cs);
        var f = AliceCompiler.Load(AliceCompiler.Emit([cs], "fork", exe: false)).GetType("Program")!.GetMethod("fib")!;
        Assert.Equal(75025L, f.Invoke(null, [25]));
    }

    [Theory]
    [InlineData("def g(n : Int32) : Int32\n  puts n\n  n\nend\ndef f(n : Int32) : Int32\n  return 0 if n < 1\n  a = f(n - 1)\n  b = g(n)\n  a + b\nend\n")]   // impure callee
    [InlineData("def f(n : Int32) : Int32\n  return 0 if n < 1\n  a = f(n - 1)\n  b = f(a - 1)\n  a + b\nend\n")]                                  // second call reads the first result
    [InlineData("def sq(n : Int32) : Int32\n  n * n\nend\ndef f(n : Int32) : Int32\n  a = sq(n)\n  b = sq(n + 1)\n  a + b\nend\n")]               // no recursion: too small to fork
    public void Forks_need_purity_independence_and_recursion(string src)
        => Assert.DoesNotContain("Par.Fork", AliceCompiler.Transpile(src));

    [Fact]
    public void Forked_exceptions_surface_unwrapped()
    {
        const string src = "def f(n : Int32) : Int32\n  return 1 / n if n < 1\n  a = f(n - 1)\n  b = f(n - 2)\n  a + b\nend\nputs f(20)\n";
        Assert.Contains("Par.Fork", AliceCompiler.Transpile(src));
        Assert.ThrowsAny<DivideByZeroException>(() => AliceCompiler.Run(src));
    }

    [Fact]
    public void Forking_method_has_no_closure_on_its_sequential_path()
    {
        // lambdas capturing a parameter make Roslyn allocate the closure at method entry, on every recursive call:
        // the fork lives in a helper so only the forking calls near the root pay for a closure
        var cs = AliceCompiler.Transpile(Fib);
        int start = cs.IndexOf("public static long fib(int n)");
        string body = cs.Substring(start, cs.IndexOf("\n}\n", start) - start);
        Assert.DoesNotContain("=>", body);
    }

    const string Radix = "def radix(values : Array(Int64), shift : Int32) : Array(Int64)\n  return values if values.Length < 2 || shift < 0\n  zeros = Array(Int64).new(values.Length)\n  ones = Array(Int64).new(values.Length)\n  z = 0\n  o = 0\n  for v in values\n    if (v >> shift) & 1 == 0\n      zeros[z] = v\n      z += 1\n    else\n      ones[o] = v\n      o += 1\n    end\n  end\n  left = Array(Int64).new(z)\n  right = Array(Int64).new(o)\n  for i in 0...z\n    left[i] = zeros[i]\n  end\n  for i in 0...o\n    right[i] = ones[i]\n  end\n  sl = radix(left, shift - 1)\n  sr = radix(right, shift - 1)\n  out = Array(Int64).new(values.Length)\n  for i in 0...z\n    out[i] = sl[i]\n  end\n  for i in 0...o\n    out[z + i] = sr[i]\n  end\n  out\nend\n";

    [Fact]
    public void Writes_into_fresh_local_arrays_keep_a_method_pure()
    {
        var cs = AliceCompiler.Transpile(Radix + "xs = [5_i64, 3_i64, 9_i64, 1_i64, 7_i64, 2_i64]\nputs radix(xs, 3).inspect\n");
        Assert.Contains("Par.Fork(", cs);
        Assert.Equal("[1, 2, 3, 5, 7, 9]", AliceCompiler.Run(Radix + "xs = [5_i64, 3_i64, 9_i64, 1_i64, 7_i64, 2_i64]\nputs radix(xs, 3).inspect\n").Trim());
    }

    [Theory]
    [InlineData("def f(xs : Array(Int32), n : Int32) : Int32\n  return 0 if n < 1\n  xs[0] = n\n  a = f(xs, n - 1)\n  b = f(xs, n - 2)\n  a + b\nend\n")]                    // writes the caller's array
    [InlineData("def f(xs : Array(Int32), n : Int32) : Int32\n  return 0 if n < 1\n  ys = Array(Int32).new(1)\n  ys = xs\n  ys[0] = n\n  a = f(xs, n - 1)\n  b = f(xs, n - 2)\n  a + b\nend\n")]   // alias of a parameter
    public void Writes_through_shared_arrays_stay_impure(string src)
        => Assert.DoesNotContain("Par.Fork", AliceCompiler.Transpile(src));

    [Fact]
    public void Parallel_multi_assign_forks_like_bends_parallel_call()
    {
        // Bend: `a b = pow2(p) pow2(p)`; Alice: `a, b = pow2(n - 1), pow2(n - 1)`
        const string src = "def pow2(n : Int32) : Int64\n  return 1_i64 if n == 0\n  a, b = pow2(n - 1), pow2(n - 1)\n  a + b\nend\nputs pow2(16)\n";
        Assert.Contains("Par.Fork(", AliceCompiler.Transpile(src));
        Assert.Equal("65536", AliceCompiler.Run(src).Trim());
    }

    [Fact]
    public void Multi_assign_fork_into_existing_variables()
    {
        const string src = "def f(n : Int32) : Int64\n  return 1_i64 if n < 2\n  a = 0_i64\n  b = 0_i64\n  a, b = f(n - 1), f(n - 2)\n  a + b\nend\nputs f(20)\n";
        Assert.Contains("Par.Fork(", AliceCompiler.Transpile(src));
        Assert.Equal("10946", AliceCompiler.Run(src).Trim());
    }

    // 0.12.2: HVM reduces every independent redex of a term in parallel; Alice forks the two
    // independent recursive calls of one arithmetic expression without naming their results
    [Theory]
    [InlineData("def fib(n : Int32) : Int64\n  return n.to_i64 if n < 2\n  fib(n - 1) + fib(n - 2)\nend\nputs fib(24)\n", "46368")]
    [InlineData("def fib(n : Int32) : Int64\n  return n.to_i64 if n < 2\n  return fib(n - 1) + fib(n - 2)\nend\nputs fib(24)\n", "46368")]
    [InlineData("def sum(d : Int32) : Int64\n  return 1_i64 if d == 0\n  s = 1_i64 + sum(d - 1) * 2_i64 + sum(d - 1)\n  s\nend\nputs sum(10)\n", "88573")]
    public void Two_recursive_calls_in_one_expression_fork(string src, string expected)
    {
        var cs = AliceCompiler.Transpile(src);
        Assert.Contains("Par.Fork(", cs);
        Assert.Equal(expected, AliceCompiler.Run(src).Trim());
    }

    [Theory]
    [InlineData("def f(n : Int32) : Bool\n  return true if n < 1\n  n > 3 && f(n - 1) && f(n - 2)\nend\n")]                   // short-circuit: the second call may never run
    [InlineData("def f(n : Int32) : Int32\n  return 0 if n < 1\n  n > 3 ? f(n - 1) : f(n - 2)\nend\n")]                     // only one branch runs
    [InlineData("def g(n : Int32) : Int32\n  puts n\n  n\nend\ndef f(n : Int32) : Int32\n  return 0 if n < 1\n  f(n - 1) + g(n)\nend\n")]   // impure operand
    [InlineData("def f(n : Int32) : Int32\n  return 0 if n < 1\n  f(f(n - 1) - 1)\nend\n")]                                  // nested: dependent
    public void Expression_forks_keep_evaluation_semantics(string src)
        => Assert.DoesNotContain("Par.Fork", AliceCompiler.Transpile(src));

    [Fact]
    public void Loops_calling_pure_runtime_functions_parallelize()
    {
        // Digest is marked [Pure] in Alice.Runtime: hashing each index is independent work
        const string src = "def leaves(n : Int32) : Array(String)\n  out = Array(String).new(n)\n  for i in 0...n\n    out[i] = Digest::SHA256.hexdigest(\"#{i}:x\")\n  end\n  out\nend\nputs leaves(100_000)[99_999]\n";
        Assert.Contains("Par.Range", AliceCompiler.Transpile(src));
        Assert.Equal(System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("99999:x"))), AliceCompiler.Run(src).Trim());
    }

    [Fact]
    public void Loops_calling_unmarked_dotnet_methods_stay_serial()
    {
        const string src = "def f(n : Int32) : Array(Int32)\n  out = Array(Int32).new(n)\n  for i in 0...n\n    out[i] = System.Random.Shared.Next(i + 1)\n  end\n  out\nend\n";
        Assert.DoesNotContain("Par.Range", AliceCompiler.Transpile(src));
    }

    [Fact]
    public void Pure_module_functions_parallelize_through_their_static_receiver()
    {
        const string src = "module H\n  def self.sha(t : String) : String\n    Digest::SHA256.hexdigest(t)\n  end\nend\ndef f(n : Int32) : Array(String)\n  out = Array(String).new(n)\n  for i in 0...n\n    out[i] = H.sha(\"#{i}\")\n  end\n  out\nend\n";
        Assert.Contains("Par.Range", AliceCompiler.Transpile(src));
    }

    [Fact]
    public void Literal_array_min_in_a_loop_body_is_pure()
    {
        // merkle's pair loop: right = level[[i * 2 + 1, level.size - 1].min]
        const string src = "def f(level : Array(String)) : Array(String)\n  nxt = Array(String).new((level.size + 1) / 2)\n  for i in 0...nxt.size\n    l = level[i * 2]\n    r = level[[i * 2 + 1, level.size - 1].min]\n    nxt[i] = Digest::SHA256.hexdigest(l + r)\n  end\n  nxt\nend\n";
        Assert.Contains("Par.Range", AliceCompiler.Transpile(src));
    }

    [Fact]
    public void Loops_over_numeric_primitives_parallelize()
    {
        const string src = "def f(xs : Array(Float64)) : Array(Int32)\n  out = Array(Int32).new(xs.size)\n  for i in 0...xs.size\n    out[i] = xs[i].log2.floor.to_i32.clamp(0, 99) + xs[i].sign\n  end\n  out\nend\n";
        Assert.Contains("Par.Range", AliceCompiler.Transpile(src));
    }
}
