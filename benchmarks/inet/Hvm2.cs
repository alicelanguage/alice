using System.Runtime.CompilerServices;

namespace Bench.Inet;

/// <summary>
/// A .NET port of the HVM2 design (Taelin, "HVM2: A Parallel Evaluator for Interaction Combinators", sections 4-8):
/// 32-bit ports (3-bit tag, 29-bit value), a node is one 64-bit word holding its two auxiliary ports, wires are
/// var cells linked with one atomic exchange (section 5), all interaction rules of section 4 including the "safe
/// REF" copy on DUP ~ REF (4.4), and per-thread redex bags where a starving thread steals single redexes from the
/// old end (8.1). Compared with <see cref="Net"/> (the first prototype: 64-bit ports, chunk sharing through a
/// ConcurrentQueue), this halves memory traffic per node and removes the shared queue.
/// </summary>
public static class H
{
    public const uint VAR = 0, REF = 1, ERA = 2, NUM = 3, CON = 4, DUP = 5, OPR = 6, SWI = 7;
    public const uint NONE = 0;   // VAR 0: var cell 0 is never handed out

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint P(uint tag, uint val) => (val << 3) | tag;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Tag(uint p) => p & 7;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Val(uint p) => p >> 3;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong Pair(uint a, uint b) => a | (ulong)b << 32;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Fst(ulong n) => (uint)n;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Snd(ulong n) => (uint)(n >> 32);
}

/// <summary>A top-level definition: a static net copied on CALL. <c>Safe</c>: it has no DUP nodes (section 7.1).</summary>
public sealed record HDef(string Name, uint Root, ulong[] Nodes, int Vars, ulong[] Redexes, bool Safe);

public sealed class HNet(int capacity, HDef[] book)
{
    public readonly ulong[] Node = new ulong[capacity];
    public readonly uint[] Vars = new uint[capacity];
    public readonly HDef[] Book = book;
    public const uint Chunk = 1 << 12;
    long nextNodeChunk = 1, nextVarChunk = 1;   // index 0 is NONE; the last cells belong to main

    /// <summary>Claims a fresh run of <see cref="Chunk"/> cells with one fetch-add (BendRT 4.1: "claims fresh pages
    /// off one global counter"), so uneven work cannot exhaust a fixed per-thread region.</summary>
    public uint ClaimNodes() => Claim(ref nextNodeChunk);
    public uint ClaimVars() => Claim(ref nextVarChunk);

    uint Claim(ref long counter)
    {
        long start = Interlocked.Add(ref counter, Chunk) - Chunk;
        if (start + Chunk >= Node.Length - 4) throw new OutOfMemoryException("net heap full");
        return (uint)start;
    }
}

/// <summary>Chase-Lev work-stealing deque of redexes (64-bit pairs): the owner pushes/pops at the bottom, thieves
/// take the oldest redex at the top with one compare-and-swap.</summary>
public sealed class Deque(int log2)
{
    readonly ulong[] buf = new ulong[1 << log2];
    readonly long mask = (1 << log2) - 1;
    long top, bottom;

