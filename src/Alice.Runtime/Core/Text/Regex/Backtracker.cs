using System.Runtime.CompilerServices;
using System.Text;

namespace Alice.Core.Text;

/// <summary>The backtracker exceeded its step budget on a pattern whose memoization is off (backreferences).</summary>
public sealed class RegexBacktrackLimitException(string pattern)
    : InvalidOperationException($"regex backtracking limit exceeded: /{pattern}/ (catastrophic backtracking; rewrite the pattern)");

/// <summary>
/// Tier B: a backtracking matcher over scalars for look-around, backreferences, atomic groups, possessive and large
/// counted repeats (Onigmo semantics: leftmost-first, captures of the last iteration, look-behind of fixed-length
/// alternatives). Without backreferences it memoizes failed (instruction, position) pairs like Rust's bounded
/// backtracker, so matching is O(program x haystack) per window; with backreferences a step budget stops
/// catastrophic patterns with <see cref="RegexBacktrackLimitException"/>. Atomic groups and look-arounds run as
/// sub-matches; their memo marks are dropped when they succeed (a success is not a failure to remember).
/// </summary>
internal sealed class Backtracker
{
    enum Op : byte { Char, Set, Any, AnyNl, Lit, Split, Jmp, Save, Assert, Look, LookBehind, Atomic, SubEnd, BackRef, LoopInit, LoopCheck, Match, Fail }

    struct Inst
    {
        public Op Op;
        public bool Flag;          // Look: negated; BackRef: ignore case; Split: unused
        public int A, B, C;
    }

    readonly List<Inst> prog = [];
    readonly List<RegexRange[]> sets = [];
    readonly List<ulong[]> asciiBits = [];
    readonly List<string> lits = [];
    readonly List<byte[]> lits8 = [];
    readonly List<(int Pc, int Len)[]> behinds = [];
    readonly string pattern;
    Inst[] code = [];
    public readonly int SlotCount;
    int RegBase => SlotCount + SlotCount / 2;   // slots: committed captures, pending group starts, loop registers
    readonly int registers;
    public readonly bool Memo;
    public readonly bool AnchoredStart;
    const long Budget = 200_000_000;
    const int MemoBits = 1 << 25;

    public Backtracker(RegexPattern p)
    {
        pattern = p.Source;
        SlotCount = 2 * (p.CaptureCount + 1);
        Memo = (p.Features & RegexFeatures.Backreferences) == 0;
        ic = (p.Flags & RegexFlags.IgnoreCase) != 0;
        AnchoredStart = StartsWithTextStart(p.Root);
        Emit(new Inst { Op = Op.Save, A = 0 });
        Compile(p.Root);
        Emit(new Inst { Op = Op.Save, A = 1 });
        Emit(new Inst { Op = Op.Match });
        registers = loopRegs;
        code = prog.ToArray();
    }

    static bool StartsWithTextStart(RegexNode n) => n switch
    {
        RegexNode.Assertion { Kind: RegexAssertionKind.TextStart } => true,
        RegexNode.Concat c => c.Items.Length > 0 && StartsWithTextStart(c.Items[0]),
        RegexNode.Capture c => StartsWithTextStart(c.Body),
        RegexNode.FlagScope f => StartsWithTextStart(f.Body),
        RegexNode.Atomic a => StartsWithTextStart(a.Body),
        RegexNode.Alternation a => a.Alternatives.All(StartsWithTextStart),
        _ => false,
    };

    int loopRegs;
    int Pc => prog.Count;
    int Emit(Inst i) { if (prog.Count > 2_000_000) throw new RegexParseException("pattern too large for the backtracker", pattern, 0); prog.Add(i); return prog.Count - 1; }
    void Patch(int at, int a, int b = int.MinValue) { var x = prog[at]; x.A = a; if (b != int.MinValue) x.B = b; prog[at] = x; }

