using System.Globalization;
using System.Text;

namespace Alice.Core.Text;

public static partial class RegexSyntax
{
    /// <summary>
    /// The pattern in .NET `System.Text.RegularExpressions` syntax with Ruby semantics spelled out (use it with
    /// `RegexOptions.CultureInvariant` only): classes and case folding are explicit sets, `.` and classes consume
    /// whole surrogate pairs, `^ $ \b` are written as look-arounds, group N is named `gN`. For interop and as the
    /// differential test oracle; Alice itself never runs the BCL engine.
    /// </summary>
    public static string ToDotNet(RegexPattern p)
    {
        var sb = new StringBuilder(p.Source.Length * 2 + 16);
        Emit(sb, p.Root);
        return sb.ToString();
    }

    const string WordClass = "[0-9A-Za-z_]";

    static void Emit(StringBuilder sb, RegexNode n)
    {
        switch (n)
        {
            case RegexNode.Empty:
                sb.Append("(?:)");
                break;
            case RegexNode.Literal l:
                foreach (var r in l.Text.EnumerateRunes())
                {
                    var eq = l.IgnoreCase ? CaseFold.Equivalents(r.Value) : null;
                    if (eq != null) EmitSet(sb, RangeSet.Of(eq));
                    else EmitScalar(sb, r.Value);
                }
                break;
            case RegexNode.AnyChar a:
                EmitSet(sb, a.DotAll ? RangeSet.Complement([]) : RangeSet.Complement([new('\n', '\n')]));
                break;
            case RegexNode.CharClass c:
                EmitSet(sb, Resolve(c));
                break;
            case RegexNode.Concat c:
                sb.Append("(?:");
                foreach (var x in c.Items) Emit(sb, x);
                sb.Append(')');
                break;
            case RegexNode.Alternation a:
                sb.Append("(?:");
                for (int k = 0; k < a.Alternatives.Length; k++)
                {
                    if (k > 0) sb.Append('|');
                    Emit(sb, a.Alternatives[k]);
                }
                sb.Append(')');
                break;
            case RegexNode.Repeat r:
                {
                    if (r.Mode == RegexGreediness.Possessive) sb.Append("(?>");
                    sb.Append("(?:");
                    Emit(sb, r.Body);
                    sb.Append(')');
                    if (r.Min == 0 && r.Max == -1) sb.Append('*');
                    else if (r.Min == 1 && r.Max == -1) sb.Append('+');
                    else if (r.Min == 0 && r.Max == 1) sb.Append('?');
                    else if (r.Max == -1) sb.Append('{').Append(r.Min).Append(",}");
                    else if (r.Min == r.Max) sb.Append('{').Append(r.Min).Append('}');
                    else sb.Append('{').Append(r.Min).Append(',').Append(r.Max).Append('}');
                    if (r.Mode == RegexGreediness.Lazy) sb.Append('?');
                    if (r.Mode == RegexGreediness.Possessive) sb.Append(')');
                    break;
                }
            case RegexNode.Capture c:
                sb.Append("(?<g").Append(c.Index).Append('>');
                Emit(sb, c.Body);
                sb.Append(')');
                break;
            case RegexNode.FlagScope f:
                Emit(sb, f.Body);
                break;
            case RegexNode.Atomic a:
                sb.Append("(?>");
                Emit(sb, a.Body);
                sb.Append(')');
                break;
            case RegexNode.Lookaround l:
                sb.Append(l.Behind ? (l.Negated ? "(?<!" : "(?<=") : (l.Negated ? "(?!" : "(?="));
                Emit(sb, l.Body);
                sb.Append(')');
                break;
            case RegexNode.Backreference b:
                sb.Append("\\k<g").Append(b.Index).Append('>');
                break;
            case RegexNode.Assertion a:
                sb.Append(a.Kind switch
                {
                    RegexAssertionKind.LineStart => @"(?:\A|(?<=\n)(?!\z))",
                    RegexAssertionKind.LineEnd => @"(?=\n|\z)",
                    RegexAssertionKind.TextStart => @"\A",
                    RegexAssertionKind.TextEnd => @"\z",
                    RegexAssertionKind.TextEndOrFinalNewline => @"\Z",
                    RegexAssertionKind.WordBoundary => $"(?:(?<={WordClass})(?!{WordClass})|(?<!{WordClass})(?={WordClass}))",
                    RegexAssertionKind.NotWordBoundary => $"(?:(?<={WordClass})(?={WordClass})|(?<!{WordClass})(?!{WordClass}))",
                    RegexAssertionKind.SearchStart => @"\G",
                    _ => throw new ArgumentOutOfRangeException(nameof(n)),
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(n), n.GetType().Name);
        }
    }

    static void EmitScalar(StringBuilder sb, int c)
    {
        if (c > 0xFFFF)
        {
            string pair = char.ConvertFromUtf32(c);
            sb.Append("(?:\\u").Append(((int)pair[0]).ToString("X4", CultureInfo.InvariantCulture))
              .Append("\\u").Append(((int)pair[1]).ToString("X4", CultureInfo.InvariantCulture)).Append(')');
        }
        else sb.Append("\\u").Append(c.ToString("X4", CultureInfo.InvariantCulture));
    }

    static void Unit(StringBuilder sb, int u) => sb.Append("\\u").Append(u.ToString("X4", CultureInfo.InvariantCulture));

    // a set of scalars: BMP members as one bracket, astral members as surrogate-pair alternatives
    static void EmitSet(StringBuilder sb, List<RegexRange> set)
    {
        var bmp = new List<RegexRange>();
        var astral = new List<RegexRange>();
        foreach (var r in set)
        {
            if (r.First <= 0xFFFF) bmp.Add(new(r.First, Math.Min(r.Last, 0xFFFF)));
            if (r.Last > 0xFFFF) astral.Add(new(Math.Max(r.First, 0x10000), r.Last));
        }
        if (bmp.Count == 0 && astral.Count == 0) { sb.Append("(?!)"); return; }
        bool alt = astral.Count > 0;
        if (alt) sb.Append("(?:");
        if (bmp.Count > 0)
        {
            sb.Append('[');
            foreach (var r in bmp)
            {
                Unit(sb, r.First);
                if (r.Last > r.First) { sb.Append('-'); Unit(sb, r.Last); }
            }
            sb.Append(']');
        }
        bool firstAlt = bmp.Count == 0;
        foreach (var (hiLo, hiHi, loLo, loHi) in Utf16Pairs(astral))
        {
            if (!firstAlt) sb.Append('|');
            firstAlt = false;
            sb.Append('[');
            Unit(sb, hiLo);
            if (hiHi > hiLo) { sb.Append('-'); Unit(sb, hiHi); }
            sb.Append("][");
            Unit(sb, loLo);
            if (loHi > loLo) { sb.Append('-'); Unit(sb, loHi); }
            sb.Append(']');
        }
        if (alt) sb.Append(')');
    }

    /// <summary>Astral scalar ranges as (high-surrogate range, low-surrogate range) pairs.</summary>
    internal static IEnumerable<(int HiLo, int HiHi, int LoLo, int LoHi)> Utf16Pairs(List<RegexRange> astral)
    {
        foreach (var r in astral)
        {
            int a = r.First - 0x10000, b = r.Last - 0x10000;
            int ha = 0xD800 + (a >> 10), la = 0xDC00 + (a & 0x3FF);
            int hb = 0xD800 + (b >> 10), lb = 0xDC00 + (b & 0x3FF);
            if (ha == hb) { yield return (ha, ha, la, lb); continue; }
            if (la != 0xDC00) { yield return (ha, ha, la, 0xDFFF); ha++; }
            if (lb != 0xDFFF) { if (hb - 1 >= ha) yield return (ha, hb - 1, 0xDC00, 0xDFFF); yield return (hb, hb, 0xDC00, lb); }
            else yield return (ha, hb, 0xDC00, 0xDFFF);
        }
    }
}
