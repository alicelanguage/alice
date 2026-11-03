using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Core.Text;

/// <summary>
/// Pike VM: simulates the NFA with one thread per NFA state, in priority order (leftmost-first), carrying capture
/// slots. Linear in the haystack. Used for captures (on the slice the DFA found) and whenever the DFA cannot be
/// used (`\Z`, `\G`, a thrashing cache).
/// </summary>
internal sealed class PikeVm(Nfa nfa)
{
    readonly NState[] states = nfa.States;
    readonly int slotCount = nfa.SlotCount;

    public sealed class Cache
    {
        internal readonly SparseSet Curr, Next;
        internal int[] CurrSlots, NextSlots;
        internal int[] Stack = new int[128];
        internal readonly int[] Work;

        internal Cache(PikeVm vm)
        {
            int n = vm.states.Length;
            Curr = new SparseSet(n);
            Next = new SparseSet(n);
            CurrSlots = new int[n * vm.slotCount];
            NextSlots = new int[n * vm.slotCount];
            Work = new int[vm.slotCount];
        }
    }

    public Cache NewCache() => new(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Unit<T>(ReadOnlySpan<T> h, int i) where T : unmanaged =>
        typeof(T) == typeof(byte) ? Unsafe.As<T, byte>(ref Unsafe.AsRef(in h[i])) : Unsafe.As<T, char>(ref Unsafe.AsRef(in h[i]));

    internal static bool Look<T>(RegexAssertionKind k, ReadOnlySpan<T> h, int at, int searchStart) where T : unmanaged
    {
        int n = h.Length;
        switch (k)
        {
            case RegexAssertionKind.LineStart: return at == 0 || (Unit(h, at - 1) == '\n' && at < n);
            case RegexAssertionKind.LineEnd: return at == n || Unit(h, at) == '\n';
            case RegexAssertionKind.TextStart: return at == 0;
            case RegexAssertionKind.TextEnd: return at == n;
            case RegexAssertionKind.TextEndOrFinalNewline: return at == n || (at == n - 1 && Unit(h, at) == '\n');
            case RegexAssertionKind.SearchStart: return at == searchStart;
            case RegexAssertionKind.WordBoundary:
            case RegexAssertionKind.NotWordBoundary:
                {
                    bool a = at > 0 && RegexSyntax.IsAsciiWord(Unit(h, at - 1));
                    bool b = at < n && RegexSyntax.IsAsciiWord(Unit(h, at));
                    return (a != b) == (k == RegexAssertionKind.WordBoundary);
                }
        }
        return false;
    }

    /// <summary>Leftmost-first search from <paramref name="start"/>; fills <paramref name="slots"/> (2 per group,
    /// -1 when unset) and returns true on a match. <paramref name="limit"/> bounds the match end (the DFA's end).</summary>
    public bool Search<T>(ReadOnlySpan<T> h, int start, int limit, bool anchored, Span<int> slots, Cache c) where T : unmanaged
    {
        var curr = c.Curr;
        var next = c.Next;
        curr.Clear();
        next.Clear();
        bool matched = false;
        int sc = slotCount;
        for (int at = start; ; at++)
        {
            if (!matched && (!anchored || at == start))
            {
                c.Work.AsSpan().Fill(-1);
                Epsilon(c, curr, c.CurrSlots, nfa.Start, at, h, start);
            }
            if (curr.Count == 0) break;
            int u = at < limit ? Unit(h, at) : -1;
            for (int k = 0; k < curr.Count; k++)
            {
                int sid = curr[k];
                ref readonly var st = ref states[sid];
                if (st.Kind == NKind.Match)
                {
                    c.CurrSlots.AsSpan(sid * sc, Math.Min(sc, slots.Length)).CopyTo(slots);
                    matched = true;
                    break;   // lower-priority threads are cut
                }
                if (st.Kind == NKind.Range && u >= 0)
                {
                    foreach (var t in st.Trans!)
                        if (t.Lo <= u && u <= t.Hi)
                        {
                            c.CurrSlots.AsSpan(sid * sc, sc).CopyTo(c.Work);
                            Epsilon(c, next, c.NextSlots, t.Next, at + 1, h, start);
                        }
                }
            }
            if (at >= limit) break;
            (c.CurrSlots, c.NextSlots) = (c.NextSlots, c.CurrSlots);
            (curr, next) = (next, curr);
            next.Clear();
        }
        // keep Curr/Next identities stable for the next call
        if (!ReferenceEquals(curr, c.Curr)) { (c.CurrSlots, c.NextSlots) = (c.NextSlots, c.CurrSlots); }
        return matched;
    }

    // DFS epsilon closure from sid at position `at`, with c.Work as the incoming slots; restores slots on return.
    void Epsilon<T>(Cache c, SparseSet set, int[] table, int sid, int at, ReadOnlySpan<T> h, int searchStart) where T : unmanaged
    {
        int sc = slotCount;
        var work = c.Work;
        int sp = 0;
        Push(c, ref sp, sid, int.MinValue);
        while (sp > 0)
        {
            sp -= 2;
            int s = c.Stack[sp], restore = c.Stack[sp + 1];
            if (s < 0) { work[-s - 1] = restore; continue; }   // restore frame
            while (true)
            {
                if (!set.Insert(s)) break;
                ref readonly var st = ref states[s];
                switch (st.Kind)
                {
                    case NKind.Range:
                    case NKind.Match:
                        work.AsSpan().CopyTo(table.AsSpan(s * sc, sc));
                        goto next;
                    case NKind.Split:
                        {
                            var alts = st.Alts!;
                            for (int k = alts.Length - 1; k >= 1; k--) Push(c, ref sp, alts[k], int.MinValue);
                            s = alts[0];
                            continue;
                        }
                    case NKind.Save:
                        if (st.Slot < sc)
                        {
                            Push(c, ref sp, -st.Slot - 1, work[st.Slot]);
                            work[st.Slot] = at;
                        }
                        s = st.Next;
                        continue;
                    case NKind.Look:
                        if (!Look(st.Look, h, at, searchStart)) goto next;
                        s = st.Next;
                        continue;
                    default:
                        goto next;
                }
            }
        next:;
        }
    }

    static void Push(Cache c, ref int sp, int a, int b)
    {
        if (sp + 2 > c.Stack.Length) Array.Resize(ref c.Stack, c.Stack.Length * 2);
        c.Stack[sp] = a;
        c.Stack[sp + 1] = b;
        sp += 2;
    }
}
