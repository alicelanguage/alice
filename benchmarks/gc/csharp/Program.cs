// Idiomatic C# versions of benchmarks/gc/alice/*.al: same algorithms, same allocations per iteration.
// Prints: GCSTAT <name> <median_ms> <bytes_per_run> <gen0> <gen1> <gen2> <pause_ms> <peak_bytes> <checksum>
using System.Diagnostics;
using System.Globalization;

var name = args[0];
(Func<long> kernel, Action warmup) = name switch
{
    "rows" => ((Func<long>)Rows.Kernel, (Action)(() => Rows.Dist(Rows.Seq(64, 1), Rows.Seq(64, 2)))),
    "scratch" => (Scratch.Kernel, () => Scratch.WindowScore(Scratch.Source(1024), 0, 512)),
    "frames" => (() => Frames.Step(1 << 20, 120), () => Frames.Step(1024, 4)),
    "trees" => (Trees.Kernel, () => Trees.Build(8).Check()),
    _ => throw new ArgumentException(name),
};

long check = kernel();
var warm = Stopwatch.StartNew();
for (int wn = 0; wn < 30 || warm.Elapsed.TotalMilliseconds < 300; wn++) warmup();
GC.Collect();
int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
long alloc = GC.GetTotalAllocatedBytes(true);
double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
var times = new double[7];
for (int r = 0; r < times.Length; r++)
{
    var sw = Stopwatch.StartNew();
    if (kernel() != check) throw new Exception("checksum mismatch");
    times[r] = sw.Elapsed.TotalMilliseconds;
}
alloc = GC.GetTotalAllocatedBytes(true) - alloc;
pause = GC.GetTotalPauseDuration().TotalMilliseconds - pause;
g0 = GC.CollectionCount(0) - g0; g1 = GC.CollectionCount(1) - g1; g2 = GC.CollectionCount(2) - g2;
Array.Sort(times);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"GCSTAT {name} {times[3]} {alloc / 7} {g0} {g1} {g2} {pause} {Process.GetCurrentProcess().PeakWorkingSet64} {check}"));

static class Rows
{
    public static int[] Seq(int n, int seed)
    {
        var xs = new int[n];
        int s = seed;
        for (int i = 0; i < n; i++) { s = (s * 1103 + 12345) & 0xFFFF; xs[i] = s & 3; }
        return xs;
    }

    public static int Dist(int[] a, int[] b)
    {
        int m = b.Length;
        var previous = new int[m + 1];
        for (int j = 0; j <= m; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            var row = new int[m + 1];
            row[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                row[j] = Math.Min(Math.Min(row[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            previous = row;
        }
        return previous[m];
    }

    public static long Kernel()
    {
        long total = 0;
        for (int k = 0; k < 4; k++) total += Dist(Seq(2000, k + 1), Seq(2000, k + 7));
        return total;
    }
}

static class Scratch
{
    public static int WindowScore(int[] src, int start, int w)
    {
        var buf = new int[w];
        for (int k = 0; k < w; k++) buf[k] = (src[start + k] * 7 + k) & 1023;
        int score = 0;
        for (int k = 1; k < w; k++) score += Math.Abs(buf[k] - buf[k - 1]);
        return score;
    }

    public static int[] Source(int n)
    {
        var xs = new int[n];
        int s = 7;
        for (int i = 0; i < n; i++) { s = (s * 1103 + 12345) & 0xFFFF; xs[i] = s; }
        return xs;
    }

    public static long Kernel()
    {
        var src = Source(1_000_000);
        long total = 0;
        for (int s = 0; s < 300_000; s++) total += WindowScore(src, s * 3, 512);
        return total;
    }
}

static class Frames
{
    public static long Step(int n, int frames)
    {
        var cur = new int[n];
        for (int i = 0; i < n; i++) cur[i] = i & 0xFFFF;
        for (int f = 0; f < frames; f++)
        {
            var nxt = new int[n];
            for (int i = 1; i < n - 1; i++) nxt[i] = (cur[i - 1] + cur[i] + cur[i + 1] + f) & 0xFFFF;
            cur = nxt;
        }
        long total = 0;
        foreach (var v in cur) total += v;
        return total;
    }
}

sealed class Trees(Trees? left, Trees? right)
{
    public int Check() => left is null || right is null ? 1 : 1 + left.Check() + right.Check();

    public static Trees Build(int depth) => depth > 0 ? new Trees(Build(depth - 1), Build(depth - 1)) : new Trees(null, null);

    public static long Kernel()
    {
        long total = 0;
        var longLived = Build(16);
        for (int i = 0; i < 256; i++) total += Build(12).Check();
        return total + longLived.Check();
    }
}
