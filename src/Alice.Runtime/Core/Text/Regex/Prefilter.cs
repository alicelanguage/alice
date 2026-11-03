using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Alice.Core.Text;

/// <summary>
/// A fast scan for match candidates. Prefix prefilters return the start of a candidate match; inner ones
/// (<see cref="Offset"/> &gt; 0) return a required literal that follows a prefix of at most Offset units, so the
/// match starts in [hit - Offset, hit]. <see cref="Exact"/>: the hit is the whole leftmost-first match (length out).
/// </summary>
internal abstract class Prefilter<T> where T : unmanaged
{
    public bool Exact { get; init; }
    public int Offset { get; init; }

    /// <summary>Relative scan speed (higher is faster), weighed against selectivity when choosing a prefilter.</summary>
    public virtual double Speed => 0;

    /// <summary>The first candidate at or after <paramref name="pos"/>, or -1. <paramref name="len"/>: match length when Exact.</summary>
    public abstract int Find(ReadOnlySpan<T> h, int pos, out int len);
}

internal static class Prefilter
{
    const double MinBits = 5;   // a candidate rate above 1/32 positions is not worth a prefilter (unless exact)
    const double CapBits = 16;

    /// <summary>Picks the best prefix or inner literal prefilter for <paramref name="p"/>, or null.</summary>
    public static Prefilter<T>? Build<T>(RegexPattern p, bool allowExact) where T : unmanaged
    {
        bool utf8 = typeof(T) == typeof(byte);
        var root = p.Root;
        while (root is RegexNode.Capture c0) root = c0.Body;
        while (root is RegexNode.FlagScope f0) root = f0.Body;
        var items = root switch
        {
            RegexNode.Concat c => Explode(c.Items),
            RegexNode.Literal l => Explode([l]),
            _ => [root],
        };
        Prefilter<T>? best = null;
        double bestScore = MinBits - 0.001;
        // selectivity (bits) above CapBits no longer changes the candidate rate much; the scan speed decides
        static double Score(double bits, Prefilter<T> pf) =>
            (pf.Exact ? 1000 : 0) + (bits < MinBits ? bits : Math.Min(bits, CapBits) + pf.Speed);

        var pre = Literals.Prefixes(root);
        if (pre.Lits is { Count: > 0 } pl && !pl.Contains(""))
        {
            bool exact = allowExact && pre.Exact;
            double bits = Literals.Bits(pl) + 1;
            if (Make<T>(pl, exact, 0) is { } pf && Score(bits, pf) > bestScore) { best = pf; bestScore = Score(bits, pf); }
        }
        if (items.Count > 0 && items[0] is RegexNode.Literal { IgnoreCase: true } && CaselessRun(items, 0) is { } run0)
        {
            double bits = Literals.CaselessBits(run0) + 1;
            bool exact = allowExact && run0.Length == items.Count;   // the run is the whole pattern
            if (Caseless<T>(run0, 0, exact) is { } pf && Score(bits, pf) > bestScore) { best = pf; bestScore = Score(bits, pf); }
        }
        // inner literals after a bounded prefix: the match starts in [hit - off, hit] (off + 1 anchored attempts)
        for (int k = 1; k < items.Count && k < 16; k++)
        {
            if (Literals.MaxUnits(new RegexNode.Concat(items.Take(k).ToImmutableArray()), utf8) is not { } off || off > 64) break;
            double cost = Math.Log2(off + 1);
            var rest = new RegexNode.Concat(items.Skip(k).ToImmutableArray());
            var inner = Literals.Prefixes(rest);
            if (inner.Lits is { Count: > 0 } il && !il.Contains(""))
            {
                double bits = Literals.Bits(il) - cost;
                if (Make<T>(il, false, off) is { } pf && Score(bits, pf) > bestScore) { best = pf; bestScore = Score(bits, pf); }
            }
            if (items[k] is RegexNode.Literal { IgnoreCase: true } && CaselessRun(items, k) is { } run)
            {
                double bits = Literals.CaselessBits(run) - cost;
                if (Caseless<T>(run, off, false) is { } pf && Score(bits, pf) > bestScore) { best = pf; bestScore = Score(bits, pf); }
            }
        }
        // UTF-16, the pattern is one case-insensitive literal whose case variants are all single units: find its best
        // ASCII run, then compare the whole literal by simple case folding (exact, no DFA run per hit)
        if (typeof(T) == typeof(char) && allowExact && WholeCaselessLiteral(items) is { } full)
        {
            (string Run, int K, double Bits)? pick = null;
            for (int k = 0; k < items.Count; k++)
                if (CaselessRun(items, k) is { } run && Literals.CaselessBits(run) is var b && (pick == null || b > pick.Value.Bits))
                    pick = (run, k, b);
            if (pick is { } p0)
            {
                var pf = (Prefilter<T>)(object)new CaselessLiteralChars(full, p0.Run, p0.K) { Exact = true };
                if (Score(p0.Bits, pf) > bestScore) { best = pf; bestScore = Score(p0.Bits, pf); }
            }
        }
        return best;
    }

