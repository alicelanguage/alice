// Alice Core library benchmarks (benchmarks/core protocol, README.md):
//   RESULT <engine> <case> <input> <median_ms> <MB_per_s> <alloc_bytes_per_call> <checksum>
// Warm-up >= WARM_MS ms (default 1000) and >= WARM_N calls (default 50); TIMED calls (default 15), median reported.
// Allocation = GC.GetAllocatedBytesForCurrentThread delta, mean over the timed calls. Usage: core-lib-bench <data dir> [case...]
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Alice.Core.Crypto;
using Alice.Core.Collections;
using Alice.Core.Csv;
using Alice.Core.Fs;
using Alice.Core.Net;
using Alice.Core.Text;
using Alice.Core.Toml;
using Alice.Runtime;
using CoreTime = Alice.Core.Time.Time;

string dataDir = args.Length > 0 ? args[0] : "data";
var only = args.Skip(1).ToHashSet();
bool Run(string suite) => only.Count == 0 || only.Contains(suite);
Directory.CreateDirectory(dataDir);

if (Run("text")) Text();
if (Run("format")) Formatting();
if (Run("hash")) Hashes();
if (Run("csv")) CsvRead(dataDir);
if (Run("lines")) Lines(dataDir);
if (Run("hmac")) HmacBench();
if (Run("collections")) Collections();
if (Run("toml")) TomlParse();
if (Run("echo")) await Echo();

static void Measure(string engine, string kase, string input, long bytes, Func<long> f)
{
    double warmMs = double.TryParse(Environment.GetEnvironmentVariable("WARM_MS"), out var w) ? w : 1000;
    int warmN = int.TryParse(Environment.GetEnvironmentVariable("WARM_N"), out var wn) ? wn : 50;
    int timed = int.TryParse(Environment.GetEnvironmentVariable("TIMED"), out var t) ? t : 15;
    long reference = f();
    var sw0 = Stopwatch.StartNew();
    for (int n = 0; n < warmN || sw0.Elapsed.TotalMilliseconds < warmMs; n++)
        if (f() != reference) throw new Exception($"{engine} {kase}: checksum changed in warm-up");
    var ts = new double[timed];
    long alloc = 0;
    for (int i = 0; i < timed; i++)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        long s = Stopwatch.GetTimestamp();
        long c = f();
        ts[i] = Stopwatch.GetElapsedTime(s).TotalMilliseconds;
        alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
        if (c != reference) throw new Exception($"{engine} {kase}: checksum changed");
    }
    Array.Sort(ts);
    double med = ts[timed / 2];
    Console.WriteLine($"RESULT {engine} {kase} {input} {med:F4} {bytes / 1e6 / (med / 1e3):F1} {alloc / timed} {reference}");
}

// 1 MB of words with some non-ASCII, deterministic
static string Words(int bytes)
{
    var rng = new Random(42);
    string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "aaardvark", "mississippi", "café", "naïve", "zoo", "hello", "world"];
    var sb = new StringBuilder(bytes + 16);
    while (sb.Length < bytes)
    {
        sb.Append(words[rng.Next(words.Length)]);
        sb.Append(rng.Next(12) == 0 ? '\n' : ' ');
    }
    return sb.ToString();
}

static void Text()
{
    string s = Words(1 << 20);
    byte[] u = Encoding.UTF8.GetBytes(s);
    var dst = new byte[u.Length];
    var set = Utf8ByteSet.Parse("a-m");
    var tr = new Utf8Translation("a-y", "b-z");
    var vowels = Utf8ByteSet.Parse("aeiou");
    Measure("alice-core", "count:a-m", "words-1MB", u.Length, () => Utf8Text.Count(u, set));
    Measure("prelude", "count:a-m", "words-1MB", u.Length, () => Prelude.count(s, "a-m"));
    Measure("alice-core", "tr:a-y/b-z", "words-1MB", u.Length, () => { Utf8Text.Tr(u, tr, dst); return dst[100]; });
    Measure("prelude", "tr:a-y/b-z", "words-1MB", u.Length, () => Prelude.tr(s, "a-y", "b-z")[100]);
    Measure("alice-core", "squeeze", "words-1MB", u.Length, () => Utf8Text.Squeeze(u, null, dst));
    Measure("prelude", "squeeze", "words-1MB", u.Length, () => Prelude.squeeze(s).Length);
    Measure("alice-core", "delete:aeiou", "words-1MB", u.Length, () => Utf8Text.Delete(u, vowels, dst));
    Measure("prelude", "delete:aeiou", "words-1MB", u.Length, () => Prelude.delete(s, "aeiou").Length);
    Measure("alice-core", "char-count", "words-1MB", u.Length, () => Utf8Text.CharCount(u));
    Measure("bcl", "char-count", "words-1MB", u.Length, () => Encoding.UTF8.GetCharCount(u));
}

