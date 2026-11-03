using System.Text;

namespace Alice.Core.Text;

internal enum NKind : byte { Range, Split, Look, Save, Match, Fail }

/// <summary>A unit-range transition: units in [Lo, Hi] go to Next.</summary>
internal readonly record struct NTrans(int Lo, int Hi, int Next);

internal struct NState
{
    public NKind Kind;
    public RegexAssertionKind Look;
    public int Next;
    public int Slot;
    public int[]? Alts;
    public NTrans[]? Trans;
}

/// <summary>Thrown when a Tier R pattern would compile to too many NFA states; the regex falls back to the backtracker.</summary>
internal sealed class NfaTooBigException : Exception { }

/// <summary>
/// Thompson NFA over code units (UTF-8 bytes or UTF-16 units) with leftmost-first priorities (Split alternatives in
/// order). Scalars compile to their unit sequences, so `.` and classes consume whole well-formed scalars only. The
/// reverse NFA (for the reverse DFA that finds match starts) reads units right to left and has no capture slots.
/// </summary>
internal sealed class Nfa
{
    public NState[] States = [];
    public int Start;              // anchored start (forward: Save(0) .. Save(1) Match)
    public int StartUnanchored;    // (?s:.)*? prefix (forward only)
    public bool Utf8, Reverse;
    public int SlotCount;          // 2 * (groups + 1)
    public bool HasLook, HasWordLook, DfaUnsupported;   // DfaUnsupported: \Z or \G (only the Pike VM evaluates them)
    public int MaxUnit => Utf8 ? 0xFF : 0xFFFF;

    const int MaxStates = 200_000;

    public static Nfa Compile(RegexPattern p, bool utf8, bool reverse)
    {
        var b = new Builder(utf8, reverse);
        var nfa = new Nfa { Utf8 = utf8, Reverse = reverse, SlotCount = 2 * (p.CaptureCount + 1) };
        int match = b.Add(new NState { Kind = NKind.Match });
        int body;
        if (reverse) body = b.Compile(p.Root, match);
        else
        {
            int s1 = b.Add(new NState { Kind = NKind.Save, Slot = 1, Next = match });
            int inner = b.Compile(p.Root, s1);
            body = b.Add(new NState { Kind = NKind.Save, Slot = 0, Next = inner });
        }
        nfa.Start = body;
        if (!reverse)
        {
            int loop = b.Add(new NState { Kind = NKind.Split });
            int any = b.Add(new NState { Kind = NKind.Range, Trans = [new NTrans(0, nfa.MaxUnit, loop)] });
            b.St[loop] = b.St[loop] with { Alts = [body, any] };
            nfa.StartUnanchored = loop;
        }
        else nfa.StartUnanchored = body;
        nfa.States = b.St.ToArray();
        nfa.HasLook = b.HasLook;
        nfa.HasWordLook = b.HasWordLook;
        nfa.DfaUnsupported = b.DfaUnsupported;
        return nfa;
    }

    sealed class Builder(bool utf8, bool reverse)
    {
        public readonly List<NState> St = [];
        public bool HasLook, HasWordLook, DfaUnsupported;
        readonly Dictionary<string, int> shared = [];

        public int Add(NState s)
        {
            if (St.Count >= MaxStates) throw new NfaTooBigException();
            St.Add(s);
            return St.Count - 1;
        }

        int Split(int a, int b) => Add(new NState { Kind = NKind.Split, Alts = [a, b] });

