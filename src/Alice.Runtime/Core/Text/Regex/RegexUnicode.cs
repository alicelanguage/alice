using System.Globalization;
using System.Text;

namespace Alice.Core.Text;

/// <summary>Range-set algebra over Unicode scalar values (sorted, non-overlapping, inclusive ranges).</summary>
internal static class RangeSet
{
    public const int MaxScalar = 0x10FFFF;

    /// <summary>Sorts and merges overlapping or adjacent ranges.</summary>
    public static List<RegexRange> Normalize(List<RegexRange> r)
    {
        if (r.Count <= 1) return r;
        r.Sort(static (a, b) => a.First.CompareTo(b.First));
        var o = new List<RegexRange>(r.Count);
        var cur = r[0];
        for (int i = 1; i < r.Count; i++)
        {
            var x = r[i];
            if (x.First <= cur.Last + 1) { if (x.Last > cur.Last) cur = new(cur.First, x.Last); }
            else { o.Add(cur); cur = x; }
        }
        o.Add(cur);
        return o;
    }

    public static List<RegexRange> Union(IEnumerable<RegexRange> a, IEnumerable<RegexRange> b)
    {
        var l = new List<RegexRange>(a);
        l.AddRange(b);
        return Normalize(l);
    }

    /// <summary>All scalars not in <paramref name="a"/> (normalized input); never contains surrogate code points.</summary>
    public static List<RegexRange> Complement(IReadOnlyList<RegexRange> a)
    {
        var o = new List<RegexRange>();
        int next = 0;
        foreach (var r in a)
        {
            if (r.First > next) o.Add(new(next, r.First - 1));
            next = Math.Max(next, r.Last + 1);
        }
        if (next <= MaxScalar) o.Add(new(next, MaxScalar));
        return RemoveSurrogates(o);
    }

    public static List<RegexRange> RemoveSurrogates(List<RegexRange> a)
    {
        bool any = false;
        foreach (var r in a) if (r.First <= 0xDFFF && r.Last >= 0xD800) { any = true; break; }
        if (!any) return a;
        var o = new List<RegexRange>(a.Count + 1);
        foreach (var r in a)
        {
            if (r.Last < 0xD800 || r.First > 0xDFFF) { o.Add(r); continue; }
            if (r.First < 0xD800) o.Add(new(r.First, 0xD7FF));
            if (r.Last > 0xDFFF) o.Add(new(0xE000, r.Last));
        }
        return o;
    }

    public static List<RegexRange> Intersect(IReadOnlyList<RegexRange> a, IReadOnlyList<RegexRange> b)
    {
        var o = new List<RegexRange>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            int lo = Math.Max(a[i].First, b[j].First), hi = Math.Min(a[i].Last, b[j].Last);
            if (lo <= hi) o.Add(new(lo, hi));
            if (a[i].Last < b[j].Last) i++; else j++;
        }
        return o;
    }

    public static bool Contains(ReadOnlySpan<RegexRange> r, int c)
    {
        int lo = 0, hi = r.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (c < r[mid].First) hi = mid - 1;
            else if (c > r[mid].Last) lo = mid + 1;
            else return true;
        }
        return false;
    }

    public static long Count(IReadOnlyList<RegexRange> r)
    {
        long n = 0;
        foreach (var x in r) n += x.Last - x.First + 1;
        return n;
    }

    public static List<RegexRange> Of(params int[] scalars)
    {
        var l = new List<RegexRange>(scalars.Length);
        foreach (int c in scalars) l.Add(new(c, c));
        return Normalize(l);
    }

    public static List<RegexRange> Ranges(params (int, int)[] rs)
    {
        var l = new List<RegexRange>(rs.Length);
        foreach (var (a, b) in rs) l.Add(new(a, b));
        return Normalize(l);
    }
}

/// <summary>Simple (one-to-one) Unicode case equivalence, the `i` flag's meaning (Onigmo's multi-character folds such
/// as `ß` = `ss` are not applied). Built lazily from the invariant casing tables.</summary>
internal static class CaseFold
{
    static class Holder
    {
        public static readonly Dictionary<int, int[]> Classes = Build(out Members);
        public static readonly int[] Members;
    }

