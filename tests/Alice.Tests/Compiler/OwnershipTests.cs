using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Ownership (ADR 0016): a local array of unmanaged elements that never escapes is owned. Its buffer comes from and
/// returns to an exact-length pool (`Alice.Runtime.Own`) at reassignment and method exit, so a loop that allocates a
/// fresh row per iteration stops allocating once warm. `own x = ...` turns the inference into a checked contract:
/// an escape is AL0207 and a use after move is AL0208. The GC stays the fallback, so a skipped release is never unsafe.
/// </summary>
[Collection("console")]
public class OwnershipTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "own", exe: false)).GetType("Program")!.GetMethod("f")!;

    static long AllocatedPerCall(Func<int, int> f, int arg)
    {
        for (int i = 0; i < 20; i++) f(arg);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) f(arg);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 50;
    }

    // editdist's rolling rows: `previous = row` moves the row, the old previous goes back to the pool
    const string Rows = """
        def f(n : Int32) : Int32
          previous = Array(Int32).new(n + 1)
          for j in 0..n
            previous[j] = j
          end
          for i in 1..n
            row = Array(Int32).new(n + 1)
            row[0] = i
            for j in 1..n
              cost = (i * 7 + j * 3) % 5 == 0 ? 0 : 1
              row[j] = [row[j - 1] + 1, previous[j] + 1, previous[j - 1] + cost].min
            end
            previous = row
          end
          previous[n]
        end
        """;

    static int RowsReference(int n)
    {
        var prev = Enumerable.Range(0, n + 1).ToArray();
        for (int i = 1; i <= n; i++)
        {
            var row = new int[n + 1];
            row[0] = i;
            for (int j = 1; j <= n; j++) row[j] = Math.Min(Math.Min(row[j - 1] + 1, prev[j] + 1), prev[j - 1] + ((i * 7 + j * 3) % 5 == 0 ? 0 : 1));
            prev = row;
        }
        return prev[n];
    }

    [Fact]
    public void Rolling_rows_are_owned_and_recycled()
    {
        var cs = AliceCompiler.Transpile(Rows);
        Assert.Contains("Own<int>.Rent(", cs);
        Assert.Contains("Own<int>.Release(previous)", cs);
    }

    [Fact]
    public void Rolling_rows_compute_the_same_and_stop_allocating()
    {
        var f = Compile(Rows).CreateDelegate<Func<int, int>>();
        Assert.Equal(RowsReference(300), f(300));
        Assert.Equal(RowsReference(1), f(1));
        Assert.True(AllocatedPerCall(f, 300) < 64, "a warm call should reuse both row buffers");
    }

    [Fact]
    public void Returned_array_is_moved_out_not_released()
    {
        var m = Compile("def f(n : Int32) : Array(Int32)\n  a = Array(Int32).new(n, 7)\n  a\nend\n");
        var a = (int[])m.Invoke(null, [4])!;
        var b = (int[])m.Invoke(null, [4])!;
        Assert.NotSame(a, b);
        Assert.Equal([7, 7, 7, 7], a);
    }

    [Fact]
    public void Borrowing_helpers_keep_ownership()
    {
        var src = "def fill(xs : Array(Float64), v : Float64)\n  for i in 0...xs.size\n    xs[i] = v\n  end\nend\ndef f(n : Int32) : Int32\n  t = Array(Float64).new(n)\n  fill(t, 2.0)\n  t[n - 1].to_i32\nend\n";
        Assert.Contains("Own<double>.Release(t)", AliceCompiler.Transpile(src));
        Assert.Equal(2, Compile(src).Invoke(null, [10]));
    }

    [Theory]
    [InlineData("def keep(xs : Array(Int32))\n  @@last = xs\nend\ndef f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  keep(a)\n  0\nend\n")]   // retained by the callee
    [InlineData("def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  b = a\n  a[0] = 1\n  b[0]\nend\n")]                                       // read after move
    [InlineData("def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  g = ->(i : Int32) { a[i] }\n  g.call(0)\nend\n")]                       // captured
    [InlineData("def f(n : Int32) : Int32\n  a = Array(Int32).new(n, 1)\n  b = Array(Int32).new(n)\n  s = 0\n  for v in a\n    b = a\n    s += v\n  end\n  s + b[0]\nend\n")]   // moved while being iterated
    public void Escaping_arrays_are_not_owned(string src)
        => Assert.DoesNotContain("Own<", AliceCompiler.Transpile(src));

    [Fact]
    public void Own_contract_rejects_an_escape()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("def keep(xs : Array(Int32))\n  @@last = xs\nend\ndef f(n : Int32) : Int32\n  own a = Array(Int32).new(n)\n  keep(a)\n  0\nend\n"));
        Assert.Equal("AL0207", ex.Diagnostics[0].Code);
    }

    [Fact]
    public void Own_contract_rejects_use_after_move()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("def f(n : Int32) : Int32\n  own a = Array(Int32).new(n)\n  own b = a\n  a[0]\nend\n"));
        Assert.Equal("AL0208", ex.Diagnostics[0].Code);
    }

    [Fact]
    public void Own_declares_an_owned_local()
    {
        var src = "def f(n : Int32) : Int32\n  own a = Array(Int64).new(n, 3_i64)\n  s = 0_i64\n  for i in 0...n\n    s += a[i]\n  end\n  s.to_i32\nend\n";
        Assert.Contains("Own<long>.Release(a)", AliceCompiler.Transpile(src));
        Assert.Equal(30, Compile(src).Invoke(null, [10]));
    }
}
