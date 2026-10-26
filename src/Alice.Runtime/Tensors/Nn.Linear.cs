using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Alice.Runtime;

/// <summary>
/// Tensor kernels over spans and <see cref="Tensor{T}"/> (ADR 0030): dot, GEMV (f32, Half, BFloat16, quantized),
/// blocked parallel GEMM, elementwise math, reductions, softmax, norms, activations. 0 allocations; large shapes run
/// on <see cref="KernelPool"/>. Matrices are row-major.
/// </summary>
public static unsafe partial class Nn
{
    /// <summary>Elements per parallel chunk below which a kernel stays on the calling thread.</summary>
    const int ParallelGrainElems = 1 << 15;

    static int RowGrain(int cols) => Math.Max(1, ParallelGrainElems / Math.Max(1, cols));

    static void Need(int have, long need, string name)
    {
        if (have < need) throw new ArgumentException($"{name} too short: {have} < {need}");
    }

    // ---------- dot ----------

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("length mismatch");
        fixed (float* pa = a, pb = b) return Dot(pa, pb, a.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Dot(float* a, float* b, int n)
    {
        int i = 0;
        float s = 0;
        if (Vector512.IsHardwareAccelerated && n >= 32)
        {
            Vector512<float> a0 = default, a1 = default;
            for (; i + 32 <= n; i += 32)
            {
                a0 = Vector512.FusedMultiplyAdd(Vector512.Load(a + i), Vector512.Load(b + i), a0);
                a1 = Vector512.FusedMultiplyAdd(Vector512.Load(a + i + 16), Vector512.Load(b + i + 16), a1);
            }
            s = Vector512.Sum(a0 + a1);
        }
        else if (Vector256.IsHardwareAccelerated && n >= 32)
        {
            Vector256<float> a0 = default, a1 = default, a2 = default, a3 = default;
            for (; i + 32 <= n; i += 32)
            {
                a0 = Vector256.FusedMultiplyAdd(Vector256.Load(a + i), Vector256.Load(b + i), a0);
                a1 = Vector256.FusedMultiplyAdd(Vector256.Load(a + i + 8), Vector256.Load(b + i + 8), a1);
                a2 = Vector256.FusedMultiplyAdd(Vector256.Load(a + i + 16), Vector256.Load(b + i + 16), a2);
                a3 = Vector256.FusedMultiplyAdd(Vector256.Load(a + i + 24), Vector256.Load(b + i + 24), a3);
            }
            s = Vector256.Sum((a0 + a1) + (a2 + a3));
        }
        else if (Vector128.IsHardwareAccelerated && n >= 16)
        {
            Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default;
            for (; i + 16 <= n; i += 16)
            {
                a0 += Vector128.Load(a + i) * Vector128.Load(b + i);
                a1 += Vector128.Load(a + i + 4) * Vector128.Load(b + i + 4);
                a2 += Vector128.Load(a + i + 8) * Vector128.Load(b + i + 8);
                a3 += Vector128.Load(a + i + 12) * Vector128.Load(b + i + 12);
            }
            s = Vector128.Sum((a0 + a1) + (a2 + a3));
        }
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static float DotBf16(ushort* w, float* x, int n)
    {
        int i = 0;
        float s = 0;
        if (Vector256.IsHardwareAccelerated && n >= 16)
        {
            Vector256<float> a0 = default, a1 = default;
            for (; i + 16 <= n; i += 16)
            {
                var (lo, hi) = Vector256.Widen(Vector256.Load(w + i));
                a0 = Vector256.FusedMultiplyAdd(Vector256.ShiftLeft(lo, 16).AsSingle(), Vector256.Load(x + i), a0);
                a1 = Vector256.FusedMultiplyAdd(Vector256.ShiftLeft(hi, 16).AsSingle(), Vector256.Load(x + i + 8), a1);
            }
            s = Vector256.Sum(a0 + a1);
        }
        for (; i < n; i++) s += BitConverter.UInt32BitsToSingle((uint)w[i] << 16) * x[i];
        return s;
    }

    static float DotHalf(Half* w, float* x, int n)
    {
        float s0 = 0, s1 = 0;
        int i = 0;
        for (; i + 2 <= n; i += 2) { s0 += (float)w[i] * x[i]; s1 += (float)w[i + 1] * x[i + 1]; }
        if (i < n) s0 += (float)w[i] * x[i];
        return s0 + s1;
    }

    // ---------- GEMV: y[rows] = W[rows, cols] * x[cols] ----------

    struct GemvCtx { public void* W; public float* X; public float* Y; public int Cols; public long RowBytes; public int Kind; }

    const int KF32 = 0, KHalf = 1, KBf16 = 2, KInt8 = 3, KInt4 = 4;

    static void GemvChunk(void* c, int lo, int hi)
    {
        var g = (GemvCtx*)c;
        byte* w = (byte*)g->W;
        for (int r = lo; r < hi; r++)
        {
            byte* row = w + r * g->RowBytes;
            g->Y[r] = g->Kind switch
            {
                KF32 => Dot((float*)row, g->X, g->Cols),
                KHalf => DotHalf((Half*)row, g->X, g->Cols),
                KBf16 => DotBf16((ushort*)row, g->X, g->Cols),
                KInt8 => QuantKernels.DotInt8(row, g->X, g->Cols / QuantizedMatrix.BlockSize),
                _ => QuantKernels.DotInt4(row, g->X, g->Cols / QuantizedMatrix.BlockSize),
            };
        }
    }

    static void GemvRun(void* w, float* x, float* y, int rows, int cols, long rowBytes, int kind)
    {
        var ctx = new GemvCtx { W = w, X = x, Y = y, Cols = cols, RowBytes = rowBytes, Kind = kind };
        KernelPool.Run(rows, RowGrain(cols), &GemvChunk, &ctx);
    }

    public static void Gemv(ReadOnlySpan<float> w, ReadOnlySpan<float> x, Span<float> y, int rows, int cols)
    {
        Need(w.Length, (long)rows * cols, "w"); Need(x.Length, cols, "x"); Need(y.Length, rows, "y");
        fixed (float* pw = w, px = x, py = y) GemvRun(pw, px, py, rows, cols, (long)cols * 4, KF32);
    }

    public static void Gemv(ReadOnlySpan<Half> w, ReadOnlySpan<float> x, Span<float> y, int rows, int cols)
    {
        Need(w.Length, (long)rows * cols, "w"); Need(x.Length, cols, "x"); Need(y.Length, rows, "y");
        fixed (Half* pw = w) fixed (float* px = x, py = y) GemvRun(pw, px, py, rows, cols, (long)cols * 2, KHalf);
    }

    public static void Gemv(ReadOnlySpan<BFloat16> w, ReadOnlySpan<float> x, Span<float> y, int rows, int cols)
    {
        Need(w.Length, (long)rows * cols, "w"); Need(x.Length, cols, "x"); Need(y.Length, rows, "y");
        fixed (BFloat16* pw = w) fixed (float* px = x, py = y) GemvRun(pw, px, py, rows, cols, (long)cols * 2, KBf16);
    }

    /// <summary>Fused dequantize GEMV: weights stay quantized in memory, scales applied per block.</summary>
    public static void Gemv(QuantizedMatrix w, ReadOnlySpan<float> x, Span<float> y)
    {
        Need(x.Length, w.Columns, "x"); Need(y.Length, w.Rows, "y");
        fixed (float* px = x, py = y)
            GemvRun(w.Pointer, px, py, w.Rows, w.Columns, w.RowBytes, w.Format == QuantFormat.Int8Block ? KInt8 : KInt4);
        GC.KeepAlive(w);
    }

    public static void Gemv(Tensor<float> w, Tensor<float> x, Tensor<float> y)
    {
        if (w.Rank != 2 || !w.IsContiguous) throw new ArgumentException("w must be a contiguous rank-2 tensor");
        int rows = w.Shape[0], cols = w.Shape[1];
        Need((int)Math.Min(int.MaxValue, x.Length), cols, "x"); Need((int)Math.Min(int.MaxValue, y.Length), rows, "y");
        if (!x.IsContiguous || !y.IsContiguous) throw new ArgumentException("x and y must be contiguous");
        GemvRun(w.Pointer, x.Pointer, y.Pointer, rows, cols, (long)cols * 4, KF32);
        GC.KeepAlive(w); GC.KeepAlive(x); GC.KeepAlive(y);
    }

    // ---------- GEMM: C[m, n] = A[m, k] * B[k, n] ----------

    const int TileK = 256, TileN = 512, RowsPerTask = 4;

    struct GemmCtx { public float* A, B, C; public int M, K, N; }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void GemmChunk(void* p, int lo, int hi)
    {
        var g = (GemmCtx*)p;
        int k = g->K, n = g->N;
        int i0 = lo * RowsPerTask, i1 = Math.Min(g->M, hi * RowsPerTask);
        for (int i = i0; i < i1; i++) new Span<float>(g->C + (long)i * n, n).Clear();
        for (int jj = 0; jj < n; jj += TileN)
        {
            int jn = Math.Min(TileN, n - jj);
            for (int kk = 0; kk < k; kk += TileK)
            {
                int kn = Math.Min(TileK, k - kk);
                for (int i = i0; i < i1; i++)
                {
                    float* c = g->C + (long)i * n + jj;
                    float* a = g->A + (long)i * k + kk;
                    for (int q = 0; q < kn; q++) AxpyRaw(a[q], g->B + (long)(kk + q) * n + jj, c, jn);
                }
            }
        }
    }

    public static void MatMul(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c, int m, int k, int n)
    {
        Need(a.Length, (long)m * k, "a"); Need(b.Length, (long)k * n, "b"); Need(c.Length, (long)m * n, "c");
        fixed (float* pa = a, pb = b, pc = c) MatMulRaw(pa, pb, pc, m, k, n);
    }

    static void MatMulRaw(float* a, float* b, float* c, int m, int k, int n)
    {
        var ctx = new GemmCtx { A = a, B = b, C = c, M = m, K = k, N = n };
        int tasks = (m + RowsPerTask - 1) / RowsPerTask;
        long work = (long)RowsPerTask * k * n;
        KernelPool.Run(tasks, (int)Math.Max(1, (1L << 17) / Math.Max(1, work)), &GemmChunk, &ctx);
    }

    public static void MatMul(Tensor<float> a, Tensor<float> b, Tensor<float> c)
    {
        if (a.Rank != 2 || b.Rank != 2 || c.Rank != 2 || !a.IsContiguous || !b.IsContiguous || !c.IsContiguous)
            throw new ArgumentException("MatMul needs contiguous rank-2 tensors");
        int m = a.Shape[0], k = a.Shape[1], n = b.Shape[1];
        if (b.Shape[0] != k || c.Shape[0] != m || c.Shape[1] != n) throw new ArgumentException("shape mismatch");
        MatMulRaw(a.Pointer, b.Pointer, c.Pointer, m, k, n);
        GC.KeepAlive(a); GC.KeepAlive(b); GC.KeepAlive(c);
    }

    // ---------- C[m, n] = A[m, k] * B[n, k]^T (both operands read along rows) ----------

    struct GemmTCtx { public float* A, B, C; public int M, K, N; }

    static void GemmTChunk(void* p, int lo, int hi)
    {
        var g = (GemmTCtx*)p;
        for (int j = lo; j < hi; j++)
        {
            float* bj = g->B + (long)j * g->K;
            for (int i = 0; i < g->M; i++) g->C[(long)i * g->N + j] = Dot(g->A + (long)i * g->K, bj, g->K);
        }
    }

    public static void MatMulTransposed(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c, int m, int k, int n)
    {
        Need(a.Length, (long)m * k, "a"); Need(b.Length, (long)n * k, "b"); Need(c.Length, (long)m * n, "c");
        fixed (float* pa = a, pb = b, pc = c)
        {
            var ctx = new GemmTCtx { A = pa, B = pb, C = pc, M = m, K = k, N = n };
            KernelPool.Run(n, (int)Math.Max(1, ParallelGrainElems / Math.Max(1L, (long)m * k)), &GemmTChunk, &ctx);
        }
    }
}
