using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>
/// Parallel counted loops (ADR 0008) must not carry a dependency between iterations: a written array is read
/// only at the loop index, and a read of another array at a shifted index runs in parallel only when a runtime
/// check proves the two arrays are distinct objects.
/// </summary>
public class LoopDependenceTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "dep", exe: false)).GetType("Program")!.GetMethod("f")!;

    const int N = 200_000;

    const string Prefix = "def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  for i in 1...n\n    a[i] = a[i - 1] + 1\n  end\n  a[n - 1]\nend\n";
    const string Aliased = "def f(n : Int32) : Int32\n  a = Array(Int32).new(n)\n  b = a\n  for i in 1...n\n    a[i] = b[i - 1] + 1\n  end\n  a[n - 1]\nend\n";
    const string Stencil = "def f(n : Int32) : Int32\n  src = Array(Int32).new(n, 1)\n  dst = Array(Int32).new(n)\n  for i in 1...n - 1\n    dst[i] = src[i - 1] + src[i + 1]\n  end\n  dst[n / 2]\nend\n";

    [Fact]
    public void Loop_carried_dependency_stays_serial()
    {
        Assert.DoesNotContain("__parfor", AliceCompiler.Transpile(Prefix));
        Assert.Equal(N - 1, Compile(Prefix).Invoke(null, [N]));
    }

    [Fact]
    public void Aliased_arrays_take_the_serial_path()
        => Assert.Equal(N - 1, Compile(Aliased).Invoke(null, [N]));

    [Fact]
    public void Stencil_over_a_distinct_array_stays_parallel()
    {
        var cs = AliceCompiler.Transpile(Stencil);
        Assert.Contains("__parfor", cs);
        Assert.Contains("ReferenceEquals(dst, src)", cs);
        Assert.Equal(2, Compile(Stencil).Invoke(null, [N]));
    }

    // row-major image: iteration y writes [y * w, y * w + w), disjoint from every other row
    const string Rows2D = "def f(h : Int32, w : Int32) : Int32\n  img = Array(Int32).new(h * w)\n  for y in 0...h\n    for x in 0...w\n      it = 0\n      while it < (x + y) % 7\n        it += 1\n      end\n      img[y * w + x] = it\n    end\n  end\n  s = 0\n  for v in img\n    s += v\n  end\n  s\nend\n";
    const string Rows2DIndexLocal = "def f(h : Int32, w : Int32) : Int32\n  img = Array(Int32).new(h * w)\n  for y in 0...h\n    for x in 0...w\n      at = y * w + x\n      img[at] = (x * 3 + y) % 11\n    end\n  end\n  img[h * w - 1] + img[w + 1]\nend\n";
    const string Overlapping = "def f(h : Int32, w : Int32) : Int32\n  img = Array(Int32).new(h * w + w + 1)\n  for y in 0...h\n    for x in 0..w\n      img[y * w + x] = y\n    end\n  end\n  img[w]\nend\n";

    [Fact]
    public void Row_major_writes_run_rows_in_parallel()
    {
        Assert.Contains("__parfor", AliceCompiler.Transpile(Rows2D));
        Assert.Contains("__parfor", AliceCompiler.Transpile(Rows2DIndexLocal));
        int h = 600, w = 500, s = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) s += (x + y) % 7;
        Assert.Equal(s, Compile(Rows2D).Invoke(null, [h, w]));
        Assert.Equal((499 * 3 + 599) % 11 + (1 * 3 + 1) % 11, Compile(Rows2DIndexLocal).Invoke(null, [600, 500]));
    }

    [Fact]
    public void Overlapping_rows_stay_serial()   // x in 0..w touches the next row's first cell
        => Assert.DoesNotContain("__parfor", AliceCompiler.Transpile(Overlapping));
}