    int SetIndex(List<RegexRange> r)
    {
        sets.Add(r.ToArray());
        var bits = new ulong[2];
        foreach (var x in r)
            for (int c = x.First; c <= Math.Min(x.Last, 127); c++) bits[c >> 6] |= 1UL << (c & 63);
        asciiBits.Add(bits);
        return sets.Count - 1;
    }

    void Compile(RegexNode n)
    {
        switch (n)
        {
            case RegexNode.Empty:
                break;
            case RegexNode.Literal l:
                if (!l.IgnoreCase) { lits.Add(l.Text); lits8.Add(Encoding.UTF8.GetBytes(l.Text)); Emit(new Inst { Op = Op.Lit, A = lits.Count - 1 }); break; }
                foreach (var r in l.Text.EnumerateRunes())
                {
                    var eq = CaseFold.Equivalents(r.Value);
                    if (eq == null) Emit(new Inst { Op = Op.Char, A = r.Value });
                    else Emit(new Inst { Op = Op.Set, A = SetIndex(RangeSet.Of(eq)) });
                }
                break;
            case RegexNode.AnyChar a:
                Emit(new Inst { Op = a.DotAll ? Op.Any : Op.AnyNl });
                break;
            case RegexNode.CharClass c:
                Emit(new Inst { Op = Op.Set, A = SetIndex(RegexSyntax.Resolve(c)) });
                break;
            case RegexNode.Concat c:
                foreach (var x in c.Items) Compile(x);
                break;
            case RegexNode.Alternation a:
                {
                    // Split L1, next; L1: alt0; Jmp end; next: Split L2, next2 ...
                    var jumps = new List<int>();
                    for (int k = 0; k < a.Alternatives.Length; k++)
                    {
                        if (k < a.Alternatives.Length - 1)
                        {
                            int sp = Emit(new Inst { Op = Op.Split });
                            Compile(a.Alternatives[k]);
                            jumps.Add(Emit(new Inst { Op = Op.Jmp }));
                            Patch(sp, sp + 1, Pc);
                        }
                        else Compile(a.Alternatives[k]);
                    }
                    foreach (int j in jumps) Patch(j, Pc);
                    break;
                }
            case RegexNode.Repeat r:
                if (r.Mode == RegexGreediness.Possessive)
                {
                    int at = Emit(new Inst { Op = Op.Atomic });
                    Repeat(r with { Mode = RegexGreediness.Greedy });
                    Emit(new Inst { Op = Op.SubEnd });
                    Patch(at, at + 1, Pc);
                }
                else Repeat(r);
                break;
            case RegexNode.Capture c:
                Emit(new Inst { Op = Op.Save, A = 2 * c.Index });
                Compile(c.Body);
                Emit(new Inst { Op = Op.Save, A = 2 * c.Index + 1 });
                break;
            case RegexNode.FlagScope f:
                {
                    bool saved = ic;
                    if ((f.Enable & RegexFlags.IgnoreCase) != 0) ic = true;
                    if ((f.Disable & RegexFlags.IgnoreCase) != 0) ic = false;
                    Compile(f.Body);
                    ic = saved;
                    break;
                }
            case RegexNode.Atomic a:
                {
                    int at = Emit(new Inst { Op = Op.Atomic });
                    Compile(a.Body);
                    Emit(new Inst { Op = Op.SubEnd });
                    Patch(at, at + 1, Pc);
                    break;
                }
            case RegexNode.Lookaround { Behind: false } l:
                {
                    int at = Emit(new Inst { Op = Op.Look, Flag = l.Negated });
                    Compile(l.Body);
                    Emit(new Inst { Op = Op.SubEnd });
                    Patch(at, at + 1, Pc);
                    break;
                }
            case RegexNode.Lookaround l:
                {
                    var alts = l.Body is RegexNode.Alternation alt ? alt.Alternatives : [l.Body];
                    int at = Emit(new Inst { Op = Op.LookBehind, Flag = l.Negated });
                    var list = new (int, int)[alts.Length];
                    for (int k = 0; k < alts.Length; k++)
                    {
                        list[k] = (Pc, RegexSyntax.FixedLength(alts[k]) ?? 0);
                        Compile(alts[k]);
                        Emit(new Inst { Op = Op.SubEnd });
                    }
                    behinds.Add(list);
                    Patch(at, behinds.Count - 1, Pc);
                    break;
                }
            case RegexNode.Backreference b:
                Emit(new Inst { Op = Op.BackRef, A = b.Index, Flag = ic });
                break;
            case RegexNode.Assertion a:
                Emit(new Inst { Op = Op.Assert, A = (int)a.Kind });
                break;
            default:
                throw new NotSupportedException(n.GetType().Name);
        }
    }

