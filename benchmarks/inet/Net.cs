using System.Runtime.CompilerServices;

namespace Bench.Inet;

/// <summary>
/// An HVM2-style interaction-net heap. A port is one ulong: 3 tag bits, 61 value bits (node address, var address,
/// number, or definition id). Binary nodes keep their two auxiliary ports in <see cref="Nodes"/>; a node's
/// principal port is the port value that points at it. A wire between two auxiliary ports is a var cell: both
/// ends hold VAR(v), and the first end to be linked leaves its partner in the cell for the second (atomic exchange).
/// </summary>
public static class Tag
{
    public const ulong Var = 0, Ref = 1, Era = 2, Num = 3, Con = 4, Dup = 5, Opr = 6, Swi = 7;
}

public readonly record struct Def(string Name, ulong Root, ulong[] Nodes, int Vars, (ulong A, ulong B)[] Redexes);

public sealed class Net
{
    public const ulong None = 0;   // VAR(0): var cell 0 is never handed out, so this means "empty"

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong P(ulong tag, ulong val) => (val << 3) | tag;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong TagOf(ulong p) => p & 7;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong ValOf(ulong p) => p >> 3;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool Binary(ulong p) => (p & 7) >= Tag.Con;

    public readonly ulong[] Nodes;   // 2 slots per node
    public readonly ulong[] Vars;
    public readonly Def[] Defs;

    public Net(int capacity, Def[] defs)
    {
        Nodes = GC.AllocateUninitializedArray<ulong>(capacity * 2);
        Vars = new ulong[capacity];
        Defs = defs;
    }
}

/// <summary>One reducer thread: its own redex stack, allocation region and free lists, so the hot path is unshared.</summary>
public sealed class Worker
{
    readonly Net net;
    readonly ulong[] nodes, vars;
    readonly Def[] defs;
    ulong nextNode, endNode, nextVar, endVar;
    ulong[] freeNodes = new ulong[1 << 16], freeVars = new ulong[1 << 16];
    int nFreeNodes, nFreeVars;

    public ulong[] Stack = new ulong[1 << 16];   // redex pairs
    public int Top;                               // in ulongs (2 per redex)
    public long Interactions;
    readonly bool atomic;

