using System.Runtime.CompilerServices;

namespace Alice.Core.Text;

/// <summary>
/// One encoding's matcher for a pattern (UTF-8 bytes or UTF-16 units). Tier R: prefilter -> forward lazy DFA (end)
/// -> reverse lazy DFA (start) -> Pike VM on the match for captures; the Pike VM alone for `\Z`/`\G` or when the
/// DFA cache thrashes. Tier B: the backtracker. Mutable caches are pooled (one Interlocked exchange per call).
/// </summary>
internal sealed class Engine<T> where T : unmanaged
{
    readonly bool tierB, pikeOnly, anchoredStart;
    readonly LazyDfa? dfa, rdfa;
    readonly PikeVm? pike;
    readonly Backtracker? bt;
    readonly Prefilter<T>? pf;
    public readonly int SlotCount;
    Caches? pooled;

    internal sealed class Caches
    {
        internal LazyDfa.Cache? D, R;
        internal PikeVm.Cache? P;
        internal Backtracker.Cache? B;
        internal int[] Slots = [];
    }

    /// <summary>Tier R without a start anchor, a prefilter or `\Z`, and never empty: the forward DFA alone gives
    /// the end of each match, so callers that only need ends (Count) skip the reverse DFA.</summary>
    public readonly bool EndOnly;

    public bool IsBacktracking => tierB;
    public bool HasPrefilter => pf != null;
    public string PrefilterKind => pf?.GetType().Name ?? "none";

    public Engine(RegexPattern p, bool forceBacktracking)
    {
        bool utf8 = typeof(T) == typeof(byte);
        SlotCount = 2 * (p.CaptureCount + 1);
        tierB = forceBacktracking || p.Tier == RegexTier.Backtracking || RegexSyntax.HasNullableLoop(p.Root);
        anchoredStart = StartsWithTextStart(p.Root);
        if (!tierB)
        {
            try
            {
                var fwd = Nfa.Compile(p, utf8, false);
                pike = new PikeVm(fwd);
                pikeOnly = fwd.DfaUnsupported;
                if (!pikeOnly)
                {
                    dfa = new LazyDfa(fwd, longest: false);
                    rdfa = new LazyDfa(Nfa.Compile(p, utf8, true), longest: true);
                }
            }
            catch (NfaTooBigException) { tierB = true; pike = null; dfa = rdfa = null; }
        }
        if (tierB) bt = new Backtracker(p);
        pf = Prefilter.Build<T>(p, allowExact: !tierB && !pikeOnly);
        EndOnly = !tierB && !pikeOnly && !anchoredStart && pf == null && !RegexSyntax.Nullable(p.Root);
    }

    /// <summary>The end of the leftmost-first match at or after pos (requires <see cref="EndOnly"/>).</summary>
    public bool FindEnd(ReadOnlySpan<T> h, int pos, out int e, Caches c)
    {
        e = -1;
        if (pos > h.Length) return false;
        if (LazyDfa.Thrashing(c.D!)) return Find(h, pos, out _, out e, c);
        int end = dfa!.FindFwd(h, pos, anchored: false, earliest: false, c.D!);
        if (end == LazyDfa.GaveUp) return Find(h, pos, out _, out e, c);
        if (end < 0) return false;
        e = end;
        return true;
    }

    internal static bool StartsWithTextStart(RegexNode n) => n switch
    {
        RegexNode.Assertion { Kind: RegexAssertionKind.TextStart } => true,
        RegexNode.Concat c => c.Items.Length > 0 && StartsWithTextStart(c.Items[0]),
        RegexNode.Capture c => StartsWithTextStart(c.Body),
        RegexNode.FlagScope f => StartsWithTextStart(f.Body),
        RegexNode.Alternation a => a.Alternatives.All(StartsWithTextStart),
        _ => false,
    };

    public Caches Rent()
    {
        var c = Interlocked.Exchange(ref pooled, null);
        if (c != null) return c;
        c = new Caches { Slots = new int[Math.Max(2, SlotCount)] };
        if (dfa != null) c.D = dfa.NewCache();
        if (rdfa != null) c.R = rdfa.NewCache();
        if (pike != null) c.P = pike.NewCache();
        if (bt != null) c.B = bt.NewCache();
        return c;
    }