    // backreferences follow the case flag in effect where they appear (Onigmo: case-insensitive backrefs under /i)
    bool ic;

    void Repeat(RegexNode.Repeat r)
    {
        bool greedy = r.Mode == RegexGreediness.Greedy;
        bool nullable = RegexSyntax.Nullable(r.Body);
        for (int k = 0; k < r.Min; k++) Compile(r.Body);
        if (r.Max == -1)
        {
            // L: Split body, exit ; body ; [LoopCheck] ; Jmp L
            int reg = nullable ? loopRegs++ : -1;
            int split = Emit(new Inst { Op = Op.Split });
            if (reg >= 0) Emit(new Inst { Op = Op.LoopInit, A = reg });
            Compile(r.Body);
            int check = reg >= 0 ? Emit(new Inst { Op = Op.LoopCheck, A = reg }) : -1;
            Emit(new Inst { Op = Op.Jmp, A = split });
            if (greedy) Patch(split, split + 1, Pc); else Patch(split, Pc, split + 1);
            if (check >= 0) { var x = prog[check]; x.B = Pc; prog[check] = x; }
            return;
        }
        // optional copies; an empty iteration ends the repeat (the empty check of .NET and Onigmo)
        var exits = new List<int>();
        var checks = new List<int>();
        int ereg = nullable && r.Max > r.Min ? loopRegs++ : -1;
        for (int k = 0; k < r.Max - r.Min; k++)
        {
            int split = Emit(new Inst { Op = Op.Split });
            exits.Add(split);
            if (ereg >= 0) Emit(new Inst { Op = Op.LoopInit, A = ereg });
            Compile(r.Body);
            if (ereg >= 0) checks.Add(Emit(new Inst { Op = Op.LoopCheck, A = ereg }));
        }
        foreach (int sp in exits) { if (greedy) Patch(sp, sp + 1, Pc); else Patch(sp, Pc, sp + 1); }
        foreach (int ck in checks) { var x = prog[ck]; x.B = Pc; prog[ck] = x; }
    }

    // ---------------------------------------------------------------- execution

    public sealed class Cache
    {
        internal int[] Slots = [];
        internal int[] Stack = new int[256];   // triples: kind|pc, pos, extra
        internal ulong[] Bits = [];
        internal int[] Marked = new int[256];
        internal int MarkCount;
        internal long Steps;
        internal int Base, Window, SearchStart, Sp;
    }

    public Cache NewCache() => new();

    // stack frames: kind in the low 2 bits. Branch: an alternative to try; Restore: undo a slot write;
    // FailMark: every alternative below a Split at (pc, pos) failed, so remember it
    const int KBranch = 0, KRestore = 1, KFailMark = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Unit<T>(ReadOnlySpan<T> h, int i) where T : unmanaged =>
        typeof(T) == typeof(byte) ? Unsafe.As<T, byte>(ref Unsafe.AsRef(in h[i])) : Unsafe.As<T, char>(ref Unsafe.AsRef(in h[i]));

    // decodes the scalar at i; returns its width (0: end or ill-formed, which nothing matches)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Decode<T>(ReadOnlySpan<T> h, int i, out int cp) where T : unmanaged
    {
        cp = -1;
        if (i >= h.Length) return 0;
        int u = Unit(h, i);
        if (u < 0x80) { cp = u; return 1; }
        if (typeof(T) == typeof(char))
        {
            if (u is < 0xD800 or > 0xDFFF) { cp = u; return 1; }
            if (u <= 0xDBFF && i + 1 < h.Length && Unit(h, i + 1) is >= 0xDC00 and <= 0xDFFF and var lo) { cp = 0x10000 + ((u - 0xD800) << 10) + (lo - 0xDC00); return 2; }
            return 0;
        }
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<T, byte>(h)[i..];
        if (Rune.DecodeFromUtf8(span, out var r, out int n) != System.Buffers.OperationStatus.Done) return 0;
        cp = r.Value;
        return n;
    }

