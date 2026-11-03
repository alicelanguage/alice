namespace Alice.Core.Text;

/// <summary>
/// Equivalence classes of code units: two units share a class when every NFA transition treats them alike (and,
/// when the pattern has assertions, when they agree on being `\n` and being an ASCII word unit). The DFA's
/// transition table has one column per class, so the alphabet stays small even for UTF-16. The partition is
/// refined interval by interval (cost per refinement ~ number of intervals, not units).
/// </summary>
internal sealed class ByteClasses
{
    public readonly ushort[] Map;      // unit -> class for units 0..255
    readonly int[] highStart = [];     // UTF-16 units >= 256: class runs (start unit, class), sorted
    readonly ushort[] highCls = [];
    public readonly int Count;
    public readonly int[] Rep;         // class -> a representative unit
    public readonly byte[] Ctx;        // class -> context (LazyDfa.CtxOther/Newline/Word)

    public ByteClasses(Nfa nfa)
    {
        int units = nfa.MaxUnit + 1;
        var parts = new List<(int Lo, int Hi, int Cls)> { (0, units - 1, 0) };
        int next = 1;
        var seen = new HashSet<string>();
        var remap = new Dictionary<int, int>();

        void Refine(List<(int Lo, int Hi)> set)
        {
            set.Sort();
            var key = string.Join(";", set.Select(r => $"{r.Lo}-{r.Hi}"));
            if (!seen.Add(key)) return;
            remap.Clear();
            var outp = new List<(int, int, int)>(parts.Count + 2 * set.Count);
            int si = 0;
            foreach (var (plo, phi, pc) in parts)
            {
                int lo = plo;
                while (lo <= phi)
                {
                    while (si < set.Count && set[si].Hi < lo) si++;
                    if (si >= set.Count || set[si].Lo > phi) { outp.Add((lo, phi, pc)); break; }
                    var (slo, shi) = set[si];
                    if (slo > lo) { outp.Add((lo, slo - 1, pc)); lo = slo; continue; }
                    int end = Math.Min(phi, shi);
                    if (!remap.TryGetValue(pc, out int nc)) remap[pc] = nc = next++;
                    outp.Add((lo, end, nc));
                    lo = end + 1;
                }
            }
            parts = outp;
        }

        foreach (var st in nfa.States)
        {
            if (st.Kind != NKind.Range || st.Trans == null) continue;
            var byTarget = new Dictionary<int, List<(int, int)>>();
            foreach (var t in st.Trans)
            {
                if (!byTarget.TryGetValue(t.Next, out var l)) byTarget[t.Next] = l = [];
                l.Add((t.Lo, t.Hi));
            }
            foreach (var l in byTarget.Values)
            {
                if (l.Count == 1 && l[0].Item1 == 0 && l[0].Item2 == units - 1) continue;
                Refine(Merge(l));
            }
        }
        if (nfa.HasLook)
        {
            Refine([('\n', '\n')]);
            Refine([('0', '9'), ('A', 'Z'), ('_', '_'), ('a', 'z')]);
        }
        var compact = new Dictionary<int, int>();
        var reps = new List<int>();
        Map = new ushort[256];
        var hs = new List<int>();
        var hc = new List<ushort>();
        foreach (var (lo, hi, c0) in parts)
        {
            if (!compact.TryGetValue(c0, out int c)) { c = compact.Count; compact[c0] = c; reps.Add(lo); }
            if (lo <= 255) Map.AsSpan(lo, Math.Min(hi, 255) - lo + 1).Fill((ushort)c);
            if (hi >= 256)
            {
                int st = Math.Max(lo, 256);
                if (hc.Count > 0 && hc[^1] == c) continue;   // merge runs of the same class
                hs.Add(st);
                hc.Add((ushort)c);
            }
        }
        highStart = hs.ToArray();
        highCls = hc.ToArray();
        Count = compact.Count;
        Rep = reps.ToArray();
        Ctx = new byte[Count];
        for (int c = 0; c < Count; c++) Ctx[c] = LazyDfa.CtxOfUnit(Rep[c]);
    }

    /// <summary>Class of a UTF-16 unit >= 256 (binary search over class runs).</summary>
    public int High(int u)
    {
        var st = highStart;
        int lo = 0, hi = st.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (st[mid] <= u) lo = mid; else hi = mid - 1;
        }
        return highCls[lo];
    }

    static List<(int, int)> Merge(List<(int, int)> l)
    {
        l.Sort();
        var o = new List<(int, int)>(l.Count);
        foreach (var (a, b) in l)
        {
            if (o.Count > 0 && o[^1].Item2 + 1 >= a) o[^1] = (o[^1].Item1, Math.Max(o[^1].Item2, b));
            else o.Add((a, b));
        }
        return o;
    }
}
