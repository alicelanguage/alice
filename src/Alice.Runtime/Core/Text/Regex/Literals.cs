using System.Text;

namespace Alice.Core.Text;

/// <summary>
/// Literal extraction for prefilters: the set of strings every match starts with (prefix), or a required literal
/// after a prefix of bounded length (inner), chosen by a byte-rarity heuristic (as in Rust's regex-syntax).
/// </summary>
internal static class Literals
{
    const int MaxLits = 64, MaxLen = 16;

    /// <summary>Literal strings (scalars) in priority order; null = unknown/infinite. Exact: the set is the language.</summary>
    internal readonly record struct Seq(List<string>? Lits, bool Exact)
    {
        public static Seq Infinite => new(null, false);
        public static Seq EmptyExact => new([""], true);
    }

    public static Seq Prefixes(RegexNode n)
    {
        switch (n)
        {
            case RegexNode.Empty:
                return Seq.EmptyExact;
            case RegexNode.Literal { IgnoreCase: false } l:
                return l.Text.Length > MaxLen ? new([Truncate(l.Text)], false) : new([l.Text], true);
            case RegexNode.Literal l:
                {
                    var acc = new List<string> { "" };
                    int count = 0;
                    foreach (var r in l.Text.EnumerateRunes())
                    {
                        var vs = CaseFold.Equivalents(r.Value) ?? [r.Value];
                        if (acc.Count * vs.Length > MaxLits || ++count > MaxLen) return new(acc, false);
                        var next = new List<string>(acc.Count * vs.Length);
                        foreach (var a in acc) foreach (int v in vs) next.Add(a + char.ConvertFromUtf32(v));
                        acc = next;
                    }
                    return new(acc, true);
                }
            case RegexNode.AnyChar:
                return Seq.Infinite;
            case RegexNode.CharClass c:
                {
                    var set = RegexSyntax.Resolve(c);
                    if (RangeSet.Count(set) > 16) return Seq.Infinite;
                    var l = new List<string>();
                    foreach (var r in set) for (int x = r.First; x <= r.Last; x++) l.Add(char.ConvertFromUtf32(x));
                    return l.Count == 0 ? Seq.Infinite : new(l, true);
                }
            case RegexNode.Concat c:
                return Concat(c.Items.AsSpan());
            case RegexNode.Alternation a:
                {
                    var l = new List<string>();
                    var seen = new HashSet<string>();
                    bool exact = true;
                    foreach (var alt in a.Alternatives)
                    {
                        var p = Prefixes(alt);
                        if (p.Lits == null) return Seq.Infinite;
                        exact &= p.Exact;
                        foreach (var s in p.Lits) if (seen.Add(s)) l.Add(s);
                        if (l.Count > MaxLits) return Seq.Infinite;
                    }
                    return new(l, exact);
                }
            case RegexNode.Repeat r:
                {
                    if (r.Min == 0) return new([""], false);
                    var p = Prefixes(r.Body);
                    if (p.Lits == null) return Seq.Infinite;
                    if (r.Min == 1 && r.Max == 1) return p;
                    if (p.Exact && r.Min == r.Max && r.Min <= 8)
                    {
                        var items = Enumerable.Repeat(r.Body, r.Min).ToArray();
                        return Concat(items);
                    }
                    if (p.Exact && r.Min <= 8 && r.Mode == RegexGreediness.Greedy)
                    {
                        var items = Enumerable.Repeat(r.Body, r.Min).ToArray();
                        var q = Concat(items);
                        return q with { Exact = false };
                    }
                    return p with { Exact = false };
                }
            case RegexNode.Capture c:
                return Prefixes(c.Body);
            case RegexNode.FlagScope f:
                return Prefixes(f.Body);
            case RegexNode.Atomic a:
                return Prefixes(a.Body) with { Exact = false };
            case RegexNode.Assertion or RegexNode.Lookaround:
                return new([""], false);
            default:
                return Seq.Infinite;
        }
    }