    static Dictionary<int, int[]> Build(out int[] members)
    {
        var groups = new Dictionary<int, SortedSet<int>>();
        void Scan(int lo, int hi)
        {
            for (int c = lo; c <= hi; c++)
            {
                if (c is >= 0xD800 and <= 0xDFFF or 0x130 or 0x131) continue;   // dotted/dotless i: Turkic-only folds
                int up = Rune.ToUpperInvariant(new Rune(c)).Value;
                if (up is 0x130 or 0x131) continue;
                int key = Rune.ToLowerInvariant(new Rune(up)).Value;
                if (key == c && up == c) continue;
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new SortedSet<int>();
                g.Add(c); g.Add(key); g.Add(up);
            }
        }
        Scan(0, 0xFFFF);
        Scan(0x10400, 0x104FF); Scan(0x10570, 0x105BF); Scan(0x10C80, 0x10CFF); Scan(0x118A0, 0x118FF);
        Scan(0x16E40, 0x16E9F); Scan(0x1E900, 0x1E95F);
        var map = new Dictionary<int, int[]>();
        foreach (var g in groups.Values)
        {
            if (g.Count < 2) continue;
            var arr = g.ToArray();
            foreach (int c in arr) map[c] = arr;
        }
        members = map.Keys.OrderBy(x => x).ToArray();
        return map;
    }

    /// <summary>The case-equivalence class of <paramref name="c"/> (sorted, includes c), or null when c has no other case.</summary>
    public static int[]? Equivalents(int c)
    {
        if (c < 0x80)
        {
            // fast path: ASCII letters, plus the two non-ASCII members (U+017F long s, U+212A Kelvin sign)
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) return Holder.Classes.TryGetValue(c, out var a) ? a : null;
            return null;
        }
        return Holder.Classes.TryGetValue(c, out var r) ? r : null;
    }

    public static bool HasOtherCase(int c) => Equivalents(c) != null;

    /// <summary>True when every case variant of <paramref name="c"/> is ASCII (so ASCII case-insensitive search finds them all).</summary>
    public static bool AsciiOnlyVariants(int c)
    {
        if (c >= 0x80) return false;
        var e = Equivalents(c);
        if (e == null) return true;
        foreach (int x in e) if (x >= 0x80) return false;
        return true;
    }

    public static bool Equal(int a, int b)
    {
        if (a == b) return true;
        var e = Equivalents(a);
        return e != null && Array.IndexOf(e, b) >= 0;
    }

    /// <summary>Adds every case variant of every member.</summary>
    public static List<RegexRange> Close(List<RegexRange> set)
    {
        if (set.Count == 0) return set;
        var add = new List<RegexRange>(set);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(set);
        foreach (int c in Holder.Members)
            if (RangeSet.Contains(span, c))
                foreach (int e in Holder.Classes[c]) add.Add(new(e, e));
        return RangeSet.Normalize(add);
    }
}

/// <summary>Unicode general categories, POSIX bracket classes and a few scripts, resolved to scalar ranges on first use.</summary>
internal static class UnicodeTables
{
    static class CategoryHolder { public static readonly List<RegexRange>[] ByCategory = Build(); }

    static List<RegexRange>[] Build()
    {
        var cats = new List<RegexRange>[30];
        for (int i = 0; i < cats.Length; i++) cats[i] = new List<RegexRange>();
        int start = 0, prev = -1;
        for (int c = 0; c <= RangeSet.MaxScalar; c++)
        {
            if (c == 0xD800) { if (prev >= 0) cats[prev].Add(new(start, c - 1)); prev = -1; c = 0xDFFF; continue; }
            int cat = (int)Rune.GetUnicodeCategory(new Rune(c));
            if (cat != prev)
            {
                if (prev >= 0) cats[prev].Add(new(start, c - 1));
                prev = cat; start = c;
            }
        }
        cats[prev].Add(new(start, RangeSet.MaxScalar));
        return cats;
    }

    static List<RegexRange> Cats(params UnicodeCategory[] cs)
    {
        var l = new List<RegexRange>();
        foreach (var c in cs) l.AddRange(CategoryHolder.ByCategory[(int)c]);
        return RangeSet.Normalize(l);
    }

