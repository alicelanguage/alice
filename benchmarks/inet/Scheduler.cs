namespace Bench.Inet;

/// <summary>Builds a definition: node constructors return the port that points at the new node's principal.</summary>
public sealed class DefBuilder(string name)
{
    readonly List<ulong> nodes = [];
    readonly List<(ulong, ulong)> redexes = [];
    int vars;

    public ulong Var() => Net.P(Tag.Var, (ulong)vars++);
    public static ulong Num(ulong n) => Net.P(Tag.Num, n);
    public static ulong Era => Net.P(Tag.Era, 0);
    public static ulong Ref(int def) => Net.P(Tag.Ref, (ulong)def);
    ulong Node(ulong tag, ulong a, ulong b) { nodes.Add(a); nodes.Add(b); return Net.P(tag, (ulong)(nodes.Count / 2 - 1)); }
    public ulong Con(ulong a, ulong b) => Node(Tag.Con, a, b);
    public ulong Dup(ulong a, ulong b) => Node(Tag.Dup, a, b);
    public ulong Add(ulong other, ulong result) => Node(Tag.Opr, other, result);
    public ulong Swi(ulong zeroSucc, ulong result) => Node(Tag.Swi, zeroSucc, result);
    public void Redex(ulong a, ulong b) => redexes.Add((a, b));
    public Def Build(ulong root) => new(name, root, [.. nodes], vars, [.. redexes]);
}

/// <summary>
/// Parallel reduction. Each worker runs its own redex stack depth-first; when the shared queue runs low it gives
/// away the bottom half of its stack (the shallowest redexes, the largest subtrees). Termination: a counter of
/// outstanding chunks, held by a worker from taking a chunk until its stack is empty again.
/// </summary>
public static class Scheduler
{
    public static long Reduce(Net net, ulong rootVar, (ulong, ulong) start, int threads, out long interactions)
    {
        var workers = Enumerable.Range(0, threads).Select(i => new Worker(net, i, threads, atomic: threads > 1)).ToArray();
        if (threads == 1)
        {
            workers[0].Link(start.Item1, start.Item2);
            workers[0].Run();
            interactions = workers[0].Interactions;
            return Read(net, rootVar);
        }
        var queue = new System.Collections.Concurrent.ConcurrentQueue<ulong[]>();
        long outstanding = 1, queued = 0;
        workers[0].Link(start.Item1, start.Item2);
        var ts = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            var w = workers[t];
            bool holding = t == 0;
            ts[t] = new Thread(() =>
            {
                var spin = new SpinWait();
                while (true)
                {
                    if (!holding)
                    {
                        if (Volatile.Read(ref outstanding) == 0) return;
                        if (!queue.TryDequeue(out var chunk)) { spin.SpinOnce(sleep1Threshold: -1); continue; }
                        Interlocked.Decrement(ref queued);
                        spin.Reset();
                        for (int i = 0; i < chunk.Length; i += 2) w.Push(chunk[i], chunk[i + 1]);
                        holding = true;
                    }
                    while (w.Top > 0)
                    {
                        w.Run(256);
                        if (w.Top >= 8 && Volatile.Read(ref queued) < threads)
                        {
                            int half = (w.Top / 4) * 2;   // bottom half, whole pairs
                            var give = w.Stack.AsSpan(0, half).ToArray();
                            Array.Copy(w.Stack, half, w.Stack, 0, w.Top - half);
                            w.Top -= half;
                            Interlocked.Increment(ref outstanding);
                            Interlocked.Increment(ref queued);
                            queue.Enqueue(give);
                        }
                    }
                    holding = false;
                    Interlocked.Decrement(ref outstanding);
                }
            }) { IsBackground = true };
            ts[t].Start();
        }
        foreach (var th in ts) th.Join();
        interactions = workers.Sum(w => w.Interactions);
        return Read(net, rootVar);
    }

    static long Read(Net net, ulong rootVar)
    {
        ulong p = net.Vars[Net.ValOf(rootVar)];
        while (Net.TagOf(p) == Tag.Var && p != Net.None) p = net.Vars[Net.ValOf(p)];
        if (Net.TagOf(p) != Tag.Num) throw new InvalidOperationException($"net did not reduce to a number (tag {Net.TagOf(p)})");
        return (long)Net.ValOf(p);
    }
}