    static string Truncate(string s)
    {
        int n = MaxLen;
        if (char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }

    static Seq Concat(ReadOnlySpan<RegexNode> items)
    {
        var acc = new List<string> { "" };
        bool exact = true;
        foreach (var it in items)
        {
            var p = Prefixes(it);
            if (p.Lits == null) { exact = false; break; }
            if (acc.Count * p.Lits.Count > MaxLits) { exact = false; break; }
            var next = new List<string>(acc.Count * p.Lits.Count);
            var seen = new HashSet<string>();
            bool tooLong = false;
            foreach (var a in acc)
                foreach (var b in p.Lits)
                {
                    var s = a + b;
                    if (s.Length > MaxLen) { s = Truncate(s); tooLong = true; }
                    if (seen.Add(s)) next.Add(s);
                }
            acc = next;
            if (tooLong) { exact = false; break; }
            if (!p.Exact) { exact = false; break; }
        }
        return new(acc, exact);
    }

    /// <summary>Maximum length in code units of any match of <paramref name="n"/>; null when unbounded.</summary>
    public static int? MaxUnits(RegexNode n, bool utf8)
    {
        int unitsPerScalar(int c) => utf8 ? new Rune(c).Utf8SequenceLength : (c > 0xFFFF ? 2 : 1);
        switch (n)
        {
            case RegexNode.Empty or RegexNode.Assertion or RegexNode.Lookaround: return 0;
            case RegexNode.Literal l:
                {
                    int s = 0;
                    foreach (var r in l.Text.EnumerateRunes())
                    {
                        var vs = l.IgnoreCase ? CaseFold.Equivalents(r.Value) ?? [r.Value] : [r.Value];
                        s += vs.Max(unitsPerScalar);
                    }
                    return s;
                }
            case RegexNode.AnyChar: return utf8 ? 4 : 2;
            case RegexNode.CharClass c:
                {
                    var set = RegexSyntax.Resolve(c);
                    return set.Count == 0 ? 0 : unitsPerScalar(set[^1].Last);
                }
            case RegexNode.Concat c:
                {
                    int s = 0;
                    foreach (var x in c.Items) { if (MaxUnits(x, utf8) is not { } v) return null; s += v; }
                    return s;
                }
            case RegexNode.Alternation a:
                {
                    int m = 0;
                    foreach (var x in a.Alternatives) { if (MaxUnits(x, utf8) is not { } v) return null; m = Math.Max(m, v); }
                    return m;
                }
            case RegexNode.Repeat r:
                return r.Max < 0 ? null : MaxUnits(r.Body, utf8) is { } b ? (int)Math.Min((long)b * r.Max, 1 << 20) : null;
            case RegexNode.Capture c: return MaxUnits(c.Body, utf8);
            case RegexNode.FlagScope f: return MaxUnits(f.Body, utf8);
            case RegexNode.Atomic a: return MaxUnits(a.Body, utf8);
            default: return null;
        }
    }

    /// <summary>Approximate frequency of a unit in text (English letters, punctuation, rare non-ASCII), for
    /// choosing the most selective literal.</summary>
    internal static double Freq(int c)
    {
        if (c >= 0x80) return 0.0005;
        if (c is >= 'A' and <= 'Z') return c is 'T' or 'A' or 'I' or 'S' or 'H' or 'W' ? 0.004 : 0.002;
        return c switch
        {
            ' ' => 0.17, 'e' => 0.09, 't' => 0.065, 'a' => 0.06, 'o' => 0.06, 'i' => 0.055, 'n' => 0.055, 's' => 0.05,
            'h' => 0.045, 'r' => 0.045, 'd' => 0.03, 'l' => 0.03, 'u' => 0.02, 'c' => 0.02, 'm' => 0.02, '\n' => 0.02,
            'w' or 'f' or 'g' or 'y' or 'p' => 0.015, 'b' => 0.012, ',' or '.' => 0.01, 'v' => 0.008, 'k' => 0.006,
            '\'' or '"' or '-' => 0.003, >= '0' and <= '9' => 0.002, 'x' or 'j' or 'q' or 'z' => 0.001, < 0x20 => 0.0001,
            _ => 0.001,
        };
    }

    /// <summary>Selectivity in bits (-log2 of the chance that a position starts one of the literals); higher is better.</summary>
    internal static double Bits(List<string> lits)
    {
        double sum = 0;
        foreach (var s in lits)
        {
            double p = 1;
            for (int k = 0; k < s.Length && k < 8; k++) p *= Freq(s[k]);
            sum += p;
        }
        return -Math.Log2(Math.Min(1, sum));
    }

    /// <summary>Selectivity of an ASCII case-insensitive literal.</summary>
    internal static double CaselessBits(string run)
    {
        double p = 1;
        for (int k = 0; k < run.Length && k < 8; k++)
        {
            char c = run[k];
            p *= char.IsAsciiLetter(c) ? Freq(char.ToLowerInvariant(c)) + Freq(char.ToUpperInvariant(c)) : Freq(c);
        }
        return -Math.Log2(Math.Min(1, p));
    }
}