    // the text of a pattern made only of case-insensitive scalars whose case variants are all one UTF-16 unit, or null
    static string? WholeCaselessLiteral(List<RegexNode> items)
    {
        var sb = new StringBuilder();
        foreach (var it in items)
        {
            if (it is not RegexNode.Literal { IgnoreCase: true } l || l.Text.Length != 1) return null;
            if (CaseFold.Equivalents(l.Text[0]) is { } vs && vs.Any(v => v > 0xFFFF)) return null;
            sb.Append(l.Text[0]);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    // exact search of a case-insensitive literal over UTF-16: its ASCII run (at offset k) by .NET's packed
    // OrdinalIgnoreCase search, then every unit compared by simple case folding (ſ, the Kelvin sign...)
    sealed class CaselessLiteralChars(string lit, string run, int k) : Prefilter<char>
    {
        readonly SearchValues<string> sv = SearchValues.Create([run], StringComparison.OrdinalIgnoreCase);
        public override double Speed => 2.7;
        public override int Find(ReadOnlySpan<char> h, int pos, out int len)
        {
            len = lit.Length;
            int from = pos + k;
            while (from <= h.Length)
            {
                int r = h[from..].IndexOfAny(sv);
                if (r < 0) return -1;
                int at = from + r - k;
                if (at + lit.Length <= h.Length && Matches(h.Slice(at, lit.Length))) return at;
                from += r + 1;
            }
            return -1;
        }

        bool Matches(ReadOnlySpan<char> s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] != lit[i] && !CaseFold.Equal(s[i], lit[i])) return false;
            return true;
        }
    }

    // one node per scalar for literals (inner candidates may start inside a literal)
    static List<RegexNode> Explode(ImmutableArray<RegexNode> items)
    {
        var o = new List<RegexNode>();
        foreach (var it in items)
        {
            if (it is RegexNode.Literal l)
                foreach (var r in l.Text.EnumerateRunes()) o.Add(new RegexNode.Literal(r.ToString(), l.IgnoreCase));
            else o.Add(it);
        }
        return o;
    }

    // the longest run (>= 3) of case-insensitive single-scalar literals starting at k whose case variants are all ASCII
    static string? CaselessRun(List<RegexNode> items, int k)
    {
        var sb = new StringBuilder();
        bool letter = false;
        for (int j = k; j < items.Count && sb.Length < 16; j++)
        {
            if (items[j] is not RegexNode.Literal { IgnoreCase: true } l || l.Text.Length != 1) break;
            int c = l.Text[0];
            if (!CaseFold.AsciiOnlyVariants(c)) break;
            letter |= char.IsAsciiLetter((char)c);
            sb.Append((char)c);
        }
        return sb.Length >= 3 && letter ? sb.ToString() : null;
    }