    public void Return(Caches c) => Volatile.Write(ref pooled, c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Unit(ReadOnlySpan<T> h, int i) =>
        typeof(T) == typeof(byte) ? Unsafe.As<T, byte>(ref Unsafe.AsRef(in h[i])) : Unsafe.As<T, char>(ref Unsafe.AsRef(in h[i]));

    /// <summary>True at scalar boundaries (never inside a UTF-8 sequence or a surrogate pair).</summary>
    public static bool Boundary(ReadOnlySpan<T> h, int i)
    {
        if (i <= 0 || i >= h.Length) return true;
        if (typeof(T) == typeof(byte)) return (Unit(h, i) & 0xC0) != 0x80;
        return !(char.IsLowSurrogate((char)Unit(h, i)) && char.IsHighSurrogate((char)Unit(h, i - 1)));
    }

    /// <summary>The next scalar boundary after <paramref name="i"/> (n + 1 past the end).</summary>
    public static int NextBoundary(ReadOnlySpan<T> h, int i)
    {
        if (i >= h.Length) return h.Length + 1;
        int j = i + 1;
        while (j < h.Length && !Boundary(h, j)) j++;
        return j;
    }

    /// <summary>The leftmost-first match at or after pos (group 0 in s/e).</summary>
    public bool Find(ReadOnlySpan<T> h, int pos, out int s, out int e, Caches c)
    {
        while (true)
        {
            if (!FindRaw(h, pos, out s, out e, c)) return false;
            if (s == e && !Boundary(h, s))
            {
                pos = NextBoundary(h, s);
                if (pos > h.Length) return false;
                continue;
            }
            return true;
        }
    }

    bool FindRaw(ReadOnlySpan<T> h, int pos, out int s, out int e, Caches c)
    {
        s = e = -1;
        int n = h.Length;
        if (pos > n || (anchoredStart && pos > 0)) return false;
        if (tierB)
        {
            var slots = c.Slots.AsSpan(0, 2);
            if (!bt!.Search(h, pos, anchoredStart, slots, c.B!, pf)) return false;
            s = slots[0];
            e = slots[1];
            return true;
        }
        if (pikeOnly || LazyDfa.Thrashing(c.D!)) return PikeFind(h, pos, out s, out e, c);
        var d = dfa!;
        if (anchoredStart)
        {
            int r = d.FindFwd(h, 0, anchored: true, earliest: false, c.D!);
            if (r == LazyDfa.GaveUp) return PikeFind(h, pos, out s, out e, c);
            if (r < 0) return false;
            s = 0; e = r;
            return true;
        }
        if (pf != null)
        {
            int from = pos, tested = pos - 1;
            while (true)
            {
                int hit = pf.Find(h, from, out int len);
                if (hit < 0) return false;
                if (pf.Exact) { s = hit; e = hit + len; return true; }
                int lo = Math.Max(Math.Max(pos, tested + 1), hit - pf.Offset);
                for (int st = lo; st <= hit; st++)
                {
                    int r = d.FindFwd(h, st, anchored: true, earliest: false, c.D!);
                    if (r == LazyDfa.GaveUp) return PikeFind(h, pos, out s, out e, c);
                    if (r >= 0) { s = st; e = r; return true; }
                }
                tested = hit;
                from = hit + 1;
            }
        }
        int end = d.FindFwd(h, pos, anchored: false, earliest: false, c.D!);
        if (end == LazyDfa.GaveUp) return PikeFind(h, pos, out s, out e, c);
        if (end < 0) return false;
        int start = rdfa!.FindRev(h, end, pos, c.R!);
        if (start == LazyDfa.GaveUp || start < 0) return PikeFind(h, pos, out s, out e, c);
        s = start;
        e = end;
        return true;
    }

    bool PikeFind(ReadOnlySpan<T> h, int pos, out int s, out int e, Caches c)
    {
        var slots = c.Slots.AsSpan(0, 2);
        s = e = -1;
        if (!pike!.Search(h, pos, h.Length, anchoredStart, slots, c.P!)) return false;
        s = slots[0];
        e = slots[1];
        return true;
    }

    /// <summary>Is there any match at or after pos? (no start, no captures)</summary>
    public bool IsMatch(ReadOnlySpan<T> h, int pos, Caches c)
    {
        if (tierB || pikeOnly || anchoredStart || LazyDfa.Thrashing(c.D!)) return Find(h, pos, out _, out _, c);
        var d = dfa!;
        if (pf != null)
        {
            int from = pos, tested = pos - 1;
            while (true)
            {
                int hit = pf.Find(h, from, out _);
                if (hit < 0) return false;
                if (pf.Exact) return true;
                int lo = Math.Max(Math.Max(pos, tested + 1), hit - pf.Offset);
                for (int st = lo; st <= hit; st++)
                {
                    int r = d.FindFwd(h, st, anchored: true, earliest: true, c.D!);
                    if (r == LazyDfa.GaveUp) return Find(h, pos, out _, out _, c);
                    if (r >= 0) return true;
                }
                tested = hit;
                from = hit + 1;
            }
        }
        int end = d.FindFwd(h, pos, anchored: false, earliest: true, c.D!);
        if (end == LazyDfa.GaveUp) return Find(h, pos, out _, out _, c);
        return end >= 0;
    }

    /// <summary>The match at or after pos with its capture slots (2 per group, -1 unset) in <paramref name="slots"/>.</summary>
    public bool Captures(ReadOnlySpan<T> h, int pos, Span<int> slots, Caches c)
    {
        slots.Fill(-1);
        if (tierB)
        {
            while (true)
            {
                if (!bt!.Search(h, pos, anchoredStart, slots, c.B!, pf)) return false;
                if (slots[0] == slots[1] && !Boundary(h, slots[0])) { pos = NextBoundary(h, slots[0]); if (pos > h.Length) return false; continue; }
                return true;
            }
        }
        if (!Find(h, pos, out int s, out int e, c)) return false;
        if (slots.Length <= 2 || SlotCount <= 2) { slots[0] = s; slots[1] = e; return true; }
        if (!pike!.Search(h, s, e, anchored: true, slots, c.P!) || slots[1] != e) { slots.Fill(-1); slots[0] = s; slots[1] = e; }
        return true;
    }
}
