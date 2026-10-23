using System.Diagnostics;

namespace Bench.Inet;

/// <summary>`dotnet run -c Release -- hvm2 [n] [reps]`: the HVM2-faithful reducer on pow2 and on a tree build + sum.</summary>
public static class Hvm2Bench
{
    const int Pow2 = 0, Pow2S = 1, Build = 2, BuildS = 3, SumT = 4, SumS = 5, SumZ = 6;

    static HDef[] Book()
    {
        var defs = new HDef[7];
        {   // @pow2 = (n r) & n ~ ?((#1 @pow2S) r)
            var b = new HBuilder("pow2"); var r = b.Var();
            defs[Pow2] = b.Build(b.Con(b.Swi(b.Con(HBuilder.Num(1), HBuilder.Ref(Pow2S)), r), r));
        }
        {   // @pow2S = ({p1 p2} R) & @pow2 ~ (p1 $(r2 R)) & @pow2 ~ (p2 r2)
            var b = new HBuilder("pow2S"); uint p1 = b.Var(), p2 = b.Var(), r2 = b.Var(), res = b.Var();
            b.Redex(HBuilder.Ref(Pow2), b.Con(p1, b.Add(r2, res)));
            b.Redex(HBuilder.Ref(Pow2), b.Con(p2, r2));
            defs[Pow2S] = b.Build(b.Con(b.Dup(p1, p2), res));
        }
        {   // @build = (d R) & d ~ ?((#1 @buildS) R): a leaf is #1, a node is a CON pair
            var b = new HBuilder("build"); var r = b.Var();
            defs[Build] = b.Build(b.Con(b.Swi(b.Con(HBuilder.Num(1), HBuilder.Ref(BuildS)), r), r));
        }
        {   // @buildS = ({p1 p2} (l r)) & @build ~ (p1 l) & @build ~ (p2 r)
            var b = new HBuilder("buildS"); uint p1 = b.Var(), p2 = b.Var(), l = b.Var(), r = b.Var();
            b.Redex(HBuilder.Ref(Build), b.Con(p1, l));
            b.Redex(HBuilder.Ref(Build), b.Con(p2, r));
            defs[BuildS] = b.Build(b.Con(b.Dup(p1, p2), b.Con(l, r)));
        }
        {   // @sumT = (d x) & d ~ ?((@sumZ @sumS) x): x is (tree result)
            var b = new HBuilder("sumT"); var x = b.Var();
            defs[SumT] = b.Build(b.Con(b.Swi(b.Con(HBuilder.Ref(SumZ), HBuilder.Ref(SumS)), x), x));
        }
        {   // @sumS = ({p1 p2} ((l r) R)) & @sumT ~ (p1 (l $(r2 R))) & @sumT ~ (p2 (r r2)): annihilation is the match
            var b = new HBuilder("sumS"); uint p1 = b.Var(), p2 = b.Var(), l = b.Var(), r = b.Var(), r2 = b.Var(), res = b.Var();
            b.Redex(HBuilder.Ref(SumT), b.Con(p1, b.Con(l, b.Add(r2, res))));
            b.Redex(HBuilder.Ref(SumT), b.Con(p2, b.Con(r, r2)));
            defs[SumS] = b.Build(b.Con(b.Dup(p1, p2), b.Con(b.Con(l, r), res)));
        }
        {   // @sumZ = (t t): a leaf is its own sum
            var b = new HBuilder("sumZ"); var t = b.Var();
            defs[SumZ] = b.Build(b.Con(t, t));
        }
        return defs;
    }

    public static void Run(string[] args)
    {
        int n = args.Length > 0 ? int.Parse(args[0]) : 20;
        int reps = args.Length > 1 ? int.Parse(args[1]) : 5;
        int[]? only = args.Length > 2 ? args[2].Split(',').Select(int.Parse).ToArray() : null;   // e.g. "1" or "1,12"
        var book = Book();
        HWorker.Compiled = Environment.GetEnvironmentVariable("HVM_COMPILED") == "1";
        Console.WriteLine($"HVM2 port, n = {n}, {Environment.ProcessorCount} cores, best of {reps}");
        Console.WriteLine($"{"workload",-12}{"threads",8}{"ms",10}{"MIPS",10}{"interactions",14}");
        foreach (var work in new[] { "pow2", "tree" })
            foreach (int threads in (only ?? new[] { 1, 2, 4, 8, Environment.ProcessorCount }).Distinct().Where(t => t <= Environment.ProcessorCount))
            {
                double best = double.MaxValue;
                long inter = 0;
                for (int i = 0; i <= reps; i++)
                {
                    int cap = (int)Math.Min(1 << 29 - 1, (1L << n) * 16 + (1 << 20));
                    var net = new HNet(cap, book);
                    net.Node.AsSpan().Fill(0); net.Vars.AsSpan().Fill(0);   // fault the pages in before timing
                    uint main = (uint)cap - 1, root = H.P(H.VAR, main);
                    uint a, b;
                    if (work == "pow2")
                    {
                        // @pow2 ~ (#n root)
                        net.Node[main] = H.Pair(H.P(H.NUM, (uint)n), root);
                        (a, b) = (HBuilder.Ref(Pow2), H.P(H.CON, main));
                    }
                    else
                    {
                        // @build ~ (#n t) & @sumT ~ (#n (t root)): sumT consumes the tree while it is being built
                        uint t = H.P(H.VAR, main - 1);
                        net.Node[main] = H.Pair(H.P(H.NUM, (uint)n), t);
                        net.Node[main - 1] = H.Pair(t, root);
                        net.Node[main - 2] = H.Pair(H.P(H.NUM, (uint)n), H.P(H.CON, main - 1));
                        var w0 = new HWorker(net, 0, 1, false);
                        (a, b) = (HBuilder.Ref(SumT), H.P(H.CON, main - 2));
                        net.Vars[main - 1] = H.NONE;
                        // the build redex goes in first; Reduce links the sum redex
                        _ = w0;
                    }
                    var sw = Stopwatch.StartNew();
                    uint v = work == "pow2"
                        ? HScheduler.Reduce(net, root, a, b, threads, out inter)
                        : HScheduler.Reduce2(net, root, (HBuilder.Ref(Build), H.P(H.CON, main)), (a, b), threads, out inter);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (v != (1u << n)) throw new Exception($"{work}: got {v}, expected {1u << n}");
                    if (i > 0) best = Math.Min(best, ms);
                }
                Console.WriteLine($"{work,-12}{threads,8}{best,10:F1}{inter / best / 1000,10:F0}{inter,14:N0}");
            }
    }
}