    // an ASCII case-insensitive run (CaselessRun); exact when it is the whole pattern
    static Prefilter<T>? Caseless<T>(string run, int off, bool exact) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
            return OffsetScan<byte>.Create([Encoding.ASCII.GetBytes(run)], true) is { } sb
                ? (Prefilter<T>)(object)new ScanBytes(sb, 2.6) { Offset = off, Exact = exact } : null;   // one literal to verify
        return (Prefilter<T>)(object)new CaselessChars(run) { Offset = off, Exact = exact };
    }

    // UTF-16: .NET's packed single-string search (SearchValues, OrdinalIgnoreCase), which also matches a few
    // non-ASCII simple-uppercase images (`ı` for `i`): every hit is re-checked ASCII case-insensitively
    sealed class CaselessChars(string run) : Prefilter<char>
    {
        readonly SearchValues<string> sv = SearchValues.Create([run], StringComparison.OrdinalIgnoreCase);
        public override double Speed => 2.6;
        public override int Find(ReadOnlySpan<char> h, int pos, out int len)
        {
            len = run.Length;
            while (pos <= h.Length)
            {
                int r = h[pos..].IndexOfAny(sv);
                if (r < 0) return -1;
                int at = pos + r;
                if (System.Text.Ascii.EqualsIgnoreCase(h.Slice(at, run.Length), run)) return at;
                pos = at + 1;
            }
            return -1;
        }
    }

    static Prefilter<T>? Make<T>(List<string> lits, bool exact, int off) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            var bs = lits.Select(Encoding.UTF8.GetBytes).ToList();
            if (bs.Count == 1) return (Prefilter<T>)(object)new SingleBytes(bs[0]) { Exact = exact, Offset = off };
            if (!exact && bs.All(b => b.Length >= 1) && bs.Select(b => b[0]).Distinct().Count() <= 3 && bs.Min(b => b.Length) <= 2)
                return (Prefilter<T>)(object)new FirstUnitBytes(bs.Select(b => b[0]).Distinct().ToArray()) { Offset = off };
            if (bs.All(b => b.Length == 1))
                return (Prefilter<T>)(object)new UnitSetBytes(bs.Select(b => b[0]).ToArray()) { Exact = exact, Offset = off };
            if (OffsetScan<byte>.Create(bs, false) is { } sb)
                return (Prefilter<T>)(object)new ScanBytes(sb, 2.5) { Exact = exact, Offset = off };
            return (Prefilter<T>)(object)new TeddyPrefilter(bs) { Exact = exact, Offset = off };
        }
        else
        {
            var cs = lits.Select(s => s.Select(ch => (ushort)ch).ToArray()).ToList();
            if (lits.Count == 1) return (Prefilter<T>)(object)new SingleChars(lits[0]) { Exact = exact, Offset = off };
            if (!exact && lits.Select(s => s[0]).Distinct().Count() <= 3 && lits.Min(s => s.Length) <= 2)
                return (Prefilter<T>)(object)new FirstUnitChars(lits.Select(s => s[0]).Distinct().ToArray()) { Offset = off };
            if (lits.All(s => s.Length == 1))
                return (Prefilter<T>)(object)new UnitSetChars(lits.Select(s => s[0]).ToArray()) { Exact = exact, Offset = off };
            if (OffsetScan<ushort>.Create(cs, false) is { } sc)
                return (Prefilter<T>)(object)new ScanChars(sc, 2.5) { Exact = exact, Offset = off };
            return (Prefilter<T>)(object)new StringSetChars(lits) { Exact = exact, Offset = off };
        }
    }

    sealed class ScanBytes(OffsetScan<byte> scan, double speed) : Prefilter<byte>
    {
        public override double Speed => speed;
        public override int Find(ReadOnlySpan<byte> h, int pos, out int len) => scan.Find(h, pos, out len);
    }

    sealed class ScanChars(OffsetScan<ushort> scan, double speed) : Prefilter<char>
    {
        public override double Speed => speed;
        public override int Find(ReadOnlySpan<char> h, int pos, out int len) => scan.Find(MemoryMarshal.Cast<char, ushort>(h), pos, out len);
    }

    sealed class SingleBytes(byte[] lit) : Prefilter<byte>
    {
        public override double Speed => 3;
        public override int Find(ReadOnlySpan<byte> h, int pos, out int len)
        {
            len = lit.Length;
            if (pos > h.Length) return -1;
            int r = lit.Length == 1 ? h[pos..].IndexOf(lit[0]) : h[pos..].IndexOf(lit);
            return r < 0 ? -1 : pos + r;
        }
    }

    sealed class SingleChars(string lit) : Prefilter<char>
    {
        public override double Speed => 3;
        public override int Find(ReadOnlySpan<char> h, int pos, out int len)
        {
            len = lit.Length;
            if (pos > h.Length) return -1;
            int r = lit.Length == 1 ? h[pos..].IndexOf(lit[0]) : h[pos..].IndexOf(lit);
            return r < 0 ? -1 : pos + r;
        }
    }

    // candidates by first unit only (cheap when few distinct first units)
    sealed class FirstUnitChars(char[] firsts) : Prefilter<char>
    {
        readonly SearchValues<char> sv = SearchValues.Create(firsts);
        public override double Speed => 1.5;
        public override int Find(ReadOnlySpan<char> h, int pos, out int len)
        {
            len = -1;
            if (pos > h.Length) return -1;
            int r = h[pos..].IndexOfAny(sv);
            return r < 0 ? -1 : pos + r;
        }
    }

    // candidates by first byte only (cheap when few distinct first bytes)
    sealed class FirstUnitBytes(byte[] firsts) : Prefilter<byte>
    {
        public override double Speed => 1.5;
        readonly SearchValues<byte> sv = SearchValues.Create(firsts);
        public override int Find(ReadOnlySpan<byte> h, int pos, out int len)
        {
            len = -1;
            if (pos > h.Length) return -1;
            int r = h[pos..].IndexOfAny(sv);
            return r < 0 ? -1 : pos + r;
        }
    }

    sealed class UnitSetBytes(byte[] units) : Prefilter<byte>
    {
        public override double Speed => 1.5;
        readonly SearchValues<byte> sv = SearchValues.Create(units);
        public override int Find(ReadOnlySpan<byte> h, int pos, out int len)
        {
            len = 1;
            if (pos > h.Length) return -1;
            int r = h[pos..].IndexOfAny(sv);
            return r < 0 ? -1 : pos + r;
        }
    }

    sealed class UnitSetChars(char[] units) : Prefilter<char>
    {
        public override double Speed => 1.5;
        readonly SearchValues<char> sv = SearchValues.Create(units);
        public override int Find(ReadOnlySpan<char> h, int pos, out int len)
        {
            len = 1;
            if (pos > h.Length) return -1;
            int r = h[pos..].IndexOfAny(sv);
            return r < 0 ? -1 : pos + r;
        }
    }

    sealed class StringSetChars(List<string> lits) : Prefilter<char>
    {
        public override double Speed => lits.Count <= 8 ? 1 : 0;
        readonly SearchValues<string> sv = SearchValues.Create(lits.Distinct().ToArray(), StringComparison.Ordinal);
        readonly string[] ordered = lits.ToArray();
        public override int Find(ReadOnlySpan<char> h, int pos, out int len)
        {
            len = -1;
            if (pos > h.Length) return -1;
            int r = h[pos..].IndexOfAny(sv);
            if (r < 0) return -1;
            int at = pos + r;
            var rest = h[at..];
            foreach (var s in ordered) if (rest.StartsWith(s)) { len = s.Length; break; }
            return at;
        }
    }

    sealed class TeddyPrefilter(List<byte[]> lits) : Prefilter<byte>
    {
        public override double Speed => lits.Count <= 8 ? 1 : 0;
        readonly Teddy teddy = new(lits);
        public override int Find(ReadOnlySpan<byte> h, int pos, out int len)
        {
            len = -1;
            if (pos > h.Length) return -1;
            int at = teddy.Find(h, pos, out int lit);
            if (at >= 0) len = lits[lit].Length;
            return at;
        }
    }
}

