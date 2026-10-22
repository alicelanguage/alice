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

    // 0.12.2: Bend-style single-owner update. An owned array is the only reference to its buffer,
    // so `xs = xs.map { }` with the same element type can overwrite it in place instead of allocating a new array.
    const string MapInPlace = "def f(n : Int32) : Int32\n  xs = Array(Int32).new(n, 3)\n  xs = xs.map { |v| v * 2 + 1 }\n  s = 0\n  for v in xs\n    s += v\n  end\n  s\nend\n";

    [Fact]
    public void Owned_map_rebind_updates_in_place()
    {
        var cs = AliceCompiler.Transpile(MapInPlace);
        Assert.Matches(@"xs\[__i\d+\] = ", cs);   // overwritten in place
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cs, @"Release\(xs\)"));   // only at exit: the map keeps the buffer
        var f = Compile(MapInPlace).CreateDelegate<Func<int, int>>();
        Assert.Equal(70, f(10));
        Assert.True(AllocatedPerCall(f, 1000) < 64, "the map should reuse the owned buffer");
    }

    [Fact]
    public void Shared_map_rebind_still_copies()
    {
        // `b = a` moves the array; `a.map` then reads a moved local, so `a` is not owned and the map allocates
        var src = "def f(n : Int32) : Int32\n  a = Array(Int32).new(n, 3)\n  b = a\n  a = a.map { |v| v * 2 }\n  b[0] + a[0]\nend\n";
        Assert.Equal(9, Compile(src).Invoke(null, [4]));
    }

    // `own` parameters: the caller moves its array into the callee, which owns it (updates in place, releases it)
    const string Scale = "def scale(own xs : Array(Float64), k : Float64) : Array(Float64)\n  xs = xs.map { |v| v * k }\n  xs\nend\n";

    [Fact]
    public void Own_parameter_is_moved_in_and_updated_in_place()
    {
        var src = Scale + "def f(n : Int32) : Int32\n  a = Array(Float64).new(n, 1.5)\n  b = scale(a, 2.0)\n  b[0].to_i32 + b.size\nend\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.Matches(@"xs\[__i\d+\] = ", cs);   // the callee owns xs: the map is in place
        Assert.DoesNotContain("Release(a)", cs);     // moved into scale: the caller no longer releases it
        Assert.Equal(7, Compile(src).Invoke(null, [4]));
    }

    [Fact]
    public void Own_parameter_read_after_move_is_an_error()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(Scale + "def f(n : Int32) : Float64\n  own a = Array(Float64).new(n, 1.5)\n  b = scale(a, 2.0)\n  a[0] + b[0]\nend\n"));
        Assert.Equal("AL0208", ex.Diagnostics[0].Code);
    }

    [Fact]
    public void Own_parameter_rejects_a_shared_array()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(Scale + "def f(ys : Array(Float64)) : Float64\n  scale(ys, 2.0)[0]\nend\n"));
        Assert.Equal("AL0207", ex.Diagnostics[0].Code);
    }

    // `borrow` parameters: the inferred borrow (ADR 0016) as a checked contract
    [Fact]
    public void Borrow_parameter_keeps_the_callers_ownership()
    {
        var src = "def total(borrow xs : Array(Int64)) : Int64\n  s = 0_i64\n  for v in xs\n    s += v\n  end\n  s\nend\ndef f(n : Int32) : Int32\n  own a = Array(Int64).new(n, 2_i64)\n  total(a).to_i32\nend\n";
        Assert.Contains("Own<long>.Release(a)", AliceCompiler.Transpile(src));
        Assert.Equal(20, Compile(src).Invoke(null, [10]));
    }

    [Theory]
    [InlineData("def keep(borrow xs : Array(Int32)) : Array(Int32)\n  xs\nend\n")]                     // returned
    [InlineData("def keep(borrow xs : Array(Int32))\n  @@last = xs\nend\n")]                           // stored
    [InlineData("def keep(borrow xs : Array(Int32)) : Int32\n  g = ->(i : Int32) { xs[i] }\n  g.call(0)\nend\n")]   // captured
    public void Borrow_parameter_that_escapes_is_an_error(string src)
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile(src));
        Assert.Equal("AL0213", ex.Diagnostics[0].Code);
    }

    // `scope ... end`: a lifetime region. Arrays created inside must be owned and die at `end`
    [Fact]
    public void Scope_releases_its_arrays_at_end()
    {
        var src = "def f(n : Int32) : Int32\n  s = 0\n  scope\n    t = Array(Int32).new(n, 1)\n    for v in t\n      s += v\n    end\n  end\n  u = Array(Int32).new(n, 2)\n  s + u[0]\nend\n";
        var cs = AliceCompiler.Transpile(src);
        Assert.Matches(@"Release\(t\w*\);\s*\}\s*(#line[^\n]*\n\s*)?var u", cs);   // t dies at the end of its scope, before u exists
        var f = Compile(src).CreateDelegate<Func<int, int>>();
        Assert.Equal(12, f(10));
        Assert.True(AllocatedPerCall(f, 1000) < 64);
    }

    [Fact]
    public void Scope_rejects_an_array_that_outlives_it()
    {
        var ex = Assert.Throws<CompileException>(() => AliceCompiler.Transpile("def keep(xs : Array(Int32))\n  @@last = xs\nend\ndef f(n : Int32) : Int32\n  scope\n    t = Array(Int32).new(n)\n    keep(t)\n  end\n  0\nend\n"));
        Assert.Equal("AL0207", ex.Diagnostics[0].Code);
    }

    // owned results: a method returning only a fresh, non-escaping array hands ownership to the caller
    const string Source = "def source(n : Int32) : Array(Int32)\n  xs = Array(Int32).new(n)\n  for i in 0...n\n    xs[i] = i * 3\n  end\n  xs\nend\n";

    [Fact]
    public void Fresh_result_is_adopted_by_the_caller()
    {
        var src = Source + "def f(n : Int32) : Int32\n  src = source(n)\n  src[n - 1]\nend\n";
        Assert.Contains("Own<int>.Release(src)", AliceCompiler.Transpile(src));
        var f = Compile(src).CreateDelegate<Func<int, int>>();
        Assert.Equal(2997, f(1000));
        Assert.True(AllocatedPerCall(f, 1000) < 64, "source's buffer should come back from the pool");
    }

    [Fact]
    public void Result_that_may_be_shared_is_not_adopted()
    {
        var src = "def source(n : Int32) : Array(Int32)\n  xs = Array(Int32).new(n)\n  @@keep = xs\n  xs\nend\ndef f(n : Int32) : Int32\n  src = source(n)\n  src[0]\nend\n";
        Assert.DoesNotContain("Release(src)", AliceCompiler.Transpile(src));
    }
}
