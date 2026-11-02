using System.Diagnostics;
using Alice.Runtime;

int d = args.Length > 0 ? int.Parse(args[0]) : 23;
int reps = args.Length > 1 ? int.Parse(args[1]) : 7;
int cut = d - 5;
uint expected = (uint)(((1UL << d) + (1UL << (d + 1)) - 1) * (1UL << d) / 2);
Console.WriteLine($"tree({d}), {Environment.ProcessorCount} cores, expected {expected}");

var rows = new (string Name, Func<uint> Run)[]
{
    ("records serial", () => Rec.Sum(Rec.Gen(d, 1))),
    ("records par", () => Rec.SumPar(Rec.GenPar(d, 1, cut), d, cut)),
    ("arena consume serial", () => { using var r = ArenaRegion.Enter(); return Tree.Sum(Tree.Gen(d, 1)); }),
    ("arena borrow serial", () => { using var r = ArenaRegion.Enter(); return Tree.SumB(Tree.Gen(d, 1)); }),
    ("arena par", () => { using var r = ArenaRegion.Enter(); return Tree.SumPar(Tree.GenPar(d, 1, cut), d, cut); }),
};
bool ok = true;
var best = new Dictionary<string, double>();
foreach (var (name, run) in rows)
{
    var times = new List<double>();
    for (int i = 0; i < reps; i++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var sw = Stopwatch.StartNew();
        uint v = run();
        times.Add(sw.Elapsed.TotalMilliseconds);
        if (v != expected) { Console.WriteLine($"{name}: checksum {v} != {expected}"); ok = false; }
    }
    times.Sort();
    best[name] = times[0];
    Console.WriteLine($"{name,-22} min {times[0],7:F1} ms  median {times[times.Count / 2],7:F1} ms  max {times[^1],7:F1} ms");
}
Console.WriteLine($"serial speedup vs records: {best["records serial"] / best["arena consume serial"]:F1}x; arena par vs arena serial: {best["arena consume serial"] / best["arena par"]:F1}x");
if (d == 23 && best["arena consume serial"] > 220) { Console.WriteLine("FAIL: arena consume serial > 220 ms"); ok = false; }
Console.WriteLine(ok ? "PASS" : "FAIL");
return ok ? 0 : 1;

abstract record RTree;
sealed record RLeaf(uint V) : RTree;
sealed record RNode(RTree L, RTree R) : RTree;

static class Rec
{
    public static RTree Gen(int d, uint x) => d == 0 ? new RLeaf(x) : new RNode(Gen(d - 1, x * 2), Gen(d - 1, x * 2 + 1));
    public static uint Sum(RTree t) => t is RLeaf l ? l.V : Sum(((RNode)t).L) + Sum(((RNode)t).R);
    public static RTree GenPar(int d, uint x, int cut)
    {
        if (d <= cut) return Gen(d, x);
        var (l, r) = Par.Fork(() => GenPar(d - 1, x * 2, cut), () => GenPar(d - 1, x * 2 + 1, cut));
        return new RNode(l, r);
    }
    public static uint SumPar(RTree t, int d, int cut)
    {
        if (d <= cut) return Sum(t);
        var n = (RNode)t;
        var (l, r) = Par.Fork(() => SumPar(n.L, d - 1, cut), () => SumPar(n.R, d - 1, cut));
        return l + r;
    }
}

record struct Pair(Term L, Term R);

static class Tree
{
    const int Leaf = 0, Node = 1;
    public static Term Gen(int d, uint x) =>
        d == 0 ? Term.Pack(Leaf, x) : Arena<Pair>.Alloc(Node, new Pair(Gen(d - 1, x * 2), Gen(d - 1, x * 2 + 1)));
    public static uint Sum(Term t)
    {
        if (t.IsPacked) return t.Payload;
        Arena<Pair>.Take(t, out var p);
        return Sum(p.L) + Sum(p.R);
    }
    public static uint SumB(Term t)
    {
        if (t.IsPacked) return t.Payload;
        ref readonly var p = ref Arena<Pair>.Borrow(t);
        return SumB(p.L) + SumB(p.R);
    }
    public static Term GenPar(int d, uint x, int cut)
    {
        if (d <= cut) return Gen(d, x);
        var (l, r) = Par.Fork(() => GenPar(d - 1, x * 2, cut), () => GenPar(d - 1, x * 2 + 1, cut));
        return Arena<Pair>.Alloc(Node, new Pair(l, r));
    }
    public static uint SumPar(Term t, int d, int cut)
    {
        if (d <= cut) return Sum(t);
        Arena<Pair>.Take(t, out var p);
        var (l, r) = Par.Fork(() => SumPar(p.L, d - 1, cut), () => SumPar(p.R, d - 1, cut));
        return l + r;
    }
}
