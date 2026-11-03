// Dataflow acceptance benchmark (ADR 0038, task P3.1): generate lines -> tokenize -> count over ~1 GiB of
// synthetic text, Alice.Runtime dataflow graph vs Task.Run + System.Threading.Channels.
//
//   dotnet run -c Release -- [GiB=1] [variants=serial,dataflow,dataflow-par,channels-naive,channels-tuned]
//
// Items: one Line (start, length) per text line on the first edge, one uint token hash per word on the second.
// Every variant must produce the same token count and checksum.
using System.Diagnostics;
using System.Threading.Channels;
using Alice.Runtime;

double gib = args.Length > 0 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
var variants = (args.Length > 1 ? args[1] : "serial,dataflow,dataflow-par,channels-naive,channels-tuned").Split(',');

var text = Text.Generate(64 << 20, out var lines);
long target = (long)(gib * (1L << 30));
int passes = (int)Math.Max(1, (target + text.Length - 1) / text.Length);
long totalLines = (long)passes * lines.Length, totalBytes = (long)passes * text.Length;
Console.WriteLine($"text: {text.Length >> 20} MiB x {passes} passes = {totalBytes / (double)(1L << 30):F2} GiB, {totalLines / 1e6:F1} M lines, " +
                  $"{Environment.ProcessorCount} cores, .NET {Environment.Version}");

Result? reference = null;
foreach (var v in variants)
{
    Func<int, Result> run = v switch
    {
        "serial" => n => Serial.Run(text, lines, n),
        "dataflow" => n => Flow.Run(text, lines, n, replicate: 0),
        "dataflow-par" => n => Flow.Run(text, lines, n, replicate: Math.Max(2, Environment.ProcessorCount / 2)),
        "channels-naive" => n => Chans.RunNaive(text, lines, n),
        "channels-tuned" => n => Chans.RunTuned(text, lines, n),
        _ => throw new ArgumentException($"unknown variant {v}"),
    };
    run(1);   // warm-up (JIT, tiering): one pass over the text
    GC.Collect();
    long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
    var sw = Stopwatch.StartNew();
    var r = run(passes);
    sw.Stop();
    long alloc = GC.GetTotalAllocatedBytes(precise: true) - alloc0;
    double s = sw.Elapsed.TotalSeconds;
    reference ??= r;
    string ok = r == reference ? "ok" : "MISMATCH";
    Console.WriteLine($"{v,-15} {s,7:F3} s  {r.Tokens / s / 1e6,7:F1} M tokens/s  {(r.Tokens + totalLines) / s / 1e6,7:F1} M items/s  " +
                      $"{totalBytes / s / 1e9,5:F2} GB/s  alloc {alloc / 1024.0,10:F1} KiB  tokens {r.Tokens} sum {r.Sum:x16} {ok}");
}

readonly record struct Line(int Start, int Length);

readonly record struct Result(long Tokens, ulong Sum);

static class Text
{
    /// <summary>Lowercase words of 1-10 letters, 4-16 words per line, deterministic.</summary>
    public static byte[] Generate(int size, out Line[] lines)
    {
        var buf = new byte[size];
        var idx = new List<Line>(size / 40);
        ulong x = 0x9E3779B97F4A7C15;
        ulong Next() { x ^= x << 13; x ^= x >> 7; x ^= x << 17; return x; }
        int p = 0;
        while (true)
        {
            int start = p, words = 4 + (int)(Next() % 13);
            if (p + words * 11 + 1 > size) break;
            for (int w = 0; w < words; w++)
            {
                if (w > 0) buf[p++] = (byte)' ';
                int len = 1 + (int)(Next() % 10);
                for (int i = 0; i < len; i++) buf[p++] = (byte)('a' + (int)(Next() % 26));
            }
            idx.Add(new Line(start, p - start));
            buf[p++] = (byte)'\n';
        }
        Array.Resize(ref buf, p);
        lines = idx.ToArray();
        return buf;
    }
}

/// <summary>The shared kernels: FNV-1a per word, and the count step.</summary>
static class Kernel
{
    public const uint Basis = 2166136261, Prime = 16777619;
    public const int Bins = 4096;

    public static void Count(uint h, ref Counts c)
    {
        c.bins[h & (Bins - 1)]++;
        c.sum += h;
        c.tokens++;
    }

    public static Result Finish(in Counts c)
    {
        ulong s = c.sum;
        for (int i = 0; i < Bins; i++) s = s * 31 + (ulong)c.bins[i];
        return new Result(c.tokens, s);
    }
}

struct Counts
{
    public long[] bins;
    public long tokens;
    public ulong sum;
    public static Counts New() => new() { bins = new long[Kernel.Bins] };
}

static class Serial
{
    public static Result Run(byte[] text, Line[] lines, int passes)
    {
        var c = Counts.New();
        for (int p = 0; p < passes; p++)
            foreach (var l in lines)
            {
                uint h = Kernel.Basis;
                foreach (byte b in text.AsSpan(l.Start, l.Length))
                {
                    if (b == ' ') { Kernel.Count(h, ref c); h = Kernel.Basis; }
                    else h = (h ^ b) * Kernel.Prime;
                }
                Kernel.Count(h, ref c);
            }
        return Kernel.Finish(c);
    }
}