        public int Compile(RegexNode n, int next)
        {
            switch (n)
            {
                case RegexNode.Empty:
                    return next;
                case RegexNode.Literal l:
                    {
                        var runes = new List<int>();
                        foreach (var r in l.Text.EnumerateRunes()) runes.Add(r.Value);
                        if (!reverse) for (int k = runes.Count - 1; k >= 0; k--) next = Scalar(runes[k], l.IgnoreCase, next);
                        else for (int k = 0; k < runes.Count; k++) next = Scalar(runes[k], l.IgnoreCase, next);
                        return next;
                    }
                case RegexNode.AnyChar a:
                    return Set(a.DotAll ? RangeSet.Complement([]) : RangeSet.Complement([new('\n', '\n')]), next);
                case RegexNode.CharClass c:
                    return Set(RegexSyntax.Resolve(c), next);
                case RegexNode.Concat c:
                    if (!reverse) for (int k = c.Items.Length - 1; k >= 0; k--) next = Compile(c.Items[k], next);
                    else for (int k = 0; k < c.Items.Length; k++) next = Compile(c.Items[k], next);
                    return next;
                case RegexNode.Alternation a:
                    {
                        var alts = new int[a.Alternatives.Length];
                        for (int k = 0; k < alts.Length; k++) alts[k] = Compile(a.Alternatives[k], next);
                        return Add(new NState { Kind = NKind.Split, Alts = alts });
                    }
                case RegexNode.Repeat r:
                    return Repeat(r, next);
                case RegexNode.Capture c:
                    {
                        if (reverse) return Compile(c.Body, next);
                        int end = Add(new NState { Kind = NKind.Save, Slot = 2 * c.Index + 1, Next = next });
                        int body = Compile(c.Body, end);
                        return Add(new NState { Kind = NKind.Save, Slot = 2 * c.Index, Next = body });
                    }
                case RegexNode.FlagScope f:
                    return Compile(f.Body, next);
                case RegexNode.Assertion a:
                    HasLook = true;
                    if (a.Kind is RegexAssertionKind.WordBoundary or RegexAssertionKind.NotWordBoundary) HasWordLook = true;
                    if (a.Kind is RegexAssertionKind.TextEndOrFinalNewline or RegexAssertionKind.SearchStart) DfaUnsupported = true;
                    return Add(new NState { Kind = NKind.Look, Look = a.Kind, Next = next });
                default:
                    throw new NotSupportedException(n.GetType().Name + " needs the backtracking engine");
            }
        }

        int Repeat(RegexNode.Repeat r, int next)
        {
            if (r.Mode == RegexGreediness.Possessive) throw new NotSupportedException("possessive");
            bool greedy = r.Mode == RegexGreediness.Greedy;
            int cur;
            if (r.Max == -1)
            {
                int loop = Add(new NState { Kind = NKind.Split });
                int b = Compile(r.Body, loop);
                St[loop] = St[loop] with { Alts = greedy ? [b, next] : [next, b] };
                cur = loop;
            }
            else
            {
                cur = next;
                for (int k = 0; k < r.Max - r.Min; k++)
                {
                    int s = Add(new NState { Kind = NKind.Split });
                    int b = Compile(r.Body, cur);
                    St[s] = St[s] with { Alts = greedy ? [b, next] : [next, b] };
                    cur = s;
                }
            }
            for (int k = 0; k < r.Min; k++) cur = Compile(r.Body, cur);
            return cur;
        }

        int Scalar(int c, bool ignoreCase, int next)
        {
            if (ignoreCase && CaseFold.Equivalents(c) is { } eq) return Set(RangeSet.Of(eq), next);
            Span<byte> b8 = stackalloc byte[4];
            Span<char> b16 = stackalloc char[2];
            int n;
            var rune = new Rune(c);
            if (utf8) n = rune.EncodeToUtf8(b8); else n = rune.EncodeToUtf16(b16);
            if (!reverse)
                for (int k = n - 1; k >= 0; k--) next = Unit(utf8 ? b8[k] : b16[k], next);
            else
                for (int k = 0; k < n; k++) next = Unit(utf8 ? b8[k] : b16[k], next);
            return next;
        }

        int Unit(int u, int next) => Add(new NState { Kind = NKind.Range, Trans = [new NTrans(u, u, next)] });

        // A scalar set: its unit sequences as a trie with shared suffixes.
        int Set(List<RegexRange> set, int next)
        {
            if (set.Count == 0) return Add(new NState { Kind = NKind.Fail });
            var seqs = utf8 ? UnitSequences.Utf8(set) : UnitSequences.Utf16(set);
            var root = new Trie();
            foreach (var seq in seqs)
            {
                var node = root;
                int len = seq.Length;
                for (int k = 0; k < len; k++)
                {
                    var (lo, hi) = seq[reverse ? len - 1 - k : k];
                    if (k == len - 1) { node.Edges.Add((lo, hi, null)); break; }
                    Trie? child = null;
                    foreach (var e in node.Edges) if (e.Lo == lo && e.Hi == hi && e.Child != null) { child = e.Child; break; }
                    if (child == null) { child = new Trie(); node.Edges.Add((lo, hi, child)); }
                    node = child;
                }
            }
            return Emit(root, next);
        }