    // start of the scalar ending at i (-1 at 0 or when ill-formed)
    static int StepBack<T>(ReadOnlySpan<T> h, int i) where T : unmanaged
    {
        if (i <= 0) return -1;
        if (typeof(T) == typeof(char))
        {
            int u = Unit(h, i - 1);
            if (u is >= 0xDC00 and <= 0xDFFF && i >= 2 && Unit(h, i - 2) is >= 0xD800 and <= 0xDBFF) return i - 2;
            return u is >= 0xD800 and <= 0xDFFF ? -1 : i - 1;
        }
        int j = i - 1;
        while (j > 0 && i - j < 4 && (Unit(h, j) & 0xC0) == 0x80) j--;
        return Decode(h, j, out _) == i - j ? j : -1;
    }

    bool InSet(int set, int cp)
    {
        if (cp < 128) return (asciiBits[set][cp >> 6] & (1UL << (cp & 63))) != 0;
        return RangeSet.Contains(sets[set], cp);
    }

    /// <summary>Leftmost-first match at or after <paramref name="pos"/> (only at pos when anchored); slots filled.</summary>
    public bool Search<T>(ReadOnlySpan<T> h, int pos, bool anchored, Span<int> slots, Cache c, Prefilter<T>? pf) where T : unmanaged
    {
        int need = RegBase + registers;
        if (c.Slots.Length < need) c.Slots = new int[need];
        c.Steps = 0;
        c.Sp = 0;
        c.SearchStart = pos;
        ResetMemo(c, h.Length, pos);
        try
        {
            int n = h.Length;
            for (int s = pos; s <= n;)
            {
                if (AnchoredStart && s > 0) return false;
                if (pf != null && !anchored)
                {
                    int cand = pf.Find(h, s, out _);
                    if (cand < 0) return false;
                    s = Math.Max(s, cand - pf.Offset);
                }
                if (Memo && s >= c.Base + c.Window / 2 && c.Window < n + 1) ResetMemo(c, n, s);
                c.Slots.AsSpan(0, need).Fill(-1);
                if (Run(h, 0, s, -1, c, out _))
                {
                    c.Slots.AsSpan(0, Math.Min(SlotCount, slots.Length)).CopyTo(slots);
                    return true;
                }
                if (anchored) return false;
                if (s >= n) break;
                int w = Decode(h, s, out _);
                s += w > 0 ? w : 1;
            }
            return false;
        }
        finally { ClearMarks(c); }
    }

    void ResetMemo(Cache c, int n, int basePos)
    {
        ClearMarks(c);
        if (!Memo) return;
        int window = (int)Math.Min(n - basePos + 1L, Math.Max(64, MemoBits / Math.Max(1, code.Length)));
        long bits = (long)window * code.Length;
        int words = (int)((bits + 63) >> 6);
        if (c.Bits.Length < words) c.Bits = new ulong[words];
        c.Base = basePos;
        c.Window = window;
    }

    static void ClearMarks(Cache c)
    {
        for (int k = 0; k < c.MarkCount; k++) { int b = c.Marked[k]; c.Bits[b >> 6] &= ~(1UL << (b & 63)); }
        c.MarkCount = 0;
    }

    // true when the Split at (pc, pos) is known to fail (memo); counts the step budget when not memoized
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool KnownFailed(Cache c, int pc, int pos)
    {
        int rel = pos - c.Base;
        if (!Memo || (uint)rel >= (uint)c.Window)
        {
            if (++c.Steps > Budget) throw new RegexBacktrackLimitException(pattern);
            return false;
        }
        int bit = rel * code.Length + pc;
        return (c.Bits[bit >> 6] & (1UL << (bit & 63))) != 0;
    }