/// <summary>The Alice.Runtime dataflow graph: Source(lines) -> Stage(tokenize, 1:n) -> Sink(count).</summary>
static class Flow
{
    struct Gen(Line[] lines, int passes)
    {
        public readonly Line[] lines = lines;
        public readonly int passes = passes;
        public int pass, next;
    }

    public static Result Run(byte[] text, Line[] lines, int passes, int replicate)
    {
        var g = new Graph(capacity: 16384, batch: 512);
        var src = g.Source(new Gen(lines, passes), static (ref Gen s, ref RingWriter<Line> w) =>
        {
            int end = Math.Min(s.next + 4096, s.lines.Length);
            while (s.next < end && w.HasRoom) w.Write(s.lines[s.next++]);
            if (s.next == s.lines.Length) { s.next = 0; s.pass++; }
            return s.pass < s.passes;
        });
        Pipe<uint> tokens = replicate > 0
            ? src.Replicate<byte[], uint>(() => text, Tokenize, workers: replicate, ordered: true, batch: 1024)   // read-only text, shared on purpose
            : src.Stage<byte[], uint>(text, Tokenize);
        var sink = tokens.Sink(Counts.New(), static (ref Counts c, ReadOnlySpan<uint> hs) =>
        {
            foreach (var h in hs) Kernel.Count(h, ref c);
        });
        g.Run();
        return Kernel.Finish(sink.State);
    }

    static void Tokenize(ref byte[] text, ReadOnlySpan<Line> lines, ref RingWriter<uint> w)
    {
        foreach (var l in lines)
        {
            uint h = Kernel.Basis;
            foreach (byte b in text.AsSpan(l.Start, l.Length))
            {
                if (b == ' ') { w.Write(h); h = Kernel.Basis; }
                else h = (h ^ b) * Kernel.Prime;
            }
            w.Write(h);
        }
    }
}

/// <summary>The same pipeline as three Task.Run loops over bounded System.Threading.Channels.</summary>
static class Chans
{
    static BoundedChannelOptions Options => new(16384) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait };

    /// <summary>The obvious form: WriteAsync per item, ReadAllAsync per stage.</summary>
    public static Result RunNaive(byte[] text, Line[] lines, int passes)
    {
        var c1 = Channel.CreateBounded<Line>(Options);
        var c2 = Channel.CreateBounded<uint>(Options);
        var gen = Task.Run(async () =>
        {
            for (int p = 0; p < passes; p++)
                foreach (var l in lines) await c1.Writer.WriteAsync(l);
            c1.Writer.Complete();
        });
        var tok = Task.Run(async () =>
        {
            await foreach (var l in c1.Reader.ReadAllAsync())
            {
                uint h = Kernel.Basis;
                for (int i = l.Start, e = l.Start + l.Length; i < e; i++)
                {
                    byte b = text[i];
                    if (b == ' ') { await c2.Writer.WriteAsync(h); h = Kernel.Basis; }
                    else h = (h ^ b) * Kernel.Prime;
                }
                await c2.Writer.WriteAsync(h);
            }
            c2.Writer.Complete();
        });
        var count = Task.Run(async () =>
        {
            var c = Counts.New();
            await foreach (var h in c2.Reader.ReadAllAsync()) Kernel.Count(h, ref c);
            return c;
        });
        Task.WaitAll(gen, tok);
        return Kernel.Finish(count.Result);
    }

    /// <summary>The well-known fast idiom: TryWrite/TryRead first, await only when full or empty.</summary>
    public static Result RunTuned(byte[] text, Line[] lines, int passes)
    {
        var c1 = Channel.CreateBounded<Line>(Options);
        var c2 = Channel.CreateBounded<uint>(Options);
        var gen = Task.Run(async () =>
        {
            var w = c1.Writer;
            for (int p = 0; p < passes; p++)
                foreach (var l in lines)
                    if (!w.TryWrite(l)) await w.WriteAsync(l);
            w.Complete();
        });
        var tok = Task.Run(async () =>
        {
            var r = c1.Reader;
            var w = c2.Writer;
            while (await r.WaitToReadAsync())
                while (r.TryRead(out var l))
                {
                    uint h = Kernel.Basis;
                    for (int i = l.Start, e = l.Start + l.Length; i < e; i++)
                    {
                        byte b = text[i];
                        if (b == ' ') { if (!w.TryWrite(h)) await w.WriteAsync(h); h = Kernel.Basis; }
                        else h = (h ^ b) * Kernel.Prime;
                    }
                    if (!w.TryWrite(h)) await w.WriteAsync(h);
                }
            w.Complete();
        });
        var count = Task.Run(async () =>
        {
            var c = Counts.New();
            var r = c2.Reader;
            while (await r.WaitToReadAsync())
                while (r.TryRead(out var h)) Kernel.Count(h, ref c);
            return c;
        });
        Task.WaitAll(gen, tok);
        return Kernel.Finish(count.Result);
    }
}
