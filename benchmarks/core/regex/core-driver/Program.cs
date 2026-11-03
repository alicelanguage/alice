// Alice Core regex (src/Alice.Runtime/Core/Text/Regex) for benchmarks/core, same protocol as baselines/dotnet:
//   RESULT <engine> <case> <input> <median_ms> <MB_per_s> <alloc_bytes_per_call> <checksum>
// Engines: alice/utf8 (ReadOnlySpan<byte>, no transcoding) and alice/utf16 (string / ReadOnlySpan<char>).
// Usage: core-regex-bench regex-redux <file> [utf8|utf16] | rebar <file> [utf8|utf16] | info <pattern>
using System.Buffers;
using System.Diagnostics;
using System.Text;
using Alice.Core.Text;
using AliceRegex = Alice.Core.Text.Regex;

static void Measure(string engine, string kase, string input, long bytes, Func<long> f)
{
    bool once = Environment.GetEnvironmentVariable("ONCE") is not null;
    double warmMs = double.TryParse(Environment.GetEnvironmentVariable("WARM_MS"), out var w) ? w : 1000;
    int warmN = int.TryParse(Environment.GetEnvironmentVariable("WARM_N"), out var wn) ? wn : 50;
    int timed = int.TryParse(Environment.GetEnvironmentVariable("TIMED"), out var t) ? t : 15;
    long reference = f();
    if (!once)
    {
        var sw0 = Stopwatch.StartNew();
        for (int n = 0; n < warmN || sw0.Elapsed.TotalMilliseconds < warmMs; n++)
            if (f() != reference) throw new Exception("checksum changed in warm-up");
    }
    int runs = once ? 1 : timed;
    var ts = new double[runs];
    long alloc = 0;
    for (int i = 0; i < runs; i++)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        long s = Stopwatch.GetTimestamp();
        long c = f();
        ts[i] = Stopwatch.GetElapsedTime(s).TotalMilliseconds;
        alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
        if (c != reference) throw new Exception("checksum changed");
    }
    Array.Sort(ts);
    double med = ts[runs / 2];
    Console.WriteLine($"RESULT {engine} {kase} {input} {med:F4} {bytes / 1e6 / (med / 1e3):F1} {alloc / runs} {reference}");
}

string[] variants =
[
    "agggtaaa|tttaccct", "[cgt]gggtaaa|tttaccc[acg]", "a[act]ggtaaa|tttacc[agt]t", "ag[act]gtaaa|tttac[agt]ct",
    "agg[act]taaa|ttta[agt]cct", "aggg[acg]aaa|ttt[cgt]ccct", "agggt[cgt]aa|tt[acg]accct", "agggta[cgt]a|t[acg]taccct",
    "agggtaa[cgt]|[acg]ttaccct",
];
(string, string)[] subst = [("tHa[Nt]", "<4>"), ("aND|caN|Ha[DS]|WaS", "<3>"), ("a[NSt]|BY", "<2>"), ("<[^>]*>", "|"), ("\\|[^|][^|]*\\|", "-")];
(string Name, string Pattern)[] rebar =
[
    ("literal", "Sherlock Holmes"),
    ("literal-alt", "Sherlock Holmes|John Watson|Irene Adler|Inspector Lestrade|Professor Moriarty"),
    ("literal-i", "(?i)Sherlock Holmes"),
    ("words", "\\b\\w+\\b"),
    ("ing-suffix", "[a-zA-Z]+ing"),
    ("aws-keys", "(?:AKIA|ASIA)[A-Z0-9]{16}"),
    ("bounded-repeat", "[A-Za-z]{8,13}"),
    ("unicode-upper", "\\p{Lu}\\p{Ll}+"),
    ("time", "[0-9]{1,2}:[0-9]{2}"),
];

void ReduxUtf8(string path)
{
    byte[] input = File.ReadAllBytes(path);
    var strip = new AliceRegex(">.*\n|\n");
    var vs = variants.Select(p => new AliceRegex(p)).ToArray();
    var ss = subst.Select(p => (new AliceRegex(p.Item1), Encoding.UTF8.GetBytes(p.Item2))).ToArray();
    var w = new[] { new ArrayBufferWriter<byte>(input.Length), new ArrayBufferWriter<byte>(input.Length), new ArrayBufferWriter<byte>(input.Length) };
    bool printed = false;
    Measure("alice/utf8", "regex-redux", "fasta-5M", input.Length, () =>
    {
        w[0].ResetWrittenCount();
        strip.Replace(input, ""u8, w[0]);
        var seq = w[0].WrittenSpan;
        var sb = new StringBuilder();
        long sum = 0;
        for (int i = 0; i < vs.Length; i++) { int c = vs[i].Count(seq); sum += c; sb.Append(variants[i]).Append(' ').Append(c).Append('\n'); }
        int cur = 0;
        for (int k = 0; k < ss.Length; k++)
        {
            int next = cur == 1 ? 2 : 1;
            w[next].ResetWrittenCount();
            ss[k].Item1.Replace(w[cur].WrittenSpan, ss[k].Item2, w[next]);
            cur = next;
        }
        int seqLen = w[0].WrittenCount, outLen = w[cur].WrittenCount;
        sb.Append($"\n{input.Length}\n{seqLen}\n{outLen}\n");
        if (!printed) { Console.Error.Write(sb); printed = true; }
        return sum * 1_000_000_000 + outLen;
    });
}

