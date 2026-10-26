// GEMV micro-benchmark (ADR 0030): y = W x, W n x n (default 4096), f32 / bf16 / int8-block / int4-block.
// Prints median ms per GEMV, effective weight bandwidth and GEMVs/s. A decode step of a dense network is a
// sequence of GEMVs, so GEMV/s divided by GEMVs per step is a steps/s ("tokens/s") ceiling.
using System.Diagnostics;
using Alice.Runtime;

int n = args.Length > 0 ? int.Parse(args[0]) : 4096;
var rnd = new Random(1);
var w = new float[n * n];
for (int i = 0; i < w.Length; i++) w[i] = (float)(rnd.NextDouble() * 2 - 1);
var x = new float[n];
for (int i = 0; i < n; i++) x[i] = (float)(rnd.NextDouble() * 2 - 1);
var y = new float[n];
var bf = new BFloat16[w.Length];
BFloat16.Convert(w, bf);
using var q8 = QuantizedMatrix.Quantize(w, n, n, QuantFormat.Int8Block);
using var q4 = QuantizedMatrix.Quantize(w, n, n, QuantFormat.Int4Block);

Console.WriteLine($"GEMV {n}x{n}, {KernelPool.Threads} threads, AVX2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} AVX512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
Console.WriteLine("| kernel | ms/GEMV | GB/s weights | GEMV/s |");
Console.WriteLine("|---|---:|---:|---:|");
Run("naive f32 (scalar loop, 1 thread)", (long)n * n * 4, () =>
{
    for (int r = 0; r < n; r++)
    {
        float s = 0;
        for (int c = 0; c < n; c++) s += w[r * n + c] * x[c];
        y[r] = s;
    }
});
Run("Nn.Gemv f32", (long)n * n * 4, () => Nn.Gemv(w, x, y, n, n));
Run("Nn.Gemv bf16", (long)n * n * 2, () => Nn.Gemv(bf, x, y, n, n));
Run("Nn.Gemv int8 block", q8.Bytes.Length, () => Nn.Gemv(q8, x, y));
Run("Nn.Gemv int4 block", q4.Bytes.Length, () => Nn.Gemv(q4, x, y));

void Run(string name, long bytes, Action f)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 1000) f();
    var t = new double[15];
    for (int i = 0; i < t.Length; i++)
    {
        long s = Stopwatch.GetTimestamp();
        for (int k = 0; k < 4; k++) f();
        t[i] = Stopwatch.GetElapsedTime(s).TotalMilliseconds / 4;
    }
    Array.Sort(t);
    double ms = t[t.Length / 2];
    Console.WriteLine($"| {name} | {ms:F3} | {bytes / ms / 1e6:F1} | {1000 / ms:F0} |");
}
