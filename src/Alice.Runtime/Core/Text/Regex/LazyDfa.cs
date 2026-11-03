using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Core.Text;

/// <summary>
/// Lazy DFA over code-unit classes, built from the NFA on demand and cached per thread (<see cref="Cache"/>).
/// A DFA state is the ordered list of NFA states (leftmost-first priority; lower-priority threads are cut once a
/// match is reached) plus the context of the previous unit, which `^ $ \A \z \b \B` need. Matches are reported one
/// unit late (as in Rust's regex-automata): the transition on unit i says whether a match ended at i, which is when
/// look-ahead assertions can be decided. The end-of-input column decides matches at the end of the haystack.
/// Forward DFAs find the end of the leftmost-first match; the reverse DFA (longest semantics, run backwards from
/// that end) finds its start.
/// </summary>
internal sealed class LazyDfa
{
    public const byte CtxOther = 0, CtxNewline = 1, CtxWord = 2, CtxEdge = 3;
    public const int NoMatch = -1, GaveUp = -2;
    const byte FMatch = 1, FDead = 2;
    const int MaxStates = 8192;

    public static byte CtxOfUnit(int u) => u == '\n' ? CtxNewline : RegexSyntax.IsAsciiWord(u) ? CtxWord : CtxOther;

    readonly Nfa nfa;
    readonly NState[] states;
    readonly bool longest;
    readonly ByteClasses bc;
    public readonly ushort[] Map;
    public readonly int Classes, Stride, Eoi;
    readonly int shift;   // Stride is 1 << shift: state index = entry >> shift (no division on the hot path)