/// <summary>
/// Candidate search for a literal set by two fixed offsets (the rarest units by text frequency, at most 3 distinct
/// units per offset), compared 64 (AVX-512) or 32 (AVX2) bytes per step as `IndexOf(string)` does with its first
/// and last unit. Caseless (one ASCII literal): letters are compared after OR 0x20. Candidates are verified against
/// the literals in pattern order (leftmost-first) or ASCII case-insensitively.
/// </summary>
internal sealed class OffsetScan<U> where U : unmanaged, IBinaryInteger<U>
{
    readonly U[][] lits;
    readonly bool caseless;
    readonly int minLen, k0, k1, n0, n1;   // offsets (k0 < k1) and their distinct target counts
    readonly U[] t;                        // targets: t[0..3] at k0, t[3..6] at k1 (repeated when fewer)
    readonly U m0, m1;                     // OR masks (0x20 for caseless letters)
    readonly bool packed;                  // UTF-16 units scanned as bytes (all targets below 256)

    OffsetScan(U[][] lits, bool caseless, (int K, U Mask, U[] Set) a, (int K, U Mask, U[] Set) b)
    {
        this.lits = lits;
        this.caseless = caseless;
        minLen = lits.Min(l => l.Length);
        (k0, m0, n0) = (a.K, a.Mask, a.Set.Length);
        (k1, m1, n1) = (b.K, b.Mask, b.Set.Length);
        t = new U[6];
        for (int q = 0; q < 3; q++)
        {
            t[q] = a.Set[Math.Min(q, a.Set.Length - 1)];
            t[3 + q] = b.Set[Math.Min(q, b.Set.Length - 1)];
        }
        // saturating packs map units above 255 to 255 and above 0x7FFF to 0: neither may be a target
        packed = typeof(U) == typeof(ushort) && t.All(u => int.CreateTruncating(u) is > 0 and < 255);
    }

