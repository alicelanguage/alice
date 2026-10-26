using Alice.Runtime;

namespace Alice.Tests.Runtime.Tensors;

/// <summary>Steady-state tensor kernels must not allocate (ADR 0030).</summary>
public class TensorAllocationTests
{
    static long Allocated(Action f)
    {
        for (int i = 0; i < 5; i++) f();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) f();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Kernels_allocate_zero_bytes()
    {
        int n = 1024;
        var w = Reference.Random(n * n, 1);
        var x = Reference.Random(n, 2);
        var y = new float[n];
        var g = Reference.Random(n, 3);
        using var q8 = QuantizedMatrix.Quantize(w, n, n, QuantFormat.Int8Block);
        using var q4 = QuantizedMatrix.Quantize(w, n, n, QuantFormat.Int4Block);
        var a = Reference.Random(128 * 128, 4); var c = new float[128 * 128];
        using var arena = new TensorArena(1 << 20);
        using var t = Tensor<float>.FromSpan(w, n, n);
        using var tx = Tensor<float>.FromSpan(x, n);
        using var ty = Tensor<float>.Allocate(n);

        Assert.Equal(0, Allocated(() => Nn.Gemv(w, x, y, n, n)));
        Assert.Equal(0, Allocated(() => Nn.Gemv(q8, x, y)));
        Assert.Equal(0, Allocated(() => Nn.Gemv(q4, x, y)));
        Assert.Equal(0, Allocated(() => Nn.Gemv(t, tx, ty)));
        Assert.Equal(0, Allocated(() => Nn.MatMul(a, a, c, 128, 128, 128)));
        Assert.Equal(0, Allocated(() => Nn.MatMulTransposed(a, a, c, 128, 128, 128)));
        Assert.Equal(0, Allocated(() => { Nn.RmsNorm(x, g, y, 1e-5f); Nn.LayerNorm(x, g, g, y, 1e-5f); Nn.Softmax(y); Nn.Silu(y, y); Nn.Gelu(y, y); Nn.Dot(x, y); }));
        Assert.Equal(0, Allocated(() => { arena.Reset(); arena.Rent<float>(4096)[0] = 1; }));
    }
}