    public LazyDfa(Nfa nfa, bool longest)
    {
        this.nfa = nfa;
        states = nfa.States;
        this.longest = longest;
        bc = new ByteClasses(nfa);
        Map = bc.Map;
        Classes = bc.Count;
        shift = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Classes);   // 2^shift > Classes
        Stride = 1 << shift;
        Eoi = Classes;
    }

    sealed class KeyComparer : IEqualityComparer<int[]>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals(int[]? a, int[]? b) => a.AsSpan().SequenceEqual(b);
        public int GetHashCode(int[] k)
        {
            var h = new HashCode();
            h.AddBytes(MemoryMarshal.AsBytes(k.AsSpan()));
            return h.ToHashCode();
        }
    }

    /// <summary>Mutable DFA state cache; one per thread at a time (see Regex's cache pool).</summary>
    public sealed class Cache
    {
        internal int[] Trans;
        internal byte[] Flags = new byte[64];
        internal byte[] Ctx = new byte[64];
        internal readonly List<int[]> Keys = [];
        internal readonly Dictionary<int[], int> Index = new(KeyComparer.Instance);
        internal int States;
        internal readonly int[] Starts = new int[8];
        internal readonly SparseSet Seen;
        internal int[] Stack = new int[64];
        internal readonly List<int> L2 = [], L4 = [];
        internal bool Overflow;
        internal int Resets;
        readonly int stride;

        internal Cache(LazyDfa d)
        {
            stride = d.Stride;
            Trans = new int[stride * 64];
            Seen = new SparseSet(d.states.Length);
            Reset();
        }

        internal void Reset()
        {
            Array.Clear(Trans);
            Keys.Clear();
            Index.Clear();
            Keys.Add([]);    // index 0: reserved (entry 0 = unknown transition)
            States = 1;
            Array.Clear(Starts);
            Overflow = false;
        }
    }

    public Cache NewCache() => new(this);

    // ---------------------------------------------------------------- start-state acceleration
    // The unanchored start state loops on most units for patterns such as `\p{Lu}\p{Ll}+`: the units that leave
    // it are found with a vectorized `IndexOfAny` (bytes) or `IndexOfAnyExcept` over the ASCII units that stay
    // (UTF-16; every non-ASCII unit stops the skip and is stepped normally). Built once per DFA, only when the
    // leaving units are rare in text, and only without look-arounds (one start state per search).

    sealed class Accel(SearchValues<byte>? leave8, SearchValues<char>? stay16)
    {
        public readonly SearchValues<byte>? Leave8 = leave8;
        public readonly SearchValues<char>? Stay16 = stay16;
    }

    static readonly Accel NoAccel = new(null, null);
    Accel? accel;
    const double MaxLeaveFreq = 0.15;

    // the accelerator for start entry s (a positive, unflagged entry) of cache c, or null
    Accel? AccelFor(Cache c, int s)
    {
        var a = Volatile.Read(ref accel);
        if (a != null) return a == NoAccel ? null : a;
        a = BuildAccel(c, s) ?? NoAccel;
        Volatile.Write(ref accel, a);
        return a == NoAccel ? null : a;
    }

    Accel? BuildAccel(Cache c, int s)
    {
        if (nfa.Reverse || nfa.HasLook) return null;
        var stay = new bool[Classes];
        for (int cls = 0; cls < Classes; cls++)
        {
            int nx = c.Trans[s + cls];
            if (nx == 0) { nx = Compute(c, s, cls); if (nx == 0) return null; }
            stay[cls] = nx == s;
        }
        double leave = 0;
        if (nfa.Utf8)
        {
            var units = new List<byte>();
            for (int b = 0; b < 256; b++)
                if (!stay[Map[b]]) { units.Add((byte)b); leave += Literals.Freq(b); }
            return leave <= MaxLeaveFreq && units.Count < 256 ? new Accel(SearchValues.Create(units.ToArray()), null) : null;
        }
        var stays = new List<char>();
        leave = 0.01;   // non-ASCII text
        for (int u = 0; u < 128; u++)
            if (stay[Map[u]]) stays.Add((char)u); else leave += Literals.Freq(u);
        return leave <= MaxLeaveFreq && stays.Count > 0 ? new Accel(null, SearchValues.Create(stays.ToArray())) : null;
    }

    // the first position >= i whose unit may leave the start state, or -1
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Skip<T>(Accel a, ReadOnlySpan<T> hay, int i) where T : unmanaged
    {
        int r = typeof(T) == typeof(byte)
            ? MemoryMarshal.Cast<T, byte>(hay)[i..].IndexOfAny(a.Leave8!)
            : MemoryMarshal.Cast<T, char>(hay)[i..].IndexOfAnyExcept(a.Stay16!);
        return r < 0 ? -1 : i + r;
    }

    /// <summary>True when the DFA keeps giving up on this thread (cache thrashing): callers use the Pike VM instead.</summary>
    public static bool Thrashing(Cache c) => c.Resets > 16;

    int AddState(Cache c, List<int> set, int ctx, bool matched)
    {
        var key = new int[set.Count + 1];
        key[0] = (matched ? 1 : 0) | (ctx << 1);
        for (int k = 0; k < set.Count; k++) key[k + 1] = set[k];
        if (c.Index.TryGetValue(key, out int idx)) return idx;
        if (c.States >= MaxStates) { c.Overflow = true; return -1; }
        idx = c.States++;
        if ((idx + 1) * Stride > c.Trans.Length) Array.Resize(ref c.Trans, Math.Max(c.Trans.Length * 2, (idx + 1) * Stride));
        if (idx >= c.Flags.Length) { Array.Resize(ref c.Flags, c.Flags.Length * 2); Array.Resize(ref c.Ctx, c.Ctx.Length * 2); }
        c.Keys.Add(key);
        c.Index[key] = idx;
        c.Flags[idx] = (byte)((matched ? FMatch : 0) | (set.Count == 0 ? FDead : 0));
        c.Ctx[idx] = (byte)ctx;
        return idx;
    }

    int Entry(Cache c, int idx) => c.Flags[idx] != 0 ? -(idx * Stride) : idx * Stride;

    static bool Eval(RegexAssertionKind k, byte tb, byte ta) => k switch
    {
        RegexAssertionKind.LineStart => tb == CtxEdge || (tb == CtxNewline && ta != CtxEdge),
        RegexAssertionKind.LineEnd => ta is CtxEdge or CtxNewline,
        RegexAssertionKind.TextStart => tb == CtxEdge,
        RegexAssertionKind.TextEnd => ta == CtxEdge,
        RegexAssertionKind.WordBoundary => (tb == CtxWord) != (ta == CtxWord),
        RegexAssertionKind.NotWordBoundary => (tb == CtxWord) == (ta == CtxWord),
        _ => false,
    };

    // DFS closure from s in priority order. evalLooks: decide assertions with (tb, ta) and follow them, Match sets
    // `matched`; otherwise keep assertions (and Match) pending in `list`. Returns true when leftmost-first must stop.
    bool Explore(Cache c, int start, bool evalLooks, byte tb, byte ta, List<int> list, ref bool matched)
    {
        int sp = 0;
        var stack = c.Stack;
        stack[sp++] = start;
        while (sp > 0)
        {
            int s = stack[--sp];
            if (!c.Seen.Insert(s)) continue;
            ref readonly var st = ref states[s];
            switch (st.Kind)
            {
                case NKind.Range:
                    list.Add(s);
                    break;
                case NKind.Match:
                    if (evalLooks) matched = true; else list.Add(s);
                    if (!longest) return true;
                    break;
                case NKind.Split:
                    {
                        var alts = st.Alts!;
                        if (sp + alts.Length > stack.Length) { Array.Resize(ref c.Stack, Math.Max(stack.Length * 2, sp + alts.Length)); stack = c.Stack; }
                        for (int k = alts.Length - 1; k >= 0; k--) stack[sp++] = alts[k];
                        break;
                    }
                case NKind.Save:
                    if (sp + 1 > stack.Length) { Array.Resize(ref c.Stack, stack.Length * 2); stack = c.Stack; }
                    stack[sp++] = st.Next;
                    break;
                case NKind.Look:
                    if (!evalLooks) list.Add(s);
                    else if (Eval(st.Look, tb, ta))
                    {
                        if (sp + 1 > stack.Length) { Array.Resize(ref c.Stack, stack.Length * 2); stack = c.Stack; }
                        stack[sp++] = st.Next;
                    }
                    break;
            }
        }
        return false;
    }

    // the transition from state id `from` on class `cls` (Eoi = end of input); 0 when the cache overflowed
    int Compute(Cache c, int from, int cls)
    {
        int fi = from >> shift;
        int[] key = c.Keys[fi];
        byte sctx = c.Ctx[fi];
        byte nctx = cls == Eoi ? CtxEdge : bc.Ctx[cls];
        byte tb = nfa.Reverse ? nctx : sctx, ta = nfa.Reverse ? sctx : nctx;
        var l2 = c.L2;
        l2.Clear();
        c.Seen.Clear();
        bool matched = false;
        for (int k = 1; k < key.Length; k++)
            if (Explore(c, key[k], true, tb, ta, l2, ref matched)) break;
        var l4 = c.L4;
        l4.Clear();
        c.Seen.Clear();
        if (cls != Eoi)
        {
            int u = bc.Rep[cls];
            bool dummy = false;
            foreach (int q in l2)
            {
                var tr = states[q].Trans!;
                foreach (var t in tr)
                    if (t.Lo <= u && u <= t.Hi && Explore(c, t.Next, false, 0, 0, l4, ref dummy)) goto done;
            }
        }
    done:
        int ctx = cls != Eoi && nfa.HasLook ? bc.Ctx[cls] : 0;
        int idx = AddState(c, l4, ctx, matched);
        if (idx < 0) return 0;
        int e = Entry(c, idx);
        c.Trans[from + cls] = e;
        return e;
    }

    int StartEntry(Cache c, bool anchored, byte ctx)
    {
        int slot = (anchored ? 4 : 0) + (nfa.HasLook ? ctx : 0);
        int e = c.Starts[slot];
        if (e != 0) return e;
        c.Seen.Clear();
        var l4 = c.L4;
        l4.Clear();
        bool m = false;
        Explore(c, anchored ? nfa.Start : nfa.StartUnanchored, false, 0, 0, l4, ref m);
        int idx = AddState(c, l4, nfa.HasLook ? ctx : 0, false);
        if (idx < 0) return 0;
        e = Entry(c, idx);
        c.Starts[slot] = e;
        return e;
    }

    int Fail(Cache c)
    {
        c.Reset();
        c.Resets++;
        return GaveUp;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Unit<T>(ref T r0, int i) where T : unmanaged =>
        typeof(T) == typeof(byte) ? Unsafe.Add(ref Unsafe.As<T, byte>(ref r0), i) : Unsafe.Add(ref Unsafe.As<T, char>(ref r0), i);

    // class of the unit at i: one table load for bytes and for UTF-16 units below 256
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int Cls<T>(ref ushort m0, ref T r0, int i) where T : unmanaged
    {
        if (typeof(T) == typeof(byte)) return Unsafe.Add(ref m0, Unsafe.Add(ref Unsafe.As<T, byte>(ref r0), i));
        int u = Unsafe.Add(ref Unsafe.As<T, char>(ref r0), i);
        return u < 256 ? Unsafe.Add(ref m0, u) : bc.High(u);
    }

    /// <summary>End of the leftmost-first match starting at or after <paramref name="pos"/> (exactly at pos when
    /// anchored); <paramref name="earliest"/> returns as soon as any match end is known. NoMatch or GaveUp.</summary>
    public int FindFwd<T>(ReadOnlySpan<T> hay, int pos, bool anchored, bool earliest, Cache c) where T : unmanaged
    {
        if (c.Overflow) c.Reset();
        int n = hay.Length;
        ref T h0 = ref MemoryMarshal.GetReference(hay);
        byte ctx = pos == 0 ? CtxEdge : CtxOfUnit(Unit(ref h0, pos - 1));
        int s = StartEntry(c, anchored, ctx);
        if (s == 0) return Fail(c);
        if (s < 0)
        {
            s = -s;
            if ((c.Flags[s >> shift] & FDead) != 0) return NoMatch;
        }
        int last = NoMatch;
        Accel? acc = null;
        int sAcc = -1;
        if (!anchored && (c.Flags[s >> shift] == 0) && n - pos >= 16 && (acc = AccelFor(c, s)) != null) sAcc = s;
        ref ushort m0 = ref MemoryMarshal.GetArrayDataReference(Map);
        ref int t0 = ref MemoryMarshal.GetArrayDataReference(c.Trans);
        int i = pos;
        while (true)
        {
        top:
            if (s == sAcc)
            {
                int r = Skip(acc!, hay, i);
                if (r < 0) { i = n; break; }
                i = r;
            }
            while (i + 3 < n)
            {
                int a = Unsafe.Add(ref t0, s + Cls(ref m0, ref h0, i));
                if (a <= 0) goto slow;
                int b = Unsafe.Add(ref t0, a + Cls(ref m0, ref h0, i + 1));
                if (b <= 0) { s = a; i += 1; goto slow; }
                int d = Unsafe.Add(ref t0, b + Cls(ref m0, ref h0, i + 2));
                if (d <= 0) { s = b; i += 2; goto slow; }
                int e = Unsafe.Add(ref t0, d + Cls(ref m0, ref h0, i + 3));
                if (e <= 0) { s = d; i += 3; goto slow; }
                s = e;
                i += 4;
                if (s == sAcc) goto top;
            }
            while (i < n)
            {
                int a = Unsafe.Add(ref t0, s + Cls(ref m0, ref h0, i));
                if (a <= 0) goto slow;
                s = a;
                i++;
            }
            break;
        slow:
            {
                int cls = Cls(ref m0, ref h0, i);
                int nx = Unsafe.Add(ref t0, s + cls);   // s + cls is inside Trans: s is an entry, cls < Stride
                if (nx == 0)
                {
                    nx = Compute(c, s, cls);
                    if (nx == 0) return Fail(c);
                    t0 = ref MemoryMarshal.GetArrayDataReference(c.Trans);
                }
                if (nx < 0)
                {
                    nx = -nx;
                    byte f = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.Flags), nx >> shift);
                    if ((f & FMatch) != 0) { last = i; if (earliest) return i; }
                    if ((f & FDead) != 0) return last;
                }
                s = nx;
                i++;
            }
        }
        int eo = c.Trans[s + Eoi];
        if (eo == 0) { eo = Compute(c, s, Eoi); if (eo == 0) return Fail(c); }
        if (eo < 0 && (c.Flags[(-eo) >> shift] & FMatch) != 0) last = n;
        return last;
    }

    /// <summary>Reverse DFA (longest): the smallest start >= <paramref name="minStart"/> of a match ending at
    /// <paramref name="end"/>. NoMatch or GaveUp.</summary>
    public int FindRev<T>(ReadOnlySpan<T> hay, int end, int minStart, Cache c) where T : unmanaged
    {
        if (c.Overflow) c.Reset();
        int n = hay.Length;
        ref T h0 = ref MemoryMarshal.GetReference(hay);
        byte ctx = end >= n ? CtxEdge : CtxOfUnit(Unit(ref h0, end));
        int s = StartEntry(c, true, ctx);
        if (s == 0) return Fail(c);
        if (s < 0)
        {
            s = -s;
            if ((c.Flags[s >> shift] & FDead) != 0) return NoMatch;
        }
        int last = NoMatch;
        ref ushort m0 = ref MemoryMarshal.GetArrayDataReference(Map);
        ref int t0 = ref MemoryMarshal.GetArrayDataReference(c.Trans);
        int stop = minStart > 0 ? minStart - 1 : 0;
        for (int i = end - 1; i >= stop; i--)
        {
            int cls = Cls(ref m0, ref h0, i);
            int nx = Unsafe.Add(ref t0, s + cls);
            if (nx <= 0)
            {
                if (nx == 0)
                {
                    nx = Compute(c, s, cls);
                    if (nx == 0) return Fail(c);
                    t0 = ref MemoryMarshal.GetArrayDataReference(c.Trans);
                }
                if (nx < 0)
                {
                    nx = -nx;
                    byte f = c.Flags[nx >> shift];
                    if ((f & FMatch) != 0) last = i + 1;
                    if ((f & FDead) != 0) return last;
                }
            }
            if (i < minStart) return last;   // the unit before minStart only decides a match at minStart
            s = nx;
        }
        if (minStart == 0)
        {
            int eo = c.Trans[s + Eoi];
            if (eo == 0) { eo = Compute(c, s, Eoi); if (eo == 0) return Fail(c); }
            if (eo < 0 && (c.Flags[(-eo) >> shift] & FMatch) != 0) last = 0;
        }
        return last;
    }
}

/// <summary>Sparse set of small ints with O(1) clear (Briggs-Torczon).</summary>
internal sealed class SparseSet
{
    readonly int[] dense, sparse;
    int count;

    public SparseSet(int capacity) { dense = new int[Math.Max(1, capacity)]; sparse = new int[Math.Max(1, capacity)]; }

    public int Count => count;
    public int this[int i] => dense[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(int v) { int i = sparse[v]; return i < count && dense[i] == v; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Insert(int v)
    {
        if (Contains(v)) return false;
        dense[count] = v;
        sparse[v] = count++;
        return true;
    }

    public void Clear() => count = 0;
}