    /// <summary>A scan over the two rarest offsets, or null when the literals are shorter than 2 units or the pair is
    /// not selective enough for the verification cost.</summary>
    public static OffsetScan<U>? Create(List<U[]> lits, bool caseless)
    {
        int min = lits.Min(l => l.Length);
        if (min < 2 || (caseless && lits.Count != 1)) return null;
        var cand = new List<(int K, U Mask, U[] Set, double F)>();
        for (int k = 0; k < min && k < 32; k++)
        {
            if (caseless)
            {
                int c = int.CreateTruncating(lits[0][k]);
                bool letter = char.IsAsciiLetter((char)c);
                var target = U.CreateTruncating(letter ? c | 0x20 : c);
                double f = letter ? Literals.Freq(c | 0x20) + Literals.Freq(c & ~0x20) : Literals.Freq(c);
                cand.Add((k, U.CreateTruncating(letter ? 0x20 : 0), [target], f));
                continue;
            }
            var set = lits.Select(l => l[k]).Distinct().ToArray();
            if (set.Length > 3) continue;
            cand.Add((k, U.Zero, set, set.Sum(u => Literals.Freq(int.CreateTruncating(u)))));
        }
        if (cand.Count < 2) return null;
        // the rarest offset, then the rarest one not adjacent to it (adjacent units are correlated: "ck", "th")
        var byF = cand.OrderBy(c => c.F).ThenByDescending(c => c.K).ToList();
        var second = byF.Skip(1).FirstOrDefault(c => Math.Abs(c.K - byF[0].K) >= 2);
        if (second.Set == null || second.F > 8 * byF[1].F) second = byF[1];
        var two = new[] { byF[0], second }.OrderBy(c => c.K).ToArray();
        // a candidate costs one comparison per literal: few literals afford a candidate every 256 positions
        if (two[0].F * two[1].F > (lits.Count <= 4 ? 1.0 / 256 : 1.0 / 4096)) return null;
        return new OffsetScan<U>(lits.ToArray(), caseless, (two[0].K, two[0].Mask, two[0].Set), (two[1].K, two[1].Mask, two[1].Set));
    }

    bool Verify(ReadOnlySpan<U> h, int p, out int len)
    {
        var rest = h[p..];
        if (caseless)
        {
            var l = lits[0];
            len = l.Length;
            if (rest.Length < l.Length) return false;
            return typeof(U) == typeof(byte)
                ? System.Text.Ascii.EqualsIgnoreCase(MemoryMarshal.Cast<U, byte>(rest[..l.Length]), MemoryMarshal.Cast<U, byte>(l.AsSpan()))
                : System.Text.Ascii.EqualsIgnoreCase(MemoryMarshal.Cast<U, char>(rest[..l.Length]), MemoryMarshal.Cast<U, char>(l.AsSpan()));
        }
        foreach (var l in lits) if (rest.StartsWith(l)) { len = l.Length; return true; }
        len = -1;
        return false;
    }

