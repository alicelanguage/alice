using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Soundness holes in automatic parallelization and AL0202, each found as a program the compiler used to accept
/// (or parallelize) although iterations or tasks interfere.
/// </summary>
[Collection("console")]
public class ParallelSoundnessTests
{
    static System.Reflection.MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "ps", exe: false)).GetType("Program")!.GetMethod("f")!;

    // iteration i writes cells [i * w, i * w + w) and reads cell i: iteration 0 writes the cell iteration 1 reads
    const string RowWriteReadsColumn = "def f(a : Array(Int32), w : Int32, n : Int32)\n  for i in 0...n\n    for j in 0...w\n      a[i * w + j] = a[i] + 1\n    end\n  end\nend\n";

    [Theory]
    [InlineData(RowWriteReadsColumn)]
    // two index shapes on the same array: a[i] and a[i * w + j] overlap across iterations
    [InlineData("def f(a : Array(Int32), w : Int32, n : Int32)\n  for i in 0...n\n    a[i] = 1\n    for j in 0...w\n      a[i * w + j] = 2\n    end\n  end\nend\n")]
    public void Mixed_index_shapes_on_one_array_stay_serial(string src)
        => Assert.DoesNotContain("Par.", AliceCompiler.Transpile(src));

    [Fact]
    public void Mixed_index_shapes_on_one_array_keep_serial_results()
    {
        int w = 2, n = 2000;
        var a = new int[n * w];
        var e = new int[n * w];
        for (int i = 0; i < n; i++) for (int j = 0; j < w; j++) e[i * w + j] = e[i] + 1;
        Compile(RowWriteReadsColumn).Invoke(null, [a, w, n]);
        Assert.Equal(e, a);
    }

    [Fact]
    public void Arrays_written_and_read_in_different_shapes_get_an_alias_guard()
    {
        var src = "def f(dst : Array(Int32), src : Array(Int32), w : Int32, n : Int32)\n  for i in 0...n\n    for j in 0...w\n      dst[i * w + j] = src[i] + j\n    end\n  end\nend\n";
        Assert.Contains("ReferenceEquals(dst, src)", AliceCompiler.Transpile(src));
        int w = 3, n = 3000;
        var a = Enumerable.Range(0, n * w).ToArray();
        var e = (int[])a.Clone();
        for (int i = 0; i < n; i++) for (int j = 0; j < w; j++) e[i * w + j] = e[i] + j;
        Compile(src).Invoke(null, [a, a, w, n]);   // same array twice: must run serially
        Assert.Equal(e, a);
    }

    [Fact]
    public void A_call_whose_override_is_impure_is_not_pure()
    {
        var src = "class A\n  def v(x : Int32) : Int32\n    x + 1\n  end\nend\nclass B < A\n  @n : Int32 = 0\n  def v(x : Int32) : Int32\n    @n += 1\n    x\n  end\nend\ndef f(o : A, n : Int32)\n  s = 0\n  for i in 0...n\n    s += o.v(i)\n  end\n  s\nend\n";
        Assert.DoesNotContain("Par.", AliceCompiler.Transpile(src));
    }

    [Fact]
    public void A_fork_of_a_call_whose_override_is_impure_is_rejected()
    {
        var src = "class A\n  def g(n : Int32) : Int32\n    n\n  end\n  def t(n : Int32) : Int32\n    return 0 if n < 2\n    a = t(n - 1)\n    b = g(n)\n    a + b\n  end\nend\nclass B < A\n  @c : Int32 = 0\n  def g(n : Int32) : Int32\n    @c += 1\n    n\n  end\nend\n";
        Assert.DoesNotContain("Par.Fork", AliceCompiler.Transpile(src));
    }

    [Theory]
    // a self-call to a method that writes fields, from a spawned task
    [InlineData("class Counter\n  @n : Int32 = 0\n  def bump\n    @n += 1\n  end\n  def run\n    t = spawn { bump }\n    t.wait\n  end\nend\n")]
    // par_each hands out values, not disjoint indices: two elements may be equal
    [InlineData("counts = Array(Int32).new(4)\n[1, 1, 2].par_each { |x| counts[x] = x }\n")]
    // the upper bound of one chunk is the lower bound of the next
    [InlineData("a = Array(Int32).new(101)\nPar.Range(100) { |lo, hi| a[hi] = 1 }\n")]
    // the index parameter is moved inside the block
    [InlineData("a = Array(Int32).new(10)\nPar.For(10) { |i|\n  i = 0\n  a[i] = 1\n}\n")]
    public void Races_al0202_used_to_miss_are_rejected(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0202", ex.Diagnostics[0].Code);
    }

    [Fact]
    public void A_branch_local_carried_across_iterations_stays_serial()
    {
        // `t` is declared once for the method: iterations that skip the branch read the previous iteration's value
        var src = "def f(n : Int32) : Int64\n  a = Array(Int64).new(n)\n  for i in 0...n\n    if i % 1000 == 0\n      t = i.to_i64\n    end\n    a[i] = t\n  end\n  a.sum\nend\n";
        Assert.DoesNotContain("Par.", AliceCompiler.Transpile(src));
        long e = 0;
        for (int i = 0; i < 200_000; i++) e += i / 1000 * 1000;
        Assert.Equal(e, Compile(src).Invoke(null, [200_000]));
    }

    [Theory]
    // a method calling a field-writing method on self mutates too
    [InlineData("class C\n  @n : Int32 = 0\n  def bump\n    @n += 1\n  end\n  def twice\n    bump\n    bump\n  end\nend\nc = C.new\nPar.For(10) { |i| c.twice }\n")]
    // a field written from a (serial) block of the method
    [InlineData("class C\n  @n : Int32 = 0\n  def add_all(xs : Array(Int32))\n    xs.each { |x| @n += x }\n  end\nend\nc = C.new\nPar.For(10) { |i| c.add_all([1, 2]) }\n")]
    public void Indirect_field_writes_are_mutating_calls(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0202", ex.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("a = Array(Int32).new(100)\nPar.For(100) { |i| a[i] = i }\nputs a.sum", "4950")]
    [InlineData("a = Array(Int32).new(100)\nPar.Range(100) { |lo, hi| a[lo] = 1 }\nputs a.sum > 0", "true")]
    public void Disjoint_index_writes_still_compile(string src, string expected)
        => Assert.Equal(expected, AliceCompiler.Run(src).Trim());
}
