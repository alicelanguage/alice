using System.Diagnostics;
using Alice.Runtime;
using Bench.Inet;

// pow2(n) = pow2(n-1) + pow2(n-1): the fork/join example of docs/fork-join.md, 2^n leaves.
// plain recursion; fork/join as Alice emitted it up to 0.12.2 (Forkable read on every call) and from 0.12.3 (serial
// clone below the fork depth); then the interaction-net reducer of Net.cs on 1..N threads.
if (args.Length > 0 && args[0] == "hvm2") { Hvm2Bench.Run(args[1..]); return; }
int n = args.Length > 0 ? int.Parse(args[0]) : 22;
int reps = args.Length > 1 ? int.Parse(args[1]) : 5;

const int Pow2 = 0, Pow2S = 1;
Def[] defs = [BuildPow2(), BuildPow2S()];

static Def BuildPow2()
{
    // @pow2 = (n r) with n ~ ?((1 @pow2S) r)
    var b = new DefBuilder("pow2");
    var r = b.Var();
    return b.Build(b.Con(b.Swi(b.Con(DefBuilder.Num(1), DefBuilder.Ref(Pow2S)), r), r));
}

static Def BuildPow2S()
{
    // @pow2S = (p R) with p ~ {p1 p2}, @pow2 ~ (p1 r1), @pow2 ~ (p2 r2), r1 ~ $(r2 R)
    var b = new DefBuilder("pow2S");
    ulong p1 = b.Var(), p2 = b.Var(), r2 = b.Var(), res = b.Var();
    b.Redex(DefBuilder.Ref(Pow2), b.Con(p1, b.Add(r2, res)));
    b.Redex(DefBuilder.Ref(Pow2), b.Con(p2, r2));
    return b.Build(b.Con(b.Dup(p1, p2), res));
}

static long Plain(int n) => n == 0 ? 1 : Plain(n - 1) + Plain(n - 1);

static long Forked(int n)
{
    if (n == 0) return 1;
    long a, c;
    if (Par.Forkable) (a, c) = Par.Fork(() => Forked(n - 1), () => Forked(n - 1));
    else { a = Forked(n - 1); c = Forked(n - 1); }
    return a + c;
}

// same forks, but below the fork depth the recursion switches to a clone with no Forkable check at all
static long ForkedClone(int n)
{
    if (n == 0) return 1;
    if (!Par.Forkable) return Plain(n);
    var (a, c) = Par.Fork(() => ForkedClone(n - 1), () => ForkedClone(n - 1));
    return a + c;
}

double Time(Func<long> f, out long value)
{
    value = f();   // warm-up (JIT tiers, pool threads)
    var best = double.MaxValue;
    for (int i = 0; i < reps; i++)
    {
        var sw = Stopwatch.StartNew();
        value = f();
        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
    }
    return best;
}

long expected = 1L << n;
Console.WriteLine($"pow2({n}) = {expected}, {Environment.ProcessorCount} cores, best of {reps}");
Console.WriteLine($"{"variant",-28}{"ms",10}{"vs plain",10}{"MIPS",10}");
double plain = Time(() => Plain(n), out var v0);
Report("plain recursion", plain, v0, 0);
Report("fork/join, check per call", Time(() => Forked(n), out var v1), v1, 0);
Report("fork/join + serial clone", Time(() => ForkedClone(n), out var v2), v2, 0);

if (args.Length > 2 && args[2] == "noinet") return;
int cap = (int)Math.Min(int.MaxValue / 4, (1L << n) * 24 + (1 << 20));
foreach (int threads in new[] { 1, 2, 4, 8, Environment.ProcessorCount }.Distinct().Where(t => t <= Environment.ProcessorCount))
{
    // heap allocation stays outside the timed region: only reduction is measured
    long inter = 0, vi = 0;
    double ms = double.MaxValue;
    for (int i = 0; i <= reps; i++)
    {
        var net = new Net(cap, defs);
        net.Nodes.AsSpan().Fill(0); net.Vars.AsSpan().Fill(0);   // fault the pages in before timing
        ulong root = Net.P(Tag.Var, (ulong)(cap - 1));   // the last var cell, outside every worker region
        var start = (DefBuilder.Ref(Pow2), NetCon(net, n, root));
        var sw = Stopwatch.StartNew();
        vi = Scheduler.Reduce(net, root, start, threads, out inter);
        if (i > 0) ms = Math.Min(ms, sw.Elapsed.TotalMilliseconds);   // rep 0 warms the JIT
    }
    Report($"interaction net, {threads} thr", ms, vi, inter / ms / 1000);
}

// main = @pow2 ~ (#n root): the caller node lives in the last node slot
static ulong NetCon(Net net, int n, ulong root)
{
    ulong loc = (ulong)net.Nodes.Length / 2 - 1;
    net.Nodes[loc * 2] = Net.P(Tag.Num, (ulong)n);
    net.Nodes[loc * 2 + 1] = root;
    return Net.P(Tag.Con, loc);
}

void Report(string name, double ms, long value, double mips)
{
    if (value != expected) throw new Exception($"{name}: got {value}, expected {expected}");
    Console.WriteLine($"{name,-28}{ms,10:F1}{plain / ms,9:F2}x{(mips > 0 ? mips.ToString("F0") : ""),10}");
}