    // The vector loops have no calls (a call would spill every vector register on each step): they return the
    // candidate bits of the first step with any, or 0 at the end with i at the first unscanned position.
    [MethodImpl(MethodImplOptions.NoInlining)]
    ulong Next512(ref U h0, int n, ref int i)
    {
        int o0 = k0, o1 = k1, j = i;
        var a0 = Vector512.Create(t[0]); var a1 = Vector512.Create(t[1]); var a2 = Vector512.Create(t[2]);
        var b0 = Vector512.Create(t[3]); var b1 = Vector512.Create(t[4]); var b2 = Vector512.Create(t[5]);
        var ma = Vector512.Create(m0); var mb = Vector512.Create(m1);
        if (n0 == 1 && n1 == 1)   // one compare per offset (port 5 bound: no redundant compares)
        {
            while (j + o1 + Vector512<U>.Count <= n)
            {
                var m = Vector512.Equals(Vector512.LoadUnsafe(ref h0, (nuint)(j + o0)) | ma, a0)
                      & Vector512.Equals(Vector512.LoadUnsafe(ref h0, (nuint)(j + o1)) | mb, b0);
                if (m != Vector512<U>.Zero) { i = j; return m.ExtractMostSignificantBits(); }
                j += Vector512<U>.Count;
            }
            i = j;
            return 0;
        }
        while (j + o1 + Vector512<U>.Count <= n)
        {
            var x = Vector512.LoadUnsafe(ref h0, (nuint)(j + o0)) | ma;
            var y = Vector512.LoadUnsafe(ref h0, (nuint)(j + o1)) | mb;
            var m = (Vector512.Equals(x, a0) | Vector512.Equals(x, a1) | Vector512.Equals(x, a2))
                  & (Vector512.Equals(y, b0) | Vector512.Equals(y, b1) | Vector512.Equals(y, b2));
            if (m != Vector512<U>.Zero) { i = j; return m.ExtractMostSignificantBits(); }
            j += Vector512<U>.Count;
        }
        i = j;
        return 0;
    }