void ReduxUtf16(string path)
{
    byte[] bytes = File.ReadAllBytes(path);
    string input = Encoding.Latin1.GetString(bytes);
    var strip = new AliceRegex(">.*\n|\n");
    var vs = variants.Select(p => new AliceRegex(p)).ToArray();
    var ss = subst.Select(p => (new AliceRegex(p.Item1), p.Item2)).ToArray();
    bool printed = false;
    Measure("alice/utf16", "regex-redux", "fasta-5M", bytes.Length, () =>
    {
        string seq = strip.Replace(input, "");
        var sb = new StringBuilder();
        long sum = 0;
        for (int i = 0; i < vs.Length; i++) { int c = vs[i].Count(seq); sum += c; sb.Append(variants[i]).Append(' ').Append(c).Append('\n'); }
        string s = seq;
        foreach (var (r, rep) in ss) s = r.Replace(s, rep);
        sb.Append($"\n{input.Length}\n{seq.Length}\n{s.Length}\n");
        if (!printed) { Console.Error.Write(sb); printed = true; }
        return sum * 1_000_000_000 + s.Length;
    });
}

void Rebar(string path, string enc)
{
    byte[] bytes = File.ReadAllBytes(path);
    string hay = Encoding.UTF8.GetString(bytes);
    string name = Path.GetFileNameWithoutExtension(path);
    var only = Environment.GetEnvironmentVariable("CASES")?.Split(',');   // optional subset, e.g. CASES=words,time
    foreach (var (kase, pat) in rebar)
    {
        if (only != null && !only.Contains(kase)) continue;
        var r = new AliceRegex(pat);
        if (enc != "utf16") Measure("alice/utf8", "count:" + kase, name, bytes.Length, () => r.Count(bytes.AsSpan()));
        if (enc != "utf8") Measure("alice/utf16", "count:" + kase, name, bytes.Length, () => r.Count(hay.AsSpan()));
    }
}

switch (args.ElementAtOrDefault(0))
{
    case "regex-redux":
        if (args.ElementAtOrDefault(2) is null or "utf8") ReduxUtf8(args[1]);
        if (args.ElementAtOrDefault(2) is null or "utf16") ReduxUtf16(args[1]);
        break;
    case "rebar": Rebar(args[1], args.ElementAtOrDefault(2) ?? ""); break;
    case "profile":
        {
            // regex-redux stage by stage (UTF-8), best of 5
            byte[] input = File.ReadAllBytes(args[1]);
            var a = new ArrayBufferWriter<byte>(input.Length);
            var b = new ArrayBufferWriter<byte>(input.Length);
            double Best(Action f) { double best = double.MaxValue; for (int i = 0; i < 5; i++) { var s0 = Stopwatch.GetTimestamp(); f(); best = Math.Min(best, Stopwatch.GetElapsedTime(s0).TotalMilliseconds); } return best; }
            var strip = new AliceRegex(">.*\n|\n");
            Console.WriteLine($"strip {Best(() => { a.ResetWrittenCount(); strip.Replace(input, ""u8, a); }):F1} ms");
            byte[] seq = a.WrittenSpan.ToArray();
            foreach (var v in variants) { var r = new AliceRegex(v); Console.WriteLine($"count {v} {Best(() => r.Count(seq)):F1} ms"); }
            byte[] cur = seq;
            foreach (var (p, rep) in subst)
            {
                var r = new AliceRegex(p);
                var rb = Encoding.UTF8.GetBytes(rep);
                byte[] src = cur;
                Console.WriteLine($"subst {p} {Best(() => { b.ResetWrittenCount(); r.Replace(src, rb, b); }):F1} ms ({r.Count(src)} matches)");
                cur = b.WrittenSpan.ToArray();
            }
            break;
        }
    case "info":
        foreach (var p in args.Skip(1)) Console.WriteLine($"/{p}/ {new AliceRegex(p).Describe()} | utf8 {new AliceRegex(p).Describe(true)}");
        break;
    default:
        Console.Error.WriteLine("usage: core-regex-bench regex-redux <file> [utf8|utf16] | rebar <file> [utf8|utf16] | info <pattern>...");
        return 2;
}
return 0;