        sealed class Trie { public readonly List<(int Lo, int Hi, Trie? Child)> Edges = []; }

        int Emit(Trie t, int next)
        {
            var trans = new List<NTrans>(t.Edges.Count);
            foreach (var (lo, hi, child) in t.Edges) trans.Add(new NTrans(lo, hi, child == null ? next : Emit(child, next)));
            trans.Sort(static (a, b) => a.Lo != b.Lo ? a.Lo.CompareTo(b.Lo) : a.Next.CompareTo(b.Next));
            // merge adjacent ranges with the same target
            var merged = new List<NTrans>(trans.Count);
            foreach (var x in trans)
            {
                if (merged.Count > 0 && merged[^1].Next == x.Next && merged[^1].Hi + 1 >= x.Lo)
                    merged[^1] = merged[^1] with { Hi = Math.Max(merged[^1].Hi, x.Hi) };
                else merged.Add(x);
            }
            var key = new StringBuilder();
            foreach (var x in merged) key.Append(x.Lo).Append('-').Append(x.Hi).Append('>').Append(x.Next).Append(';');
            string k = key.ToString();
            if (shared.TryGetValue(k, out int id)) return id;
            id = Add(new NState { Kind = NKind.Range, Trans = merged.ToArray() });
            shared[k] = id;
            return id;
        }
    }
}

/// <summary>Scalar ranges to code-unit range sequences (the utf8-ranges construction).</summary>
internal static class UnitSequences
{
    public static List<(int, int)[]> Utf8(List<RegexRange> set)
    {
        var o = new List<(int, int)[]>();
        var stack = new Stack<(int, int)>();
        for (int k = set.Count - 1; k >= 0; k--) stack.Push((set[k].First, set[k].Last));
        Span<byte> a = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
        again:
            if (s <= 0xDFFF && e >= 0xD800)
            {
                if (s < 0xD800 && e > 0xDFFF) { stack.Push((0xE000, e)); e = 0xD7FF; }
                else if (s < 0xD800) e = 0xD7FF;
                else if (e > 0xDFFF) s = 0xE000;
                else continue;
            }
            bool split = false;
            foreach (int max in (ReadOnlySpan<int>)[0x7F, 0x7FF, 0xFFFF])
                if (s <= max && max < e) { stack.Push((max + 1, e)); e = max; split = true; break; }
            if (split) goto again;
            if (e <= 0x7F) { o.Add([(s, e)]); continue; }
            for (int i = 1; i < 4; i++)
            {
                int m = (1 << (6 * i)) - 1;
                if ((s & ~m) != (e & ~m))
                {
                    if ((s & m) != 0) { stack.Push(((s | m) + 1, e)); e = s | m; split = true; break; }
                    if ((e & m) != m) { stack.Push((e & ~m, e)); e = (e & ~m) - 1; split = true; break; }
                }
            }
            if (split) goto again;
            int n = new Rune(s).EncodeToUtf8(a);
            new Rune(e).EncodeToUtf8(b);
            var seq = new (int, int)[n];
            for (int k = 0; k < n; k++) seq[k] = (a[k], b[k]);
            o.Add(seq);
        }
        return o;
    }

    public static List<(int, int)[]> Utf16(List<RegexRange> set)
    {
        var o = new List<(int, int)[]>();
        var astral = new List<RegexRange>();
        foreach (var r in set)
        {
            if (r.First <= 0xFFFF)
            {
                int lo = r.First, hi = Math.Min(r.Last, 0xFFFF);
                if (lo < 0xD800) o.Add([(lo, Math.Min(hi, 0xD7FF))]);
                if (hi > 0xDFFF) o.Add([(Math.Max(lo, 0xE000), hi)]);
            }
            if (r.Last > 0xFFFF) astral.Add(new(Math.Max(r.First, 0x10000), r.Last));
        }
        foreach (var (hl, hh, ll, lh) in RegexSyntax.Utf16Pairs(astral)) o.Add([(hl, hh), (ll, lh)]);
        return o;
    }
}
