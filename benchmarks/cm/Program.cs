// CM primitives micro-benchmark (ADR 0039). Median of 15 timed runs after warm-up; GB/s counts input bytes once
// (n * sizeof(T) / time), the convention of the P2.1 target (prefix_sum of 2^26 Int32 >= 8 GB/s on 32 cores).
// The "det" lines hash float results bit for bit: run once with DOTNET_PROCESSOR_COUNT=1 and once with the default,
// the hashes must match.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Alice.Runtime;

int log2 = args.Length > 0 ? int.Parse(args[0]) : 26;
int n = 1 << log2;
var rnd = new Random(1);
var xs = new int[n];
for (int i = 0; i < n; i++) xs[i] = rnd.Next(-1000, 1000);
var dst = new int[n];
var keys = new int[n];
for (int i = 0; i < n; i++) keys[i] = rnd.Next(65536);
var bins = new int[65536];

Console.WriteLine($"CM n=2^{log2}, {KernelPool.Threads} threads, AVX2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} AVX512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
Console.WriteLine("| kernel | ms | GB/s (input) |");
Console.WriteLine("|---|---:|---:|");
Run("prefix_sum Int32, serial loop", n * 4L, () => { int acc = 0; for (int i = 0; i < xs.Length; i++) dst[i] = acc += xs[i]; });
Run("prefix_sum Int32, Cm.InclusiveScan", n * 4L, () => Cm.InclusiveScan<int>(xs, dst));
Run("prefix_sum Int32, Cm.InclusiveScan in place", n * 4L, () => { Cm.InclusiveScan<int>(dst, dst); });
Run("prefix_sum Int32, Prelude xs.prefix_sum (allocates result)", n * 4L, () => GC.KeepAlive(xs.prefix_sum()));
var ls = new long[n / 2];
var ld = new long[n / 2];
Run($"prefix_sum Int64 2^{log2 - 1}, Cm.InclusiveScan", n * 4L, () => Cm.InclusiveScan<long>(ls, ld));
Run("histogram 64K bins, serial loop", n * 4L, () => { Array.Clear(bins); foreach (var k in keys) bins[k]++; });
Run("histogram 64K bins, Cm.Histogram", n * 4L, () => { Array.Clear(bins); Cm.Histogram<int>(keys, bins); });
int m = n / 4;
var sk = new uint[m];
var sv = new int[m];
var sk0 = new uint[m];
for (int i = 0; i < m; i++) sk0[i] = (uint)rnd.NextInt64();
Run($"sort_by_key 2^{log2 - 2} UInt32+Int32, Array.Sort(keys, items)", m * 8L, () => { sk0.CopyTo(sk, 0); Array.Sort(sk, sv); });
Run($"sort_by_key 2^{log2 - 2} UInt32+Int32, Cm.SortByKey", m * 8L, () => { sk0.CopyTo(sk, 0); Cm.SortByKey<int>(sk, sv); });
var fl = new bool[n];
for (int i = 0; i < n; i++) fl[i] = (xs[i] & 1) == 0;
Run("compact Int32 (half kept), serial loop", n * 4L, () => { int o = 0; for (int i = 0; i < n; i++) if (fl[i]) dst[o++] = xs[i]; });
Run("compact Int32 (half kept), Cm.Compact(flags)", n * 4L, () => Cm.Compact(xs, fl, dst));

// determinism hashes
int dn = 1 << 24;
var fd = new double[dn];
for (int i = 0; i < dn; i++) fd[i] = rnd.NextDouble() * 2 - 1;
var fo = new double[dn];
Cm.InclusiveScan<double>(fd, fo);
Console.WriteLine($"det scan_f64 {Hash(fo):x16}");
var ff = new float[dn];
for (int i = 0; i < dn; i++) ff[i] = (float)fd[i];
var ffo = new float[dn];
Cm.InclusiveScan<float>(ff, ffo);
Console.WriteLine($"det scan_f32 {Hash(ffo):x16}");
var tgt = new float[4096];
var idx = new int[dn];
for (int i = 0; i < dn; i++) idx[i] = rnd.Next(4096);
Cm.ScatterAdd<float>(tgt, idx, ff);
Console.WriteLine($"det scatter_add_f32 {Hash(tgt):x16}");
var flags = new bool[dn];
for (int i = 0; i < dn; i++) flags[i] = rnd.Next(10_000) == 0;
var segs = new double[Cm.SegmentCount(flags)];
Cm.SegmentedReduce<double>(fd, flags, segs);
Console.WriteLine($"det segmented_sum_f64 {Hash(segs):x16}");
Console.WriteLine($"det reduce_f64 {BitConverter.DoubleToInt64Bits(Cm.Reduce<double>(fd)):x16}");

static ulong Hash<T>(T[] a) where T : unmanaged
{
    ulong h = 14695981039346656037ul;
    foreach (byte b in MemoryMarshal.AsBytes(a.AsSpan())) h = (h ^ b) * 1099511628211ul;
    return h;
}

static void Run(string name, long bytes, Action f)
{
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 3 || sw.ElapsedMilliseconds < 500; i++) f();
    var t = new double[15];
    for (int r = 0; r < t.Length; r++)
    {
        long t0 = Stopwatch.GetTimestamp();
        f();
        t[r] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
    Array.Sort(t);
    double ms = t[t.Length / 2];
    Console.WriteLine($"| {name} | {ms:F2} | {bytes / ms / 1e6:F1} |");
}