    void MarkFailed(Cache c, int pc, int pos)
    {
        int rel = pos - c.Base;
        if (!Memo || (uint)rel >= (uint)c.Window) return;
        int bit = rel * code.Length + pc;
        c.Bits[bit >> 6] |= 1UL << (bit & 63);
        if (c.MarkCount == c.Marked.Length) Array.Resize(ref c.Marked, c.Marked.Length * 2);
        c.Marked[c.MarkCount++] = bit;
    }

    static void Push(Cache c, ref int sp, int a, int b, int x)
    {
        if (sp + 3 > c.Stack.Length) Array.Resize(ref c.Stack, c.Stack.Length * 2);
        c.Stack[sp] = a; c.Stack[sp + 1] = b; c.Stack[sp + 2] = x;
        sp += 3;
    }

    // Runs from (pc, pos) to Match/SubEnd. endTarget >= 0: SubEnd must be at that position (look-behind).
    bool Run<T>(ReadOnlySpan<T> h, int pc0, int pos0, int endTarget, Cache c, out int endPos) where T : unmanaged
    {
        int baseSp = c.Sp;
        int marks0 = c.MarkCount;
        Push(c, ref c.Sp, KBranch | (pc0 << 2), pos0, 0);
        var slots = c.Slots;
        var code = this.code;
        endPos = -1;
        while (c.Sp > baseSp)
        {
            c.Sp -= 3;
            int k = c.Stack[c.Sp], pos = c.Stack[c.Sp + 1];
            int kind = k & 3;
            if (kind == KRestore) { slots[k >> 2] = pos; continue; }
            if (kind == KFailMark) { MarkFailed(c, k >> 2, pos); continue; }
            int pc = k >> 2;
            while (true)
            {
                ref readonly var ins = ref code[pc];
                switch (ins.Op)
                {
                    case Op.Char:
                        {
                            int w = Decode(h, pos, out int cp);
                            if (w == 0 || cp != ins.A) goto fail;
                            pos += w; pc++;
                            continue;
                        }
                    case Op.Set:
                        {
                            int w = Decode(h, pos, out int cp);
                            if (w == 0 || !InSet(ins.A, cp)) goto fail;
                            pos += w; pc++;
                            continue;
                        }
                    case Op.Any:
                    case Op.AnyNl:
                        {
                            int w = Decode(h, pos, out int cp);
                            if (w == 0 || (ins.Op == Op.AnyNl && cp == '\n')) goto fail;
                            pos += w; pc++;
                            continue;
                        }
                    case Op.Lit:
                        {
                            if (typeof(T) == typeof(byte))
                            {
                                var lit = lits8[ins.A];
                                var hb = System.Runtime.InteropServices.MemoryMarshal.Cast<T, byte>(h);
                                if (!hb[pos..].StartsWith(lit)) goto fail;
                                pos += lit.Length;
                            }
                            else
                            {
                                var lit = lits[ins.A];
                                var hc = System.Runtime.InteropServices.MemoryMarshal.Cast<T, char>(h);
                                if (!hc[pos..].StartsWith(lit)) goto fail;
                                pos += lit.Length;
                            }
                            pc++;
                            continue;
                        }
                    case Op.Split:
                        if (KnownFailed(c, pc, pos)) goto fail;
                        Push(c, ref c.Sp, KFailMark | (pc << 2), pos, 0);
                        Push(c, ref c.Sp, KBranch | (ins.B << 2), pos, 0);
                        pc = ins.A;
                        continue;
                    case Op.Jmp:
                        pc = ins.A;
                        continue;
                    case Op.Save when (ins.A & 1) == 1:
                        {
                            // a group closes: commit its pending start with this end (backreferences inside an
                            // open group see the last completed capture, as in Onigmo and .NET)
                            int g = ins.A >> 1;
                            Push(c, ref c.Sp, KRestore | ((2 * g) << 2), slots[2 * g], 0);
                            Push(c, ref c.Sp, KRestore | ((2 * g + 1) << 2), slots[2 * g + 1], 0);
                            slots[2 * g] = slots[SlotCount + g];
                            slots[2 * g + 1] = pos;
                            pc++;
                            continue;
                        }
                    case Op.Save:
                    case Op.LoopInit:
                        {
                            int slot = ins.Op == Op.Save ? SlotCount + (ins.A >> 1) : RegBase + ins.A;
                            Push(c, ref c.Sp, KRestore | (slot << 2), slots[slot], 0);
                            slots[slot] = pos;
                            pc++;
                            continue;
                        }
                    case Op.LoopCheck:
                        // an iteration that consumed nothing ends the loop (Onigmo's empty check)
                        if (slots[RegBase + ins.A] == pos) { pc = ins.B; continue; }
                        pc++;
                        continue;
                    case Op.Assert:
                        if (!PikeVm.Look((RegexAssertionKind)ins.A, h, pos, c.SearchStart)) goto fail;
                        pc++;
                        continue;
                    case Op.Look:
                        {
                            bool ok = SubRun(h, pc + 1, pos, -1, c, out _);
                            if (ok == ins.Flag) goto fail;
                            pc = ins.B;
                            continue;
                        }
                    case Op.LookBehind:
                        {
                            bool found = false;
                            foreach (var (bpc, len) in behinds[ins.A])
                            {
                                int st = pos;
                                for (int j = 0; j < len && st >= 0; j++) st = StepBack(h, st);
                                if (st < 0) continue;
                                if (SubRun(h, bpc, st, pos, c, out _)) { found = true; break; }
                            }
                            if (found == ins.Flag) goto fail;
                            pc = ins.B;
                            continue;
                        }
                    case Op.Atomic:
                        {
                            if (!SubRun(h, pc + 1, pos, -1, c, out int e)) goto fail;
                            pos = e;
                            pc = ins.B;
                            continue;
                        }
                    case Op.BackRef:
                        {
                            int gs = slots[2 * ins.A], ge = slots[2 * ins.A + 1];
                            if (gs < 0 || ge < 0) goto fail;
                            int len = ge - gs;
                            if (len < 0) goto fail;
                            if (!ins.Flag)
                            {
                                if (pos + len > h.Length) goto fail;
                                for (int j = 0; j < len; j++) if (Unit(h, gs + j) != Unit(h, pos + j)) goto fail;
                                pos += len;
                            }
                            else
                            {
                                int a = gs, b = pos;
                                while (a < ge)
                                {
                                    int wa = Decode(h, a, out int ca), wb = Decode(h, b, out int cb);
                                    if (wa == 0 || wb == 0 || !CaseFold.Equal(ca, cb)) goto fail;
                                    a += wa; b += wb;
                                }
                                pos = b;
                            }
                            pc++;
                            continue;
                        }
                    case Op.SubEnd:
                    case Op.Match:
                        if (endTarget >= 0 && pos != endTarget) goto fail;
                        endPos = pos;
                        return Succeed(c, baseSp, marks0);
                    default:
                        goto fail;
                }
            }
        fail:;
        }
        return false;
    }

    // success: drop the branch frames above baseSp but keep capture-restore frames for outer backtracking
    bool Succeed(Cache c, int baseSp, int marks0)
    {
        int w = baseSp;
        for (int r = baseSp; r < c.Sp; r += 3)
            if ((c.Stack[r] & 3) == KRestore)
            {
                c.Stack[w] = c.Stack[r]; c.Stack[w + 1] = c.Stack[r + 1]; c.Stack[w + 2] = c.Stack[r + 2];
                w += 3;
            }
        c.Sp = w;
        return true;
    }

    // a sub-match (atomic group, look-around body); its failure marks stay valid (same body, same end condition)
    bool SubRun<T>(ReadOnlySpan<T> h, int pc, int pos, int endTarget, Cache c, out int end) where T : unmanaged =>
        Run(h, pc, pos, endTarget, c, out end);
}
