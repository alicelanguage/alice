namespace Alice.Tests.Runtime.Tensors;

/// <summary>Naive scalar references the optimized kernels are checked against.</summary>
static class Reference
{
    public static float[] Random(int n, int seed, float scale = 1f)
    {
        var r = new Random(seed);
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)(r.NextDouble() * 2 - 1) * scale;
        return a;
    }

    public static float[] Gemv(float[] w, float[] x, int rows, int cols)
    {
        var y = new float[rows];
        for (int r = 0; r < rows; r++)
        {
            double s = 0;
            for (int c = 0; c < cols; c++) s += (double)w[r * cols + c] * x[c];
            y[r] = (float)s;
        }
        return y;
    }

    public static float[] MatMul(float[] a, float[] b, int m, int k, int n)
    {
        var c = new float[m * n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int p = 0; p < k; p++) s += (double)a[i * k + p] * b[p * n + j];
                c[i * n + j] = (float)s;
            }
        return c;
    }

    public static void Close(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, float tol)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            float e = expected[i], a = actual[i];
            float err = MathF.Abs(e - a) / MathF.Max(1f, MathF.Abs(e));
            Assert.True(err <= tol, $"[{i}] expected {e}, got {a} (err {err})");
        }
    }
}