    public Worker(Net net, int index, int count, bool atomic)
    {
        this.net = net;
        nodes = net.Nodes; vars = net.Vars; defs = net.Defs;
        ulong nodeCap = (ulong)net.Nodes.Length / 2, varCap = (ulong)net.Vars.Length;
        nextNode = nodeCap / (ulong)count * (ulong)index; endNode = nodeCap / (ulong)count * (ulong)(index + 1);
        nextVar = Math.Max(1, varCap / (ulong)count * (ulong)index); endVar = varCap / (ulong)count * (ulong)(index + 1);
        this.atomic = atomic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ulong AllocNode(ulong a, ulong b)
    {
        ulong loc = nFreeNodes > 0 ? freeNodes[--nFreeNodes] : nextNode < endNode ? nextNode++ : throw new OutOfMemoryException("net node region full");
        nodes[loc * 2] = a; nodes[loc * 2 + 1] = b;
        return loc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ulong AllocVar()
    {
        ulong v = nFreeVars > 0 ? freeVars[--nFreeVars] : nextVar < endVar ? nextVar++ : throw new OutOfMemoryException("net var region full");
        vars[v] = Net.None;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void FreeNode(ulong loc)
    {
        if (nFreeNodes == freeNodes.Length) Array.Resize(ref freeNodes, freeNodes.Length * 2);
        freeNodes[nFreeNodes++] = loc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void FreeVar(ulong v)
    {
        if (nFreeVars == freeVars.Length) Array.Resize(ref freeVars, freeVars.Length * 2);
        freeVars[nFreeVars++] = v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(ulong a, ulong b)
    {
        if (Top + 2 > Stack.Length) Array.Resize(ref Stack, Stack.Length * 2);
        Stack[Top] = a; Stack[Top + 1] = b; Top += 2;
    }

    /// <summary>Connects two ports. Two principal ports make a redex; a var end either parks its partner in the
    /// cell or, when the other end got there first, takes what it left and keeps linking.</summary>
    public void Link(ulong a, ulong b)
    {
        while (true)
        {
            if (Net.TagOf(a) != Tag.Var) (a, b) = (b, a);
            if (Net.TagOf(a) != Tag.Var) { Push(a, b); return; }
            ulong v = Net.ValOf(a);
            ulong got = atomic ? Interlocked.Exchange(ref vars[v], b) : Swap(ref vars[v], b);
            if (got == Net.None) return;
            vars[v] = Net.None;
            FreeVar(v);
            a = got;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Swap(ref ulong cell, ulong value) { ulong old = cell; cell = value; return old; }

    /// <summary>Runs local redexes until the stack is empty (or <paramref name="budget"/> interactions).</summary>
    public void Run(long budget = long.MaxValue)
    {
        while (Top > 0 && budget-- > 0)
        {
            Top -= 2;
            Interact(Stack[Top], Stack[Top + 1]);
        }
    }

    void Interact(ulong a, ulong b)
    {
        Interactions++;
        if (Net.TagOf(a) > Net.TagOf(b)) (a, b) = (b, a);
        ulong ta = Net.TagOf(a), tb = Net.TagOf(b);
        if (ta == Tag.Ref)
        {
            if (Net.Binary(b)) Expand(defs[Net.ValOf(a)], b);   // CALL; REF ~ ERA/NUM/REF is void
            return;
        }
        if (!Net.Binary(b)) return;   // ERA/NUM ~ ERA/NUM: void
        ulong bl = Net.ValOf(b), b1 = nodes[bl * 2], b2 = nodes[bl * 2 + 1];
        if (!Net.Binary(a))
        {
            if (ta == Tag.Num && tb == Tag.Opr) { Oper(a, bl, b1, b2); return; }
            if (ta == Tag.Num && tb == Tag.Swi) { Switch(a, bl, b1, b2); return; }
            FreeNode(bl);   // ERAS: the nullary copies itself into both aux ports
            Link(b1, a); Link(b2, a);
            return;
        }
        ulong al = Net.ValOf(a), a1 = nodes[al * 2], a2 = nodes[al * 2 + 1];
        if (ta == tb)
        {
            FreeNode(al); FreeNode(bl);   // ANNI
            Link(a1, b1); Link(a2, b2);
            return;
        }
        // COMM: each node copies across the other; the two consumed nodes are reused as two of the four copies
        ulong v0 = Net.P(Tag.Var, AllocVar()), v1 = Net.P(Tag.Var, AllocVar()), v2 = Net.P(Tag.Var, AllocVar()), v3 = Net.P(Tag.Var, AllocVar());
        nodes[al * 2] = v0; nodes[al * 2 + 1] = v2;   // Ax (tag a) at al
        nodes[bl * 2] = v0; nodes[bl * 2 + 1] = v1;   // Bx (tag b) at bl
        ulong by = AllocNode(v2, v3), ay = AllocNode(v1, v3);
        Link(a1, Net.P(tb, bl)); Link(a2, Net.P(tb, by));
        Link(b1, Net.P(ta, al)); Link(b2, Net.P(ta, ay));
    }

    // NUM n ~ OPR(x, out): when x is already a number the sum goes to out; otherwise the node waits for x with n stored
    void Oper(ulong n, ulong loc, ulong x, ulong outp)
    {
        if (Net.TagOf(x) == Tag.Num)
        {
            FreeNode(loc);
            Link(outp, Net.P(Tag.Num, Net.ValOf(n) + Net.ValOf(x)));
            return;
        }
        nodes[loc * 2] = n;
        Link(x, Net.P(Tag.Opr, loc));
    }

    // NUM n ~ SWI(branches, out): 0 feeds (out ERA) to the (zero succ) pair, n > 0 feeds (ERA (n-1 out))
    void Switch(ulong n, ulong loc, ulong branches, ulong outp)
    {
        ulong v = Net.ValOf(n);
        if (v == 0)
        {
            nodes[loc * 2] = outp; nodes[loc * 2 + 1] = Net.P(Tag.Era, 0);
            Link(branches, Net.P(Tag.Con, loc));
            return;
        }
        nodes[loc * 2] = Net.P(Tag.Num, v - 1); nodes[loc * 2 + 1] = outp;
        ulong outer = AllocNode(Net.P(Tag.Era, 0), Net.P(Tag.Con, loc));
        Link(branches, Net.P(Tag.Con, outer));
    }

    /// <summary>CALL: copies a definition with fresh nodes and vars, links its root with the caller.</summary>
    void Expand(in Def d, ulong caller)
    {
        Span<ulong> nodeMap = stackalloc ulong[d.Nodes.Length / 2];
        Span<ulong> varMap = stackalloc ulong[d.Vars];
        for (int i = 0; i < varMap.Length; i++) varMap[i] = AllocVar();
        for (int i = 0; i < nodeMap.Length; i++) nodeMap[i] = AllocNode(0, 0);
        for (int i = 0; i < nodeMap.Length; i++)
        {
            ulong loc = nodeMap[i];
            nodes[loc * 2] = Adjust(d.Nodes[i * 2], nodeMap, varMap);
            nodes[loc * 2 + 1] = Adjust(d.Nodes[i * 2 + 1], nodeMap, varMap);
        }
        foreach (var (ra, rb) in d.Redexes) Link(Adjust(ra, nodeMap, varMap), Adjust(rb, nodeMap, varMap));
        Link(Adjust(d.Root, nodeMap, varMap), caller);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Adjust(ulong p, Span<ulong> nodeMap, Span<ulong> varMap)
    {
        ulong t = Net.TagOf(p);
        if (t == Tag.Var) return Net.P(Tag.Var, varMap[(int)Net.ValOf(p)]);
        if (t >= Tag.Con) return Net.P(t, nodeMap[(int)Net.ValOf(p)]);
        return p;
    }
}