    // UTF-16 with every target below 256: 64 units per step narrowed to bytes (truncation only adds candidates)
    [MethodImpl(MethodImplOptions.NoInlining)]
    ulong NextPacked(ref U h0, int n, ref int i)
    {
        ref ushort c = ref Unsafe.As<U, ushort>(ref h0);
        int o0 = k0, o1 = k1, j = i;
        var a0 = Vector512.Create(byte.CreateTruncating(t[0])); var a1 = Vector512.Create(byte.CreateTruncating(t[1])); var a2 = Vector512.Create(byte.CreateTruncating(t[2]));
        var b0 = Vector512.Create(byte.CreateTruncating(t[3])); var b1 = Vector512.Create(byte.CreateTruncating(t[4])); var b2 = Vector512.Create(byte.CreateTruncating(t[5]));
        var ma = Vector512.Create(byte.CreateTruncating(m0)); var mb = Vector512.Create(byte.CreateTruncating(m1));
        if (n0 == 1 && n1 == 1 && Avx512BW.IsSupported)
        {
            // vpackuswb: one uop per 64 units, lanes interleaved (a0 b0 a1 b1 ...); the hit mask is put back in
            // order with vpermq only when a step has a candidate
            while (j + o1 + 64 <= n)
            {
                var x = Avx512BW.PackUnsignedSaturate(Vector512.LoadUnsafe(ref c, (nuint)(j + o0)).AsInt16(), Vector512.LoadUnsafe(ref c, (nuint)(j + o0 + 32)).AsInt16());
                var y = Avx512BW.PackUnsignedSaturate(Vector512.LoadUnsafe(ref c, (nuint)(j + o1)).AsInt16(), Vector512.LoadUnsafe(ref c, (nuint)(j + o1 + 32)).AsInt16());
                var m = Vector512.Equals(x | ma, a0) & Vector512.Equals(y | mb, b0);
                if (m != Vector512<byte>.Zero)
                {
                    i = j;
                    return Avx512F.PermuteVar8x64(m.AsUInt64(), Vector512.Create(0UL, 2, 4, 6, 1, 3, 5, 7)).AsByte().ExtractMostSignificantBits();
                }
                j += 64;
            }
            i = j;
            return 0;
        }
        if (n0 == 1 && n1 == 1)
        {
            while (j + o1 + 64 <= n)
            {
                var m = Vector512.Equals(Vector512.Narrow(Vector512.LoadUnsafe(ref c, (nuint)(j + o0)), Vector512.LoadUnsafe(ref c, (nuint)(j + o0 + 32))) | ma, a0)
                      & Vector512.Equals(Vector512.Narrow(Vector512.LoadUnsafe(ref c, (nuint)(j + o1)), Vector512.LoadUnsafe(ref c, (nuint)(j + o1 + 32))) | mb, b0);
                if (m != Vector512<byte>.Zero) { i = j; return m.ExtractMostSignificantBits(); }
                j += 64;
            }
            i = j;
            return 0;
        }
        while (j + o1 + 64 <= n)
        {
            var x = Vector512.Narrow(Vector512.LoadUnsafe(ref c, (nuint)(j + o0)), Vector512.LoadUnsafe(ref c, (nuint)(j + o0 + 32))) | ma;
            var y = Vector512.Narrow(Vector512.LoadUnsafe(ref c, (nuint)(j + o1)), Vector512.LoadUnsafe(ref c, (nuint)(j + o1 + 32))) | mb;
            var m = (Vector512.Equals(x, a0) | Vector512.Equals(x, a1) | Vector512.Equals(x, a2))
                  & (Vector512.Equals(y, b0) | Vector512.Equals(y, b1) | Vector512.Equals(y, b2));
            if (m != Vector512<byte>.Zero) { i = j; return m.ExtractMostSignificantBits(); }
            j += 64;
        }
        i = j;
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    ulong Next256(ref U h0, int n, ref int i)
    {
        int o0 = k0, o1 = k1, j = i;
        var a0 = Vector256.Create(t[0]); var a1 = Vector256.Create(t[1]); var a2 = Vector256.Create(t[2]);
        var b0 = Vector256.Create(t[3]); var b1 = Vector256.Create(t[4]); var b2 = Vector256.Create(t[5]);
        var ma = Vector256.Create(m0); var mb = Vector256.Create(m1);
        if (n0 == 1 && n1 == 1)   // one compare per offset (port 5 bound: no redundant compares)
        {
            while (j + o1 + Vector256<U>.Count <= n)
            {
                var m = Vector256.Equals(Vector256.LoadUnsafe(ref h0, (nuint)(j + o0)) | ma, a0)
                      & Vector256.Equals(Vector256.LoadUnsafe(ref h0, (nuint)(j + o1)) | mb, b0);
                if (m != Vector256<U>.Zero) { i = j; return m.ExtractMostSignificantBits(); }
                j += Vector256<U>.Count;
            }
            i = j;
            return 0;
        }
        while (j + o1 + Vector256<U>.Count <= n)
        {
            var x = Vector256.LoadUnsafe(ref h0, (nuint)(j + o0)) | ma;
            var y = Vector256.LoadUnsafe(ref h0, (nuint)(j + o1)) | mb;
            var m = (Vector256.Equals(x, a0) | Vector256.Equals(x, a1) | Vector256.Equals(x, a2))
                  & (Vector256.Equals(y, b0) | Vector256.Equals(y, b1) | Vector256.Equals(y, b2));
            if (m != Vector256<U>.Zero) { i = j; return m.ExtractMostSignificantBits(); }
            j += Vector256<U>.Count;
        }
        i = j;
        return 0;
    }

    public int Find(ReadOnlySpan<U> h, int pos, out int len)
    {
        len = -1;
        int n = h.Length;
        if (pos > n) return -1;
        int i = pos;
        ref U h0 = ref MemoryMarshal.GetReference(h);
        bool v512 = Vector512.IsHardwareAccelerated;
        if (v512 || Vector256.IsHardwareAccelerated)
        {
            bool pk = packed && v512;
            int w = pk ? 64 : v512 ? Vector512<U>.Count : Vector256<U>.Count;
            while (true)
            {
                ulong bits = pk ? NextPacked(ref h0, n, ref i) : v512 ? Next512(ref h0, n, ref i) : Next256(ref h0, n, ref i);
                if (bits == 0) break;
                while (bits != 0)
                {
                    int j = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (Verify(h, i + j, out len)) return i + j;
                }
                i += w;
            }
        }
        for (; i + minLen <= n; i++)
        {
            U x = h[i + k0] | m0, y = h[i + k1] | m1;
            if ((x == t[0] || x == t[1] || x == t[2]) && (y == t[3] || y == t[4] || y == t[5]) && Verify(h, i, out len)) return i;
        }
        len = -1;
        return -1;
    }
}
