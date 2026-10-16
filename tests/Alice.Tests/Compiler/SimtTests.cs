using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>SIMT vectorization: data-dependent inner while loops run on SIMD lanes with masks; results are identical.</summary>
public class SimtTests
{
    static MethodInfo Compile(string src) =>
        AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "simt", exe: false)).GetType("Program")!.GetMethod("f")!;

    const string Row = "def f(size : Int32, py : Int32) : Int64\n  inside : Int64 = 0\n  ci = 2.0 * py / size - 1.0\n  for px in 0...size\n    cr = 2.0 * px / size - 1.5\n    zr = 0.0\n    zi = 0.0\n    it = 0\n    while it < 64 && zr * zr + zi * zi <= 4.0\n      t = zr * zr - zi * zi + cr\n      zi = 2.0 * zr * zi + ci\n      zr = t\n      it += 1\n    end\n    inside += 1 if it == 64\n  end\n  inside\nend";

    static long RowRef(int size, int py)
    {
        long inside = 0;
        double ci = 2.0 * py / size - 1.0;
        for (int px = 0; px < size; px++)
        {
            double cr = 2.0 * px / size - 1.5, zr = 0, zi = 0;
            int it = 0;
            while (it < 64 && zr * zr + zi * zi <= 4.0) { double t = zr * zr - zi * zi + cr; zi = 2.0 * zr * zi + ci; zr = t; it++; }
            if (it == 64) inside++;
        }
        return inside;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(7, 3)]
    [InlineData(101, 50)]
    [InlineData(400, 211)]
    public void Lanes_reproduce_scalar_results_exactly(int size, int py)
        => Assert.Equal(RowRef(size, py), Compile(Row).Invoke(null, [size, py]));

    [Fact]
    public void Inner_while_runs_on_vector_lanes()
    {
        var cs = AliceCompiler.Transpile(Row + "\n");
        Assert.Contains("Vector<double>.Indices", cs);
        Assert.Contains("ConditionalSelect", cs);
    }

    [Theory]
    [InlineData("def f(n : Int32) : Int64\n  s : Int64 = 0\n  for i in 0...n\n    k = i\n    while k > 1\n      k = k / 2\n    end\n    s += k\n  end\n  s\nend")]
    [InlineData("def f(n : Int32) : Int64\n  s : Int64 = 0\n  for i in 0...n\n    x = 1.5f32\n    while x < 10.0f32\n      x = x * 2.0f32\n    end\n    s += 1 if x > 11.0f32\n  end\n  s\nend")]
    public void Integer_division_and_float32_stay_scalar(string src)
        => Assert.DoesNotContain("Indices", AliceCompiler.Transpile(src + "\n"));
}