static void Formatting()
{
    const int N = 1000;
    var buf = new char[256];
    var when = CoreTime.WithOffset(2026, 1, 2, 3, 4, 5.25, 3600);
    var dto = when.ToDateTimeOffset();
    Measure("alice-core", "format:5-specs-x1000", "-", N, () =>
    {
        long sum = 0;
        for (int i = 0; i < N; i++) { Format.TryFormat(buf, out int n, "%05d %-8s %.3f %x %e", i, "name", i * 0.5, i, 1e10); sum += n; }
        return sum;
    });
    Measure("prelude", "format:5-specs-x1000", "-", N, () =>
    {
        long sum = 0;
        for (int i = 0; i < N; i++) sum += Prelude.format("%05d %-8s %.3f %x %e", i, "name", i * 0.5, i, 1e10).Length;
        return sum;
    });
    Measure("alice-core", "strftime-x1000", "-", N, () =>
    {
        long sum = 0;
        for (int i = 0; i < N; i++) { when.TryStrftime(buf, out int n, "%Y-%m-%dT%H:%M:%S.%3N%:z"); sum += n; }
        return sum;
    });
    Measure("bcl", "strftime-x1000", "-", N, () =>
    {
        long sum = 0;
        for (int i = 0; i < N; i++) { dto.TryFormat(buf, out int n, "yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture); sum += n; }
        return sum;
    });
}

static void Hashes()
{
    var data = new byte[100 << 20];
    new Random(5).NextBytes(data);
    var h = new byte[64];
    Measure("alice-core", "crc32c", "random-100MB", data.Length, () => Crc32C.Compute(data));
    Measure("alice-core", "xxh3-64", "random-100MB", data.Length, () => (long)(XxHash3.Hash64(data) & 0x7FFF_FFFF));
    Measure("alice-core", "sha256", "random-100MB", data.Length, () => { Digests.Sha256(data, h); return h[0]; });
    Measure("bcl", "sha256", "random-100MB", data.Length, () => { SHA256.HashData(data, h); return h[0]; });
    var small = data.AsSpan(0, 64).ToArray();
    Measure("alice-core", "xxh3-64-x1000", "64B", 64 * 1000, () => { ulong a = 0; for (int i = 0; i < 1000; i++) a += XxHash3.Hash64(small); return (long)(a & 0x7FFF_FFFF); });
}

static void Collections()
{
    const int N = 1_000_000;
    // FIFO: push to the back, shift from the front
    Measure("alice-core", "deque-fifo-1M", "long", N * 8L, () =>
    {
        var d = new Deque<long>(16); long sum = 0;
        for (int i = 0; i < N; i++) { d.Push(i); if ((i & 3) == 3) { sum += d.Shift(); sum += d.Shift(); } }
        while (!d.IsEmpty) sum += d.Shift();
        return sum;
    });
    Measure("bcl", "queue-fifo-1M", "long", N * 8L, () =>
    {
        var d = new Queue<long>(16); long sum = 0;
        for (int i = 0; i < N; i++) { d.Enqueue(i); if ((i & 3) == 3) { sum += d.Dequeue(); sum += d.Dequeue(); } }
        while (d.Count > 0) sum += d.Dequeue();
        return sum;
    });
    // double-ended: unshift and push, pop and shift (BCL: LinkedList)
    Measure("alice-core", "deque-both-ends-1M", "long", N * 8L, () =>
    {
        var d = new Deque<long>(16); long sum = 0;
        for (int i = 0; i < N; i++) { if ((i & 1) == 0) d.Push(i); else d.Unshift(i); if ((i & 7) == 7) { sum += d.Pop(); sum += d.Shift(); } }
        while (!d.IsEmpty) sum += d.Pop();
        return sum;
    });
    Measure("bcl", "linkedlist-both-ends-1M", "long", N * 8L, () =>
    {
        var d = new LinkedList<long>(); long sum = 0;
        for (int i = 0; i < N; i++) { if ((i & 1) == 0) d.AddLast(i); else d.AddFirst(i); if ((i & 7) == 7) { sum += d.Last!.Value; d.RemoveLast(); sum += d.First!.Value; d.RemoveFirst(); } }
        while (d.Count > 0) { sum += d.Last!.Value; d.RemoveLast(); }
        return sum;
    });
    Measure("alice-core", "set-add-contains-1M", "long", N * 8L, () =>
    {
        var s = new Set<long>(); long hits = 0;
        for (int i = 0; i < N; i++) s.Add(i * 7919L % 400_009);
        for (int i = 0; i < N; i++) if (s.Include(i)) hits++;
        return hits + s.Count;
    });
    Measure("bcl", "hashset-add-contains-1M", "long", N * 8L, () =>
    {
        var s = new HashSet<long>(); long hits = 0;
        for (int i = 0; i < N; i++) s.Add(i * 7919L % 400_009);
        for (int i = 0; i < N; i++) if (s.Contains(i)) hits++;
        return hits + s.Count;
    });
}

static string TomlDoc()
{
    var sb = new StringBuilder("title = \"bench\"\n\n");
    for (int i = 0; i < 5000; i++)
    {
        sb.Append("[[package]]\n");
        sb.Append($"name = \"pkg-{i}\"\nversion = \"{i % 9}.{i % 7}.{i % 5}\"\nfeatures = [\"a\", \"b\", \"c{i % 3}\"]\nweight = {i * 0.25}\nenabled = {(i % 2 == 0 ? "true" : "false")}\nreleased = 2026-01-{1 + i % 28:D2}T10:00:00Z\n");
        sb.Append($"[package.deps]\ncore = {{ version = \"1.{i % 10}\", optional = false }}\n\n");
    }
    return sb.ToString();
}

static void TomlParse()
{
    string text = TomlDoc();
    byte[] utf8 = Encoding.UTF8.GetBytes(text);
    // No BCL TOML parser exists (correctness target: toml-test); throughput and allocation are recorded as the baseline.
    Measure("alice-core", "toml-parse", "5000-packages", utf8.Length, () => Toml.Parse(utf8).Count);
    Measure("alice-core", "toml-roundtrip", "5000-packages", utf8.Length, () => Toml.Write(Toml.Parse(utf8)).Length);
}

static void HmacBench()
{
    var key = new byte[32]; var msg = new byte[64]; var h = new byte[32];
    Measure("alice-core", "hmac-sha256-x1000", "64B", 64 * 1000, () => { long a = 0; for (int i = 0; i < 1000; i++) { msg[0] = (byte)i; Hmac.Sha256(key, msg, h); a += h[0]; } return a; });
    Measure("bcl", "hmac-sha256-x1000", "64B", 64 * 1000, () => { long a = 0; for (int i = 0; i < 1000; i++) { msg[0] = (byte)i; HMACSHA256.HashData(key, msg, h); a += h[0]; } return a; });
}

static string CsvFile(string dir)
{
    string path = Path.Combine(dir, "core-lib-100MB.csv");
    if (File.Exists(path) && new FileInfo(path).Length >= 100_000_000) return path;
    var rng = new Random(9);
    using var f = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
    f.Write("id,name,city,price,qty,note,ts,flag\n");
    string[] names = ["alice", "bob", "carol", "dave", "eve", "mallory", "trent", "peggy"];
    string[] cities = ["Paris", "New York", "São Paulo", "Tokyo", "Berlin"];
    long written = 0;
    for (long id = 0; written < 100_000_000; id++)
    {
        string note = rng.Next(5) == 0 ? "\"has, comma and \"\"quotes\"\"\"" : "plain note";
        string line = $"{id},{names[rng.Next(names.Length)]},{cities[rng.Next(cities.Length)]},{rng.Next(100000) / 100.0},{rng.Next(1000)},{note},2026-01-{rng.Next(1, 29):D2}T10:00:00Z,{(rng.Next(2) == 0 ? "true" : "false")}\n";
        f.Write(line);
        written += Encoding.UTF8.GetByteCount(line);
    }
    return path;
}

static void CsvRead(string dir)
{
    byte[] data = File.ReadAllBytes(CsvFile(dir));
    Environment.SetEnvironmentVariable("WARM_N", Environment.GetEnvironmentVariable("WARM_N") ?? "5");
    Measure("alice-core", "csv-rows-fields", "csv-100MB", data.Length, () =>
    {
        long sum = 0;
        var r = new CsvReader(data);
        while (r.MoveNext())
        {
            var row = r.Current;
            for (int i = 0; i < row.Count; i++) sum += row[i].Raw.Length;
        }
        r.Dispose();
        return sum;
    });
    Measure("alice-core", "csv-rows-typed", "csv-100MB", data.Length, () =>
    {
        long sum = 0;
        var r = new CsvReader(data);
        r.MoveNext();
        while (r.MoveNext())
        {
            var row = r.Current;
            if (row[0].TryGetInt64(out long id)) sum += id;
            if (row[4].TryGetInt64(out long q)) sum += q;
        }
        r.Dispose();
        return sum;
    });
    Measure("alice-core", "csv-count-rows", "csv-100MB", data.Length, () => Csv.CountRows(data));
}

static void Lines(string dir)
{
    string path = CsvFile(dir);
    long size = new FileInfo(path).Length;
    Environment.SetEnvironmentVariable("WARM_N", Environment.GetEnvironmentVariable("WARM_N") ?? "5");
    Measure("alice-core", "each-line", "csv-100MB", size, () =>
    {
        long sum = 0;
        var r = Fs.EachLine(path);
        while (r.MoveNext()) sum += r.Current.Length;
        r.Dispose();
        return sum;
    });
    Measure("bcl", "each-line:File.ReadLines", "csv-100MB", size, () =>
    {
        long sum = 0;
        foreach (var l in File.ReadLines(path)) sum += l.Length + 1;
        return sum;
    });
    Measure("alice-core", "count-lines:mapped", "csv-100MB", size, () => Fs.CountLines(path));
}

// echo: 256 connections, 64-byte messages, round-robin from one client thread; server = Alice TcpServer.ServeAsync or
// a raw Socket accept loop with the same handler. bytes/msg = process-wide allocation over the timed window.
static async Task Echo()
{
    const int Conns = 256, Rounds = 200, Msg = 64;
    foreach (bool raw in new[] { false, true })
    {
        using var cts = new CancellationTokenSource();
        Socket? rawListener = null;
        int port;
        Task serving;
        TcpServer? srv = null;
        if (!raw)
        {
            srv = TcpServer.Listen(0);
            port = srv.Port;
            serving = srv.ServeAsync(async (c, ct) =>
            {
                var buf = ArrayPool<byte>.Shared.Rent(4096);
                int n;
                while ((n = await c.ReadAsync(buf, ct)) > 0) await c.WriteAsync(buf.AsMemory(0, n), ct);
                ArrayPool<byte>.Shared.Return(buf);
            }, cts.Token);
        }
        else
        {
            rawListener = new Socket(SocketType.Stream, ProtocolType.Tcp);
            rawListener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            rawListener.Listen(512);
            port = ((System.Net.IPEndPoint)rawListener.LocalEndPoint!).Port;
            serving = Task.Run(async () =>
            {
                while (true)
                {
                    Socket s;
                    try { s = await rawListener.AcceptAsync(cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        var buf = ArrayPool<byte>.Shared.Rent(4096);
                        int n;
                        try { while ((n = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token)) > 0) await s.SendAsync(buf.AsMemory(0, n), SocketFlags.None, cts.Token); }
                        catch { }
                        s.Dispose();
                    });
                }
            });
        }
        var clients = new List<Socket>();
        for (int i = 0; i < Conns; i++)
        {
            var c = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            c.Connect("127.0.0.1", port);
            clients.Add(c);
        }
        var msg = new byte[Msg];
        var back = new byte[Msg];
        long Round()
        {
            long sum = 0;
            foreach (var c in clients)
            {
                c.Send(msg);
                int got = 0;
                while (got < Msg) got += c.Receive(back.AsSpan(got));
                sum += got;
            }
            return sum;
        }
        for (int i = 0; i < 50; i++) Round();
        var times = new double[7];
        long leastAlloc = long.MaxValue;
        for (int k = 0; k < times.Length; k++)
        {
            long a0 = GC.GetTotalAllocatedBytes(precise: true);
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < Rounds; i++) Round();
            times[k] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            leastAlloc = Math.Min(leastAlloc, GC.GetTotalAllocatedBytes(precise: true) - a0);
        }
        Array.Sort(times);
        double med = times[times.Length / 2];
        long msgs = (long)Conns * Rounds;
        Console.WriteLine($"RESULT {(raw ? "bcl-socket" : "alice-core")} echo-256conns-64B loopback {med:F4} {msgs * Msg * 2 / 1e6 / (med / 1e3):F1} {leastAlloc / (double)msgs:F3}B/msg {msgs}");
        foreach (var c in clients) c.Dispose();
        cts.Cancel();
        srv?.Dispose();
        rawListener?.Dispose();
        try { await serving; } catch { }
    }
}
