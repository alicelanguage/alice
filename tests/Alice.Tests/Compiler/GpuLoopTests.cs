using System.Reflection;
using Alice.Compiler;

namespace Alice.Tests.Compiler;

/// <summary>`gpu for` launch shape, reductions and transfers (ADR 0021), run on ILGPU's CPU accelerator.</summary>
public class GpuLoopTests
{
    static readonly string IlgpuPath = typeof(ILGPU.Context).Assembly.Location;

    static GpuLoopTests() => Environment.SetEnvironmentVariable("ALICE_GPU", "cpu");   // deterministic: no device needed

    static string Cs(string src) => AliceCompiler.TranspileAll([new SourceFile("main.al", src)], [IlgpuPath])[0];

    // ILGPU's CPU accelerator emits dynamic code that refers to the kernel's assembly: load it non-collectible
    static Type Program(string cs) => Assembly.Load(AliceCompiler.Emit([cs], "gpu" + Guid.NewGuid().ToString("N"), exe: false)).GetType("Program")!;

    const string SumI64 = "def total(xs : Array(Int64)) : Int64\n  t = 0_i64\n  gpu for i in 0...xs.size\n    t += xs[i] * 2_i64\n  end\n  t\nend\n";

    [Fact]
    public void Kernels_launch_with_an_explicit_config_and_a_grid_stride_loop()
    {
        var cs = Cs(SumI64);
        Assert.Contains("LoadStreamKernel<", cs);
        Assert.DoesNotContain("LoadAutoGroupedStreamKernel", cs);
        Assert.Contains("new global::ILGPU.KernelConfig(", cs);
        Assert.Contains("global::ILGPU.Grid.DimX * global::ILGPU.Group.DimX", cs);   // the grid-stride step
        Assert.Contains("__AliceGpu.Grid(", cs);                                       // occupancy-sized, capped grid
    }

    [Fact]
    public void Reductions_accumulate_in_registers_and_add_once_per_group()
    {
        var cs = Cs(SumI64);
        Assert.Contains("global::ILGPU.Warp.ShuffleDown(", cs);
        Assert.Contains("global::ILGPU.Group.Barrier()", cs);
        Assert.Equal(1, cs.Split("global::ILGPU.Atomic.Add(").Length - 1);
        int loop = cs.IndexOf("for (int __gt"), tail = cs.IndexOf("global::ILGPU.Atomic.Add(");
        Assert.True(loop > 0 && tail > loop, cs);
        Assert.DoesNotContain("new long[1]", cs);   // the device accumulator is cached, not allocated per launch
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(100_003)]
    public void Integer_sums_match_the_serial_sum_exactly(int n)
    {
        var f = Program(Cs(SumI64)).GetMethod("total")!;
        var xs = Enumerable.Range(0, n).Select(k => (long)k * 7919 % 100_003 - 5000).ToArray();
        long expected = xs.Sum(x => x * 2);
        Assert.Equal(expected, f.Invoke(null, [xs]));
        Assert.Equal(expected, f.Invoke(null, [xs]));   // the cached accumulator is reset between launches
    }

    [Fact]
    public void Int32_and_float64_reductions_over_a_sub_range()
    {
        var t = Program(Cs("def isum(xs : Array(Int32), a : Int32, b : Int32) : Int32\n  t = 0\n  gpu for i in a...b\n    t += xs[i]\n  end\n  t\nend\n"
            + "def fsum(xs : Array(Float64)) : Float64\n  t = 0.0\n  gpu for i in 0...xs.size\n    t += xs[i]\n  end\n  t\nend\n"));
        var ints = Enumerable.Range(0, 50_000).Select(k => k % 17 - 8).ToArray();
        Assert.Equal(ints[100..40_000].Sum(), t.GetMethod("isum")!.Invoke(null, [ints, 100, 40_000]));
        var ds = Enumerable.Range(0, 70_001).Select(k => k * 0.25).ToArray();   // exact in binary: order does not matter
        Assert.Equal(ds.Sum(), t.GetMethod("fsum")!.Invoke(null, [ds]));
    }

    [Fact]
    public void Write_only_arrays_are_not_uploaded_and_read_only_ones_not_copied_back()
    {
        var src = "def saxpy(a : Float32, xs : Array(Float32), ys : Array(Float32), zs : Array(Float32))\n  gpu for i in 0...xs.size\n    zs[i] = a * xs[i] + ys[i]\n  end\nend\n";
        var cs = Cs(src);
        Assert.Contains("Allocate1D<float>(zs.Length)", cs);   // write-only: allocated, not uploaded
        Assert.DoesNotContain("CopyToCPU(__gb_xs", cs);
        Assert.DoesNotContain("CopyToCPU(__gb_ys", cs);
        var f = Program(cs).GetMethod("saxpy")!;
        float[] xs = [.. Enumerable.Range(0, 5000).Select(k => (float)k)], ys = [.. Enumerable.Repeat(1f, 5000)], o = new float[5000];
        f.Invoke(null, [2f, xs, ys, o]);
        Assert.Equal(1f, o[0]);
        Assert.Equal(9999f, o[4999]);
    }

    [Fact]
    public void A_partial_range_write_keeps_the_rest_of_the_array()
    {
        var f = Program(Cs("def fill(xs : Array(Int32), a : Int32, b : Int32)\n  gpu for i in a...b\n    xs[i] = i\n  end\nend\n")).GetMethod("fill")!;
        var xs = Enumerable.Repeat(-1, 100).ToArray();
        f.Invoke(null, [xs, 10, 20]);
        Assert.Equal(-1, xs[9]);
        Assert.Equal(10, xs[10]);
        Assert.Equal(19, xs[19]);
        Assert.Equal(-1, xs[20]);
    }

    [Fact]
    public void Regions_skip_the_upload_of_arrays_first_written_whole()
    {
        var src = "def run(xs : Array(Float32), ys : Array(Float32))\n  gpu\n    gpu for i in 0...ys.size\n      ys[i] = 3.0_f32\n    end\n    gpu for i in 0...ys.size\n      ys[i] = ys[i] + xs[i]\n    end\n  end\nend\n";
        var cs = Cs(src);
        Assert.Contains(".Get(ys, false, true)", cs);   // first use overwrites ys: no upload
        Assert.Contains(".Get(xs, true, false)", cs);   // read only: uploaded, never copied back
        var f = Program(cs).GetMethod("run")!;
        float[] xs = [.. Enumerable.Repeat(2f, 3000)], ys = [.. Enumerable.Repeat(9f, 3000)];
        f.Invoke(null, [xs, ys]);
        Assert.All(ys, y => Assert.Equal(5f, y));
        Assert.All(xs, x => Assert.Equal(2f, x));
    }
}