    static readonly UnicodeCategory[] Letter = [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter, UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter];
    static readonly UnicodeCategory[] Mark = [UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark];
    static readonly UnicodeCategory[] Number = [UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber, UnicodeCategory.OtherNumber];
    static readonly UnicodeCategory[] Punct = [UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation, UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation];
    static readonly UnicodeCategory[] Symbol = [UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol, UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol];
    static readonly UnicodeCategory[] Separator = [UnicodeCategory.SpaceSeparator, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator];
    static readonly UnicodeCategory[] Other = [UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned];

    /// <summary>Unicode White_Space.</summary>
    public static List<RegexRange> WhiteSpace() => RangeSet.Ranges((9, 13), (0x20, 0x20), (0x85, 0x85), (0xA0, 0xA0), (0x1680, 0x1680), (0x2000, 0x200A), (0x2028, 0x2029), (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000));

    /// <summary>`[[:word:]]`, `\p{Word}`: Letter | Mark | Number | Connector_Punctuation (Onigmo).</summary>
    public static List<RegexRange> Word() => RangeSet.Union(Cats(Letter), RangeSet.Union(Cats(Mark), RangeSet.Union(Cats(Number), Cats(UnicodeCategory.ConnectorPunctuation))));

    static List<RegexRange> Alpha() => RangeSet.Union(Cats(Letter), RangeSet.Union(Cats(Mark), Cats(UnicodeCategory.LetterNumber)));