    public long Count => Volatile.Read(ref bottom) - Volatile.Read(ref top);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(ulong x)
    {
        long b = bottom;
        if (b - Volatile.Read(ref top) > mask) throw new InvalidOperationException("redex bag overflow");
        buf[b & mask] = x;
        Volatile.Write(ref bottom, b + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Pop()
    {
        long b = bottom - 1;
        Interlocked.Exchange(ref bottom, b);   // full fence: a thief must see the reservation before we read top
        long t = Volatile.Read(ref top);
        if (t > b) { bottom = t; return 0; }
        ulong x = buf[b & mask];
        if (t == b)
        {
            if (Interlocked.CompareExchange(ref top, t + 1, t) != t) x = 0;   // a thief took the last one
            bottom = t + 1;
        }
        return x;
    }

    public ulong Steal()
    {
        long t = Volatile.Read(ref top);
        Interlocked.MemoryBarrier();
        long b = Volatile.Read(ref bottom);
        if (t >= b) return 0;
        ulong x = buf[t & mask];
        return Interlocked.CompareExchange(ref top, t + 1, t) == t ? x : 0;
    }
}

/// <summary>One evaluator thread: its own bag, allocation region and free lists.</summary>
public sealed class HWorker
{
    readonly ulong[] node;
    readonly uint[] vars;
    readonly HDef[] book;
    readonly bool atomic;
    uint nextNode, endNode, nextVar, endVar;
    uint[] freeNode = new uint[1 << 16], freeVar = new uint[1 << 16];
    int nFreeNode, nFreeVar;
    public readonly Deque Bag;    // shared: thieves take its oldest redex
    ulong[] stack = new ulong[1 << 12];   // private: no atomics on the hot path (HVM2's local bag)
    int top;
    public long Interactions;
    public bool HasWork => top > 0 || Bag.Count > 0;

    readonly HNet net;

    public HWorker(HNet net, int index, int count, bool atomic)
    {
        this.net = net;
        node = net.Node; vars = net.Vars; book = net.Book; this.atomic = atomic;
        Bag = new Deque(16);   // depth-first reduction keeps O(depth) redexes pending; 64K is ample
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint AllocNode(ulong v)
    {
        if (nFreeNode == 0 && nextNode == endNode) { nextNode = net.ClaimNodes(); endNode = nextNode + HNet.Chunk; }
        uint loc = nFreeNode > 0 ? freeNode[--nFreeNode] : nextNode++;
        node[loc] = v;
        return loc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint AllocVar()
    {
        if (nFreeVar == 0 && nextVar == endVar) { nextVar = net.ClaimVars(); endVar = nextVar + HNet.Chunk; }
        uint v = nFreeVar > 0 ? freeVar[--nFreeVar] : nextVar++;
        vars[v] = H.NONE;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void FreeNode(uint loc)
    {
        if (nFreeNode == freeNode.Length) Array.Resize(ref freeNode, freeNode.Length * 2);
        freeNode[nFreeNode++] = loc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void FreeVar(uint v)
    {
        if (nFreeVar == freeVar.Length) Array.Resize(ref freeVar, freeVar.Length * 2);
        freeVar[nFreeVar++] = v;
    }

    /// <summary>Section 5, the atomic linker.</summary>
    public void Link(uint a, uint b)
    {
        while (true)
        {
            if (H.Tag(a) != H.VAR) (a, b) = (b, a);
            if (H.Tag(a) != H.VAR) { PushLocal(H.Pair(a, b)); return; }
            uint v = H.Val(a);
            uint got = atomic ? Interlocked.Exchange(ref vars[v], b) : Swap(ref vars[v], b);
            if (got == H.NONE) return;
            vars[v] = H.NONE;
            FreeVar(v);
            a = got;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Swap(ref uint cell, uint value) { uint old = cell; cell = value; return old; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void PushLocal(ulong r)
    {
        if (top == stack.Length) Array.Resize(ref stack, stack.Length * 2);
        stack[top++] = r;
    }

    public void Give(ulong r) => PushLocal(r);

    /// <summary>Runs up to <paramref name="budget"/> interactions from the private stack. When <paramref name="share"/>,
    /// it keeps the shared bag stocked with the oldest private redex (the root of the largest pending subtree).</summary>
    public void Run(int budget, bool share = false)
    {
        int bottom = 0;   // oldest private redex not yet offered
        while (budget-- > 0)
        {
            if (top == 0)
            {
                ulong s = Bag.Pop();
                if (s == 0) return;
                PushLocal(s);
            }
            if (share && (budget & 63) == 0 && top - bottom > 1 && Bag.Count < 2)
            {
                // offer the oldest local redex: move it to the shared bag and close the hole
                Bag.Push(stack[bottom]);
                stack[bottom] = stack[--top];
            }
            ulong r = stack[--top];
            Interact(H.Fst(r), H.Snd(r));
        }
    }

    /// <summary>Section 4.9's table.</summary>
    void Interact(uint a, uint b)
    {
        Interactions++;
        if (H.Tag(a) > H.Tag(b)) (a, b) = (b, a);
        uint ta = H.Tag(a), tb = H.Tag(b);
        if (ta == H.REF)
        {
            if (tb == H.DUP && book[H.Val(a)].Safe) { Copy(a, b); return; }   // 4.4: copy a function pointer
            if (tb >= H.CON)
            {
                // compiled definitions (what an HVM-style code generator emits per def), else the generic copier
                if (Compiled && H.Val(a) == 0) CallPow2(b);
                else if (Compiled && H.Val(a) == 1) CallPow2S(b);
                else Call(book[H.Val(a)], b);
            }
            return;   // REF ~ ERA/NUM/REF: void
        }
        if (tb < H.CON) return;   // void
        ulong bn = node[H.Val(b)];
        if (ta < H.CON)
        {
            if (ta == H.NUM && tb == H.OPR) { Oper(a, H.Val(b), H.Fst(bn), H.Snd(bn)); return; }
            if (ta == H.NUM && tb == H.SWI) { Swit(a, H.Val(b), H.Fst(bn), H.Snd(bn)); return; }
            Copy(a, b);   // erase (ERA), or copy (NUM)
            return;
        }
        ulong an = node[H.Val(a)];
        if (ta == tb)
        {
            FreeNode(H.Val(a)); FreeNode(H.Val(b));
            Link(H.Fst(an), H.Fst(bn)); Link(H.Snd(an), H.Snd(bn));
            return;
        }
        // commute: the two consumed nodes are reused for two of the four copies
        uint v0 = H.P(H.VAR, AllocVar()), v1 = H.P(H.VAR, AllocVar()), v2 = H.P(H.VAR, AllocVar()), v3 = H.P(H.VAR, AllocVar());
        uint al = H.Val(a), bl = H.Val(b);
        node[bl] = H.Pair(v0, v1);
        uint by = AllocNode(H.Pair(v2, v3));
        node[al] = H.Pair(v0, v2);
        uint ay = AllocNode(H.Pair(v1, v3));
        Link(H.Fst(an), H.P(tb, bl)); Link(H.Snd(an), H.P(tb, by));
        Link(H.Fst(bn), H.P(ta, al)); Link(H.Snd(bn), H.P(ta, ay));
    }

    void Copy(uint nullary, uint bin)
    {
        ulong n = node[H.Val(bin)];
        FreeNode(H.Val(bin));
        Link(H.Fst(n), nullary); Link(H.Snd(n), nullary);
    }

    // 4.7 with one operator (add), as in the first prototype
    void Oper(uint num, uint loc, uint x, uint outp)
    {
        if (H.Tag(x) == H.NUM) { FreeNode(loc); Link(outp, H.P(H.NUM, (H.Val(num) + H.Val(x)) & 0x1FFFFFFF)); return; }
        node[loc] = H.Pair(num, outp);
        Link(x, H.P(H.OPR, loc));
    }

    // 4.8: #0 ~ ?(A B) => A ~ (B *);  #n+1 ~ ?(A B) => A ~ (* (#n B))
    void Swit(uint num, uint loc, uint branches, uint outp)
    {
        uint v = H.Val(num);
        if (v == 0) { node[loc] = H.Pair(outp, H.P(H.ERA, 0)); Link(branches, H.P(H.CON, loc)); return; }
        node[loc] = H.Pair(H.P(H.NUM, v - 1), outp);
        uint outer = AllocNode(H.Pair(H.P(H.ERA, 0), H.P(H.CON, loc)));
        Link(branches, H.P(H.CON, outer));
    }

    public static bool Compiled;

    // @pow2 = (?((#1 @pow2S) r) r): 3 nodes, 1 var, root linked to the caller
    void CallPow2(uint caller)
    {
        uint r = H.P(H.VAR, AllocVar());
        uint pair = AllocNode(H.Pair(H.P(H.NUM, 1), H.P(H.REF, 1)));
        uint swi = AllocNode(H.Pair(H.P(H.CON, pair), r));
        uint root = AllocNode(H.Pair(H.P(H.SWI, swi), r));
        Link(H.P(H.CON, root), caller);
    }

    // @pow2S = ({p1 p2} R) & @pow2 ~ (p1 $(r2 R)) & @pow2 ~ (p2 r2)
    void CallPow2S(uint caller)
    {
        uint p1 = H.P(H.VAR, AllocVar()), p2 = H.P(H.VAR, AllocVar()), r2 = H.P(H.VAR, AllocVar()), res = H.P(H.VAR, AllocVar());
        uint add = AllocNode(H.Pair(r2, res));
        uint c1 = AllocNode(H.Pair(p1, H.P(H.OPR, add)));
        uint c2 = AllocNode(H.Pair(p2, r2));
        uint dup = AllocNode(H.Pair(p1, p2));
        uint root = AllocNode(H.Pair(H.P(H.DUP, dup), res));
        PushLocal(H.Pair(H.P(H.REF, 0), H.P(H.CON, c1)));   // both ends principal: straight to the bag
        PushLocal(H.Pair(H.P(H.REF, 0), H.P(H.CON, c2)));
        Link(H.P(H.CON, root), caller);
    }

    /// <summary>4.2: copy the definition's nodes with fresh addresses and vars.</summary>
    void Call(HDef d, uint caller)
    {
        Span<uint> nm = stackalloc uint[d.Nodes.Length];
        Span<uint> vm = stackalloc uint[d.Vars];
        for (int i = 0; i < vm.Length; i++) vm[i] = AllocVar();
        for (int i = 0; i < nm.Length; i++) nm[i] = AllocNode(0);
        for (int i = 0; i < nm.Length; i++)
            node[nm[i]] = H.Pair(Adj(H.Fst(d.Nodes[i]), nm, vm), Adj(H.Snd(d.Nodes[i]), nm, vm));
        foreach (var r in d.Redexes) Link(Adj(H.Fst(r), nm, vm), Adj(H.Snd(r), nm, vm));
        Link(Adj(d.Root, nm, vm), caller);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Adj(uint p, Span<uint> nm, Span<uint> vm)
    {
        uint t = H.Tag(p);
        if (t == H.VAR) return H.P(H.VAR, vm[(int)H.Val(p)]);
        if (t >= H.CON) return H.P(t, nm[(int)H.Val(p)]);
        return p;
    }
}

/// <summary>Builds a definition from trees; constructors return the port that points at the new node.</summary>
public sealed class HBuilder(string name)
{
    readonly List<ulong> nodes = [];
    readonly List<ulong> redexes = [];
    int vars;
    bool dups;

    public uint Var() => H.P(H.VAR, (uint)vars++);
    public static uint Num(uint n) => H.P(H.NUM, n);
    public static uint Era => H.P(H.ERA, 0);
    public static uint Ref(int def) => H.P(H.REF, (uint)def);
    uint Node(uint tag, uint a, uint b) { nodes.Add(H.Pair(a, b)); return H.P(tag, (uint)nodes.Count - 1); }
    public uint Con(uint a, uint b) => Node(H.CON, a, b);
    public uint Dup(uint a, uint b) { dups = true; return Node(H.DUP, a, b); }
    public uint Add(uint other, uint result) => Node(H.OPR, other, result);
    public uint Swi(uint zeroSucc, uint result) => Node(H.SWI, zeroSucc, result);
    public void Redex(uint a, uint b) => redexes.Add(H.Pair(a, b));
    public HDef Build(uint root) => new(name, root, [.. nodes], vars, [.. redexes], !dups);
}

/// <summary>Section 8: every thread drains its own bag and steals the oldest redex of a neighbor when starving.</summary>
public static class HScheduler
{
    public static uint Reduce(HNet net, uint root, uint mainA, uint mainB, int threads, out long interactions) =>
        Reduce2(net, root, (mainA, mainB), null, threads, out interactions);

    /// <summary>Reduces a net whose main body is one or two redexes.</summary>
    public static uint Reduce2(HNet net, uint root, (uint, uint) first, (uint, uint)? second, int threads, out long interactions)
    {
        var ws = Enumerable.Range(0, threads).Select(i => new HWorker(net, i, threads, threads > 1)).ToArray();
        ws[0].Link(first.Item1, first.Item2);
        if (second is { } s2) ws[0].Link(s2.Item1, s2.Item2);
        if (threads == 1)
        {
            while (ws[0].HasWork) ws[0].Run(int.MaxValue);
            interactions = ws[0].Interactions;
            return Read(net, root);
        }
        int busy = threads;   // a worker counts as busy while it may hold or be acquiring work
        var ts = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int me = t;
            ts[t] = new Thread(() =>
            {
                var w = ws[me];
                var rng = new Random(me * 7919 + 1);
                bool counted = true;
                var spin = new SpinWait();
                while (true)
                {
                    w.Run(1 << 12, share: true);
                    if (w.HasWork) continue;
                    // starving: leave the busy count, then try to steal (re-entering it first, so a stolen redex
                    // is never held while busy reads 0)
                    if (counted) { Interlocked.Decrement(ref busy); counted = false; }
                    if (Volatile.Read(ref busy) == 0) return;
                    Interlocked.Increment(ref busy);
                    ulong got = 0;
                    int start = rng.Next(threads);
                    for (int k = 0; k < threads && got == 0; k++)
                    {
                        int v = (start + k) % threads;
                        if (v != me) got = ws[v].Bag.Steal();
                    }
                    if (got != 0) { counted = true; spin.Reset(); w.Give(got); continue; }
                    Interlocked.Decrement(ref busy);
                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }) { IsBackground = true };
            ts[t].Start();
        }
        foreach (var th in ts) th.Join();
        interactions = ws.Sum(w => w.Interactions);
        return Read(net, root);
    }

    static uint Read(HNet net, uint root)
    {
        uint p = net.Vars[H.Val(root)];
        while (H.Tag(p) == H.VAR && p != H.NONE) p = net.Vars[H.Val(p)];
        if (H.Tag(p) != H.NUM) throw new InvalidOperationException($"net did not reduce to a number (tag {H.Tag(p)})");
        return H.Val(p);
    }
}
