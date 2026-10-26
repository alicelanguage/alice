using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Alice.Runtime;

/// <summary>Elementwise math, reductions, softmax, norms and activations. Vector256 where accelerated, then Vector128, scalar tail.</summary>
public static unsafe partial class Nn
{
    static void Same(int a, int b) { if (b < a) throw new ArgumentException("destination too short"); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void AxpyRaw(float alpha, float* x, float* y, int n)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var va = Vector256.Create(alpha);
            for (; i + 16 <= n; i += 16)
            {
                Vector256.FusedMultiplyAdd(va, Vector256.Load(x + i), Vector256.Load(y + i)).Store(y + i);
                Vector256.FusedMultiplyAdd(va, Vector256.Load(x + i + 8), Vector256.Load(y + i + 8)).Store(y + i + 8);
            }
            for (; i + 8 <= n; i += 8) Vector256.FusedMultiplyAdd(va, Vector256.Load(x + i), Vector256.Load(y + i)).Store(y + i);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var va = Vector128.Create(alpha);
            for (; i + 4 <= n; i += 4) (va * Vector128.Load(x + i) + Vector128.Load(y + i)).Store(y + i);
        }
        for (; i < n; i++) y[i] += alpha * x[i];
    }

    /// <summary>y += alpha * x</summary>
    public static void Axpy(float alpha, ReadOnlySpan<float> x, Span<float> y)
    {
        Same(x.Length, y.Length);
        fixed (float* px = x, py = y) AxpyRaw(alpha, px, py, x.Length);
    }

    public static void Fill(Span<float> y, float v) => y.Fill(v);

    public static void Add(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> y)
    {
        Same(a.Length, b.Length); Same(a.Length, y.Length);
        int i = 0;
        fixed (float* pa = a, pb = b, py = y)
        {
            if (Vector256.IsHardwareAccelerated) for (; i + 8 <= a.Length; i += 8) (Vector256.Load(pa + i) + Vector256.Load(pb + i)).Store(py + i);
            for (; i < a.Length; i++) py[i] = pa[i] + pb[i];
        }
    }

    public static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> y)
    {
        Same(a.Length, b.Length); Same(a.Length, y.Length);
        int i = 0;
        fixed (float* pa = a, pb = b, py = y)
        {
            if (Vector256.IsHardwareAccelerated) for (; i + 8 <= a.Length; i += 8) (Vector256.Load(pa + i) * Vector256.Load(pb + i)).Store(py + i);
            for (; i < a.Length; i++) py[i] = pa[i] * pb[i];
        }
    }

    public static void Scale(ReadOnlySpan<float> a, float s, Span<float> y)
    {
        Same(a.Length, y.Length);
        int i = 0;
        fixed (float* pa = a, py = y)
        {
            if (Vector256.IsHardwareAccelerated) for (var vs = Vector256.Create(s); i + 8 <= a.Length; i += 8) (Vector256.Load(pa + i) * vs).Store(py + i);
            for (; i < a.Length; i++) py[i] = pa[i] * s;
        }
    }

    public static float Sum(ReadOnlySpan<float> a)
    {
        int i = 0;
        float s = 0;
        fixed (float* p = a)
        {
            if (Vector256.IsHardwareAccelerated && a.Length >= 16)
            {
                Vector256<float> a0 = default, a1 = default;
                for (; i + 16 <= a.Length; i += 16) { a0 += Vector256.Load(p + i); a1 += Vector256.Load(p + i + 8); }
                s = Vector256.Sum(a0 + a1);
            }
            for (; i < a.Length; i++) s += p[i];
        }
        return s;
    }

    public static float Max(ReadOnlySpan<float> a)
    {
        if (a.IsEmpty) throw new ArgumentException("empty span");
        int i = 0;
        float m = float.NegativeInfinity;
        fixed (float* p = a)
        {
            if (Vector256.IsHardwareAccelerated && a.Length >= 8)
            {
                var vm = Vector256.Create(float.NegativeInfinity);
                for (; i + 8 <= a.Length; i += 8) vm = Vector256.Max(vm, Vector256.Load(p + i));
                for (int k = 0; k < 8; k++) m = MathF.Max(m, vm[k]);
            }
            for (; i < a.Length; i++) m = MathF.Max(m, p[i]);
        }
        return m;
    }

    // ---------- exp-based ----------

    /// <summary>Unary map with a shared exp polynomial: op 0 = exp(x + bias), 1 = silu, 2 = gelu (tanh form), 3 = tanh.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static float MapExp(float* x, float* y, int n, int op, float bias)
    {
        int i = 0;
        var acc256 = Vector256<float>.Zero;
        var acc128 = Vector128<float>.Zero;
        if (Vector256.IsHardwareAccelerated)
            for (; i + 8 <= n; i += 8)
            {
                var v = Vector256.Load(x + i);
                var r = op switch
                {
                    0 => FastMath.Exp(v + Vector256.Create(bias)),
                    1 => v / (Vector256<float>.One + FastMath.Exp(-v)),
                    2 => v * Vector256.Create(0.5f) * (Vector256<float>.One + Tanh256(Vector256.Create(0.7978845608f) * (v + Vector256.Create(0.044715f) * v * v * v))),
                    _ => Tanh256(v),
                };
                r.Store(y + i);
                acc256 += r;
            }
        else if (Vector128.IsHardwareAccelerated)
            for (; i + 4 <= n; i += 4)
            {
                var v = Vector128.Load(x + i);
                var r = op switch
                {
                    0 => FastMath.Exp(v + Vector128.Create(bias)),
                    1 => v / (Vector128<float>.One + FastMath.Exp(-v)),
                    2 => v * Vector128.Create(0.5f) * (Vector128<float>.One + Tanh128(Vector128.Create(0.7978845608f) * (v + Vector128.Create(0.044715f) * v * v * v))),
                    _ => Tanh128(v),
                };
                r.Store(y + i);
                acc128 += r;
            }
        float s = Vector256.Sum(acc256) + Vector128.Sum(acc128);
        for (; i < n; i++)
        {
            float v = x[i];
            float r = op switch
            {
                0 => FastMath.Exp(v + bias),
                1 => v / (1 + FastMath.Exp(-v)),
                2 => 0.5f * v * (1 + TanhScalar(0.7978845608f * (v + 0.044715f * v * v * v))),
                _ => TanhScalar(v),
            };
            y[i] = r;
            s += r;
        }
        return s;
    }

    // tanh(z) = 1 - 2 / (exp(2z) + 1); exp clamps, so large |z| saturates to +-1
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<float> Tanh256(Vector256<float> z) => Vector256<float>.One - Vector256.Create(2f) / (FastMath.Exp(z + z) + Vector256<float>.One);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<float> Tanh128(Vector128<float> z) => Vector128<float>.One - Vector128.Create(2f) / (FastMath.Exp(z + z) + Vector128<float>.One);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float TanhScalar(float z) => 1 - 2 / (FastMath.Exp(z + z) + 1);

    static float Map(ReadOnlySpan<float> x, Span<float> y, int op, float bias = 0)
    {
        Same(x.Length, y.Length);
        fixed (float* px = x, py = y) return MapExp(px, py, x.Length, op, bias);
    }

    public static void Exp(ReadOnlySpan<float> x, Span<float> y) => Map(x, y, 0);
    public static void Silu(ReadOnlySpan<float> x, Span<float> y) => Map(x, y, 1);
    public static void Gelu(ReadOnlySpan<float> x, Span<float> y) => Map(x, y, 2);
    public static void Tanh(ReadOnlySpan<float> x, Span<float> y) => Map(x, y, 3);

    public static void Relu(ReadOnlySpan<float> x, Span<float> y)
    {
        Same(x.Length, y.Length);
        int i = 0;
        fixed (float* px = x, py = y)
        {
            if (Vector256.IsHardwareAccelerated) for (; i + 8 <= x.Length; i += 8) Vector256.Max(Vector256.Load(px + i), Vector256<float>.Zero).Store(py + i);
            for (; i < x.Length; i++) py[i] = MathF.Max(0, px[i]);
        }
    }

    /// <summary>In-place numerically stable softmax.</summary>
    public static void Softmax(Span<float> x)
    {
        if (x.IsEmpty) return;
        float max = Max(x);
        fixed (float* p = x)
        {
            float sum = MapExp(p, p, x.Length, 0, -max);
            float inv = 1f / sum;
            int i = 0;
            if (Vector256.IsHardwareAccelerated) for (var vi = Vector256.Create(inv); i + 8 <= x.Length; i += 8) (Vector256.Load(p + i) * vi).Store(p + i);
            for (; i < x.Length; i++) p[i] *= inv;
        }
    }

    /// <summary>y = x / sqrt(mean(x^2) + eps) * weight</summary>
    public static void RmsNorm(ReadOnlySpan<float> x, ReadOnlySpan<float> weight, Span<float> y, float eps)
    {
        Same(x.Length, weight.Length); Same(x.Length, y.Length);
        float ss = Dot(x, x) / x.Length;
        float inv = 1f / MathF.Sqrt(ss + eps);
        fixed (float* px = x, pw = weight, py = y)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
                for (var vi = Vector256.Create(inv); i + 8 <= x.Length; i += 8) (Vector256.Load(px + i) * vi * Vector256.Load(pw + i)).Store(py + i);
            for (; i < x.Length; i++) py[i] = px[i] * inv * pw[i];
        }
    }

    /// <summary>y = (x - mean) / sqrt(var + eps) * gamma + beta (two-pass variance).</summary>
    public static void LayerNorm(ReadOnlySpan<float> x, ReadOnlySpan<float> gamma, ReadOnlySpan<float> beta, Span<float> y, float eps)
    {
        Same(x.Length, gamma.Length); Same(x.Length, beta.Length); Same(x.Length, y.Length);
        int n = x.Length;
        float mean = Sum(x) / n;
        fixed (float* px = x, pg = gamma, pb = beta, py = y)
        {
            float var = 0;
            int i = 0;
            if (Vector256.IsHardwareAccelerated && n >= 8)
            {
                var vm = Vector256.Create(mean);
                var acc = Vector256<float>.Zero;
                for (; i + 8 <= n; i += 8) { var d = Vector256.Load(px + i) - vm; acc = Vector256.FusedMultiplyAdd(d, d, acc); }
                var = Vector256.Sum(acc);
            }
            for (; i < n; i++) { float d = px[i] - mean; var += d * d; }
            float inv = 1f / MathF.Sqrt(var / n + eps);
            i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                var vm = Vector256.Create(mean); var vi = Vector256.Create(inv);
                for (; i + 8 <= n; i += 8)
                    Vector256.FusedMultiplyAdd((Vector256.Load(px + i) - vm) * vi, Vector256.Load(pg + i), Vector256.Load(pb + i)).Store(py + i);
            }
            for (; i < n; i++) py[i] = (px[i] - mean) * inv * pg[i] + pb[i];
        }
    }
}