    static readonly Dictionary<string, Func<List<RegexRange>>> Props = new()
    {
        ["l"] = () => Cats(Letter), ["letter"] = () => Cats(Letter),
        ["lu"] = () => Cats(UnicodeCategory.UppercaseLetter), ["uppercaseletter"] = () => Cats(UnicodeCategory.UppercaseLetter),
        ["ll"] = () => Cats(UnicodeCategory.LowercaseLetter), ["lowercaseletter"] = () => Cats(UnicodeCategory.LowercaseLetter),
        ["lt"] = () => Cats(UnicodeCategory.TitlecaseLetter), ["titlecaseletter"] = () => Cats(UnicodeCategory.TitlecaseLetter),
        ["lm"] = () => Cats(UnicodeCategory.ModifierLetter), ["modifierletter"] = () => Cats(UnicodeCategory.ModifierLetter),
        ["lo"] = () => Cats(UnicodeCategory.OtherLetter), ["otherletter"] = () => Cats(UnicodeCategory.OtherLetter),
        ["lc"] = () => Cats(UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter),
        ["casedletter"] = () => Cats(UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter),
        ["m"] = () => Cats(Mark), ["mark"] = () => Cats(Mark), ["combiningmark"] = () => Cats(Mark),
        ["mn"] = () => Cats(UnicodeCategory.NonSpacingMark), ["nonspacingmark"] = () => Cats(UnicodeCategory.NonSpacingMark),
        ["mc"] = () => Cats(UnicodeCategory.SpacingCombiningMark), ["spacingmark"] = () => Cats(UnicodeCategory.SpacingCombiningMark),
        ["me"] = () => Cats(UnicodeCategory.EnclosingMark), ["enclosingmark"] = () => Cats(UnicodeCategory.EnclosingMark),
        ["n"] = () => Cats(Number), ["number"] = () => Cats(Number),
        ["nd"] = () => Cats(UnicodeCategory.DecimalDigitNumber), ["decimalnumber"] = () => Cats(UnicodeCategory.DecimalDigitNumber),
        ["nl"] = () => Cats(UnicodeCategory.LetterNumber), ["letternumber"] = () => Cats(UnicodeCategory.LetterNumber),
        ["no"] = () => Cats(UnicodeCategory.OtherNumber), ["othernumber"] = () => Cats(UnicodeCategory.OtherNumber),
        ["p"] = () => Cats(Punct), ["punctuation"] = () => Cats(Punct),
        ["pc"] = () => Cats(UnicodeCategory.ConnectorPunctuation), ["connectorpunctuation"] = () => Cats(UnicodeCategory.ConnectorPunctuation),
        ["pd"] = () => Cats(UnicodeCategory.DashPunctuation), ["dashpunctuation"] = () => Cats(UnicodeCategory.DashPunctuation),
        ["ps"] = () => Cats(UnicodeCategory.OpenPunctuation), ["openpunctuation"] = () => Cats(UnicodeCategory.OpenPunctuation),
        ["pe"] = () => Cats(UnicodeCategory.ClosePunctuation), ["closepunctuation"] = () => Cats(UnicodeCategory.ClosePunctuation),
        ["pi"] = () => Cats(UnicodeCategory.InitialQuotePunctuation), ["initialpunctuation"] = () => Cats(UnicodeCategory.InitialQuotePunctuation),
        ["pf"] = () => Cats(UnicodeCategory.FinalQuotePunctuation), ["finalpunctuation"] = () => Cats(UnicodeCategory.FinalQuotePunctuation),
        ["po"] = () => Cats(UnicodeCategory.OtherPunctuation), ["otherpunctuation"] = () => Cats(UnicodeCategory.OtherPunctuation),
        ["s"] = () => Cats(Symbol), ["symbol"] = () => Cats(Symbol),
        ["sm"] = () => Cats(UnicodeCategory.MathSymbol), ["mathsymbol"] = () => Cats(UnicodeCategory.MathSymbol),
        ["sc"] = () => Cats(UnicodeCategory.CurrencySymbol), ["currencysymbol"] = () => Cats(UnicodeCategory.CurrencySymbol),
        ["sk"] = () => Cats(UnicodeCategory.ModifierSymbol), ["modifiersymbol"] = () => Cats(UnicodeCategory.ModifierSymbol),
        ["so"] = () => Cats(UnicodeCategory.OtherSymbol), ["othersymbol"] = () => Cats(UnicodeCategory.OtherSymbol),
        ["z"] = () => Cats(Separator), ["separator"] = () => Cats(Separator),
        ["zs"] = () => Cats(UnicodeCategory.SpaceSeparator), ["spaceseparator"] = () => Cats(UnicodeCategory.SpaceSeparator),
        ["zl"] = () => Cats(UnicodeCategory.LineSeparator), ["lineseparator"] = () => Cats(UnicodeCategory.LineSeparator),
        ["zp"] = () => Cats(UnicodeCategory.ParagraphSeparator), ["paragraphseparator"] = () => Cats(UnicodeCategory.ParagraphSeparator),
        ["c"] = () => Cats(Other), ["other"] = () => Cats(Other),
        ["cc"] = () => Cats(UnicodeCategory.Control), ["control"] = () => Cats(UnicodeCategory.Control), ["cntrl"] = () => Cats(UnicodeCategory.Control),
        ["cf"] = () => Cats(UnicodeCategory.Format), ["format"] = () => Cats(UnicodeCategory.Format),
        ["co"] = () => Cats(UnicodeCategory.PrivateUse), ["privateuse"] = () => Cats(UnicodeCategory.PrivateUse),
        ["cn"] = () => Cats(UnicodeCategory.OtherNotAssigned), ["unassigned"] = () => Cats(UnicodeCategory.OtherNotAssigned),
        ["cs"] = () => new List<RegexRange>(), ["surrogate"] = () => new List<RegexRange>(),
        // POSIX bracket names (Unicode meaning, Onigmo)
        ["alpha"] = Alpha, ["alphabetic"] = Alpha,
        ["digit"] = () => Cats(UnicodeCategory.DecimalDigitNumber),
        ["alnum"] = () => RangeSet.Union(Alpha(), Cats(UnicodeCategory.DecimalDigitNumber)),
        ["upper"] = () => Cats(UnicodeCategory.UppercaseLetter), ["uppercase"] = () => Cats(UnicodeCategory.UppercaseLetter),
        ["lower"] = () => Cats(UnicodeCategory.LowercaseLetter), ["lowercase"] = () => Cats(UnicodeCategory.LowercaseLetter),
        ["space"] = WhiteSpace, ["whitespace"] = WhiteSpace,
        ["blank"] = () => RangeSet.Union(Cats(UnicodeCategory.SpaceSeparator), RangeSet.Of('\t')),
        ["graph"] = () => RangeSet.Complement(RangeSet.Union(WhiteSpace(), Cats(UnicodeCategory.Control, UnicodeCategory.OtherNotAssigned))),
        ["print"] = () => RangeSet.Union(RangeSet.Complement(RangeSet.Union(WhiteSpace(), Cats(UnicodeCategory.Control, UnicodeCategory.OtherNotAssigned))), Cats(UnicodeCategory.SpaceSeparator)),
        ["punct"] = () => RangeSet.Union(Cats(Punct), RangeSet.Of('$', '+', '<', '=', '>', '^', '`', '|', '~')),
        ["xdigit"] = () => RangeSet.Ranges(('0', '9'), ('A', 'F'), ('a', 'f')),
        ["word"] = Word,
        ["ascii"] = () => RangeSet.Ranges((0, 0x7F)),
        ["any"] = () => RangeSet.Complement(new List<RegexRange>()),
        // scripts: block-based approximations intersected with assigned letters/marks (no Script data in the BCL)
        ["latin"] = () => Script((0x41, 0x5A), (0x61, 0x7A), (0xAA, 0xAA), (0xBA, 0xBA), (0xC0, 0xD6), (0xD8, 0xF6), (0xF8, 0x24F), (0x1E00, 0x1EFF), (0x2C60, 0x2C7F), (0xA720, 0xA7FF), (0xFF21, 0xFF3A), (0xFF41, 0xFF5A)),
        ["greek"] = () => Script((0x370, 0x373), (0x375, 0x377), (0x37A, 0x37D), (0x37F, 0x37F), (0x384, 0x384), (0x386, 0x386), (0x388, 0x3E1), (0x3F0, 0x3FF), (0x1D26, 0x1D2A), (0x1F00, 0x1FFE), (0x2126, 0x2126)),
        ["cyrillic"] = () => Script((0x400, 0x52F), (0x1C80, 0x1C8F), (0x2DE0, 0x2DFF), (0xA640, 0xA69F)),
        ["armenian"] = () => Script((0x531, 0x58A), (0xFB13, 0xFB17)),
        ["hebrew"] = () => Script((0x591, 0x5F4), (0xFB1D, 0xFB4F)),
        ["arabic"] = () => Script((0x600, 0x604), (0x606, 0x60B), (0x60D, 0x61A), (0x61C, 0x61E), (0x620, 0x63F), (0x641, 0x64A), (0x656, 0x66F), (0x671, 0x6DC), (0x6DE, 0x6FF), (0x750, 0x77F), (0x8A0, 0x8FF), (0xFB50, 0xFDFF), (0xFE70, 0xFEFC)),
        ["thai"] = () => Script((0xE01, 0xE3A), (0xE40, 0xE5B)),
        ["hangul"] = () => Script((0x1100, 0x11FF), (0x3131, 0x318E), (0xA960, 0xA97F), (0xAC00, 0xD7A3), (0xD7B0, 0xD7FF), (0xFFA0, 0xFFDC)),
        ["hiragana"] = () => Script((0x3041, 0x3096), (0x309D, 0x309F)),
        ["katakana"] = () => Script((0x30A1, 0x30FA), (0x30FD, 0x30FF), (0x31F0, 0x31FF), (0x32D0, 0x32FE), (0x3300, 0x3357), (0xFF66, 0xFF6F), (0xFF71, 0xFF9D)),
        ["han"] = () => Script((0x2E80, 0x2E99), (0x2E9B, 0x2EF3), (0x2F00, 0x2FD5), (0x3005, 0x3005), (0x3007, 0x3007), (0x3021, 0x3029), (0x3038, 0x303B), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xF900, 0xFA6D), (0xFA70, 0xFAD9), (0x20000, 0x323AF)),
        ["devanagari"] = () => Script((0x900, 0x950), (0x955, 0x963), (0x966, 0x97F), (0xA8E0, 0xA8FF)),
    };

    static List<RegexRange> Script(params (int, int)[] blocks)
    {
        var assigned = RangeSet.Complement(Cats(UnicodeCategory.OtherNotAssigned));
        return RangeSet.Intersect(RangeSet.Ranges(blocks), assigned);
    }

    static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) if (c is not (' ' or '_' or '-')) sb.Append(char.ToLowerInvariant(c));
        var k = sb.ToString();
        return k.StartsWith("is", StringComparison.Ordinal) && k.Length > 2 && Props.ContainsKey(k[2..]) && !Props.ContainsKey(k) ? k[2..] : k;
    }

    /// <summary>The scalars of a property (`L`, `Lu`, `Alpha`, `Greek`...), or null when unknown.</summary>
    public static List<RegexRange>? Property(string name) => Props.TryGetValue(Key(name), out var f) ? f() : null;
}
