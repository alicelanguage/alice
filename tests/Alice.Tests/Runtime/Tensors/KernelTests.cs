using Alice.Runtime;

namespace Alice.Tests.Runtime.Tensors;

public class KernelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(1000)]
    [InlineData(4099)]
    public void Dot_sum_max_match_reference(int n)
    {
        var a = Reference.Random(n, 1);
        var b = Reference.Random(n, 2);
        double d = 0; for (int i = 0; i < n; i++) d += (double)a[i] * b[i];
        Assert.Equal(d, Nn.Dot(a, b), 1e-3);
        Assert.Equal(a.Sum(v => (double)v), Nn.Sum(a), 1e-3);
        Assert.Equal(a.Max(), Nn.Max(a));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 13)]
    [InlineData(513, 1000)]
    [InlineData(1024, 4096)]
    public void Gemv_f32_matches_reference(int rows, int cols)
    {
        var w = Reference.Random(rows * cols, 1);
        var x = Reference.Random(cols, 2);
        var y = new float[rows];
        Nn.Gemv(w, x, y, rows, cols);
        Reference.Close(Reference.Gemv(w, x, rows, cols), y, 1e-4f);
    }

    [Fact]
    public void Gemv_half_and_bf16_weights()
    {
        int rows = 70, cols = 300;
        var w = Reference.Random(rows * cols, 8);
        var x = Reference.Random(cols, 9);
        var h = new Half[w.Length]; HalfConvert.Convert(w, h);
        var b = new BFloat16[w.Length]; BFloat16.Convert(w, b);
        var wh = new float[w.Length]; HalfConvert.Convert(h, wh);
        var wb = new float[w.Length]; BFloat16.Convert(b, wb);
        var y = new float[rows];
        Nn.Gemv(h, x, y, rows, cols);
        Reference.Close(Reference.Gemv(wh, x, rows, cols), y, 1e-4f);
        Nn.Gemv(b, x, y, rows, cols);
        Reference.Close(Reference.Gemv(wb, x, rows, cols), y, 1e-4f);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(37, 129, 71)]
    [InlineData(200, 300, 260)]
    public void MatMul_blocked_parallel_matches_reference(int m, int k, int n)
    {
        var a = Reference.Random(m * k, 3);
        var b = Reference.Random(k * n, 4);
        var c = new float[m * n];
        c.AsSpan().Fill(123f); // overwritten, not accumulated
        Nn.MatMul(a, b, c, m, k, n);
        Reference.Close(Reference.MatMul(a, b, m, k, n), c, 1e-4f);
    }

    [Fact]
    public void MatMulTransposed_is_a_times_b_transposed()
    {
        int m = 5, k = 96, n = 33;
        var a = Reference.Random(m * k, 10);
        var b = Reference.Random(n * k, 11);
        var c = new float[m * n];
        Nn.MatMulTransposed(a, b, c, m, k, n);
        var bt = new float[k * n];
        for (int i = 0; i < n; i++) for (int j = 0; j < k; j++) bt[j * n + i] = b[i * k + j];
        Reference.Close(Reference.MatMul(a, bt, m, k, n), c, 1e-4f);
    }

    [Fact]
    public void Elementwise_ops()
    {
        var a = Reference.Random(37, 1); var b = Reference.Random(37, 2); var y = new float[37];
        Nn.Add(a, b, y); Reference.Close(a.Select((v, i) => v + b[i]).ToArray(), y, 0);
        Nn.Multiply(a, b, y); Reference.Close(a.Select((v, i) => v * b[i]).ToArray(), y, 0);
        Nn.Scale(a, 3f, y); Reference.Close(a.Select(v => v * 3f).ToArray(), y, 0);
        var acc = (float[])b.Clone();
        Nn.Axpy(2f, a, acc); Reference.Close(a.Select((v, i) => 2f * v + b[i]).ToArray(), acc, 1e-6f);
        Nn.Fill(y, 4f); Assert.All(y, v => Assert.Equal(4f, v));
    }

    [Fact]
    public void Fast_exp_relative_error()
    {
        var x = new float[4001];
        for (int i = 0; i < x.Length; i++) x[i] = -80f + i * 0.04f;
        var y = new float[x.Length];
        Nn.Exp(x, y);
        for (int i = 0; i < x.Length; i++)
        {
            float e = MathF.Exp(x[i]);
            Assert.True(MathF.Abs(y[i] - e) / e < 2e-6f, $"exp({x[i]}) = {y[i]} vs {e}");
            Assert.True(MathF.Abs(FastMath.Exp(x[i]) - e) / e < 2e-6f);
        }
    }

    [Fact]
    public void Softmax_matches_reference()
    {
        var x = Reference.Random(1003, 12, 20);
        var expected = new float[x.Length];
        float max = x.Max(); double sum = 0;
        for (int i = 0; i < x.Length; i++) sum += Math.Exp(x[i] - max);
        for (int i = 0; i < x.Length; i++) expected[i] = (float)(Math.Exp(x[i] - max) / sum);
        Nn.Softmax(x);
        Reference.Close(expected, x, 1e-5f);
        Assert.Equal(1.0, x.Sum(v => (double)v), 1e-4);
    }

    [Fact]
    public void Norms_match_reference()
    {
        int n = 515;
        var x = Reference.Random(n, 13, 3);
        var g = Reference.Random(n, 14);
        var b = Reference.Random(n, 15);
        var y = new float[n];

        double ss = x.Sum(v => (double)v * v) / n;
        float inv = (float)(1 / Math.Sqrt(ss + 1e-5));
        Nn.RmsNorm(x, g, y, 1e-5f);
        Reference.Close(x.Select((v, i) => v * inv * g[i]).ToArray(), y, 1e-5f);

        double mean = x.Average(v => (double)v);
        double var = x.Sum(v => (v - mean) * (v - mean)) / n;
        float il = (float)(1 / Math.Sqrt(var + 1e-5));
        Nn.LayerNorm(x, g, b, y, 1e-5f);
        Reference.Close(x.Select((v, i) => (float)((v - mean) * il) * g[i] + b[i]).ToArray(), y, 1e-5f);
    }

    [Fact]
    public void Activations_match_reference()
    {
        var x = Reference.Random(259, 16, 8);
        var y = new float[x.Length];
        Nn.Silu(x, y);
        Reference.Close(x.Select(v => v / (1 + MathF.Exp(-v))).ToArray(), y, 1e-5f);
        Nn.Gelu(x, y);
        Reference.Close(x.Select(v => 0.5f * v * (1 + MathF.Tanh(0.7978845608f * (v + 0.044715f * v * v * v)))).ToArray(), y, 1e-5f);
        Nn.Relu(x, y);
        Reference.Close(x.Select(v => MathF.Max(0, v)).ToArray(), y, 0);
        Nn.Tanh(x, y);
        Reference.Close(x.Select(MathF.Tanh).ToArray(), y, 1e-5f);
    }

    [Fact]
    public void Tensor_overloads_route_to_kernels()
    {
        using var w = Tensor<float>.FromSpan(Reference.Random(6 * 8, 1), 6, 8);
        using var x = Tensor<float>.FromSpan(Reference.Random(8, 2), 8);
        using var y = Tensor<float>.Allocate(6);
        Nn.Gemv(w, x, y);
        Reference.Close(Reference.Gemv(w.Span.ToArray(), x.Span.ToArray(), 6, 8), y.Span, 1e-5f);
        using var c = Tensor<float>.Allocate(6, 6);
        Nn.MatMul(w, w.Transpose().Contiguous(), c);
        Assert.Equal(Nn.Dot(w.Row(1), w.Row(2)), c[1, 2], 1e-5f);
    }
}
