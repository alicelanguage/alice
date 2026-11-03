using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Alice.Core.Text;

/// <summary>A pattern the front-end rejects: bad syntax or a construct Alice does not support.</summary>
public sealed class RegexParseException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    public RegexParseException(string message, string pattern, int offset)
        : base($"{message} at offset {offset}: /{pattern}/") { Pattern = pattern; Offset = offset; }

    /// <summary>The pattern text.</summary>
    public string Pattern { get; }

    /// <summary>Offset of the error in <see cref="Pattern"/>.</summary>
    public int Offset { get; }
}

/// <summary>
/// The regex front-end (ADR 0036): Ruby/Onigmo syntax to <see cref="RegexNode"/>, plus the <see cref="RegexFeatures"/>
/// that select the engine. Ruby semantics: `\w \d \s \h \b` are ASCII, `[[:word:]]` and `\p{...}` are Unicode, `^`/`$`
/// are line anchors, `m` is dot-all, an inline `(?i)` applies to the rest of its group (alternatives included), and
/// unnamed groups do not capture once the pattern has a named group.
/// </summary>
public static partial class RegexSyntax
{
    /// <summary>Parses <paramref name="pattern"/> (no delimiters).</summary>
    public static RegexPattern Parse(string pattern, RegexFlags flags = RegexFlags.None)
    {
        var p = new Parser(pattern, flags);
        var root = p.ParseTop();
        return new RegexPattern(pattern, flags, root, p.Groups, p.Names.ToImmutableArray(), p.Features);
    }

    /// <summary>Flags after a literal's closing `/`: `i`, `m` (dot-all), `x`; `s` is accepted as a dot-all alias,
    /// the Ruby encoding flags `n e u` are accepted and ignored.</summary>
    public static RegexFlags ParseFlags(string? flags)
    {
        var f = RegexFlags.None;
        if (flags == null) return f;
        foreach (char c in flags)
            f |= c switch
            {
                'i' => RegexFlags.IgnoreCase,
                'm' or 's' => RegexFlags.DotAll,
                'x' => RegexFlags.Extended,
                'n' or 'e' or 'u' or 'o' => RegexFlags.None,
                _ => throw new ArgumentException($"unknown regex flag '{c}'"),
            };
        return f;
    }

    /// <summary>Flags as Ruby writes them after the literal (`imx` order).</summary>
    public static string FlagText(RegexFlags f) =>
        ((f & RegexFlags.DotAll) != 0 ? "m" : "") + ((f & RegexFlags.IgnoreCase) != 0 ? "i" : "") + ((f & RegexFlags.Extended) != 0 ? "x" : "");

    /// <summary>Escapes every regex metacharacter (Ruby `Regexp.escape`).</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\f': sb.Append("\\f"); break;
                case '\v': sb.Append("\\v"); break;
                case ' ': sb.Append("\\ "); break;
                case '.' or '*' or '?' or '+' or '^' or '$' or '|' or '(' or ')' or '[' or ']' or '{' or '}' or '\\' or '/' or '-' or '#':
                    sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>ASCII `\w`.</summary>
    internal static bool IsAsciiWord(int c) => c < 0x80 && (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    internal static readonly ImmutableArray<RegexRange> DigitRanges = [new('0', '9')];
    internal static readonly ImmutableArray<RegexRange> WordRanges = [new('0', '9'), new('A', 'Z'), new('_', '_'), new('a', 'z')];
    internal static readonly ImmutableArray<RegexRange> SpaceRanges = [new('\t', '\r'), new(' ', ' ')];
    internal static readonly ImmutableArray<RegexRange> HexRanges = [new('0', '9'), new('A', 'F'), new('a', 'f')];

    /// <summary>The scalars a class matches (properties resolved, negation applied).</summary>
    internal static List<RegexRange> Resolve(RegexNode.CharClass c)
    {
        var l = new List<RegexRange>(c.Ranges);
        foreach (var p in c.Properties)
            l.AddRange(UnicodeTables.Property(p) ?? throw new ArgumentException("unknown property " + p));
        l = RangeSet.RemoveSurrogates(RangeSet.Normalize(l));
        return c.Negated ? RangeSet.Complement(l) : l;
    }

    /// <summary>Length in scalars when every match has the same length, else null (look-behind bodies).</summary>
    internal static int? FixedLength(RegexNode n) => n switch
    {
        RegexNode.Empty or RegexNode.Assertion or RegexNode.Lookaround => 0,
        RegexNode.Literal l => l.Text.EnumerateRunes().Count(),
        RegexNode.AnyChar or RegexNode.CharClass => 1,
        RegexNode.Concat c => c.Items.Aggregate((int?)0, (a, x) => a is { } av && FixedLength(x) is { } xv ? av + xv : null),
        RegexNode.Alternation a => a.Alternatives.Select(FixedLength).Distinct().ToList() is [{ } one] ? one : null,
        RegexNode.Repeat r => r.Min == r.Max && FixedLength(r.Body) is { } b ? b * r.Min : null,
        RegexNode.Capture c => FixedLength(c.Body),
        RegexNode.FlagScope f => FixedLength(f.Body),
        RegexNode.Atomic a => FixedLength(a.Body),
        _ => null,
    };

    /// <summary>True when an unbounded repeat has a body that can match empty (`(a*)*`, `(|a)+`): Onigmo's empty-loop
    /// check then decides the match, which automata do not model, so such patterns run on the backtracker.</summary>
    internal static bool HasNullableLoop(RegexNode n) => n switch
    {
        RegexNode.Repeat r => ((r.Max == -1 || r.Max > r.Min) && Nullable(r.Body)) || HasNullableLoop(r.Body),
        RegexNode.Concat c => c.Items.Any(HasNullableLoop),
        RegexNode.Alternation a => a.Alternatives.Any(HasNullableLoop),
        RegexNode.Capture c => HasNullableLoop(c.Body),
        RegexNode.FlagScope f => HasNullableLoop(f.Body),
        RegexNode.Atomic a => HasNullableLoop(a.Body),
        RegexNode.Lookaround l => HasNullableLoop(l.Body),
        _ => false,
    };

    /// <summary>True when the node can match the empty string.</summary>
    internal static bool Nullable(RegexNode n) => n switch
    {
        RegexNode.Empty or RegexNode.Assertion or RegexNode.Lookaround => true,
        RegexNode.Literal or RegexNode.AnyChar or RegexNode.CharClass => false,
        RegexNode.Concat c => c.Items.All(Nullable),
        RegexNode.Alternation a => a.Alternatives.Any(Nullable),
        RegexNode.Repeat r => r.Min == 0 || Nullable(r.Body),
        RegexNode.Capture c => Nullable(c.Body),
        RegexNode.FlagScope f => Nullable(f.Body),
        RegexNode.Atomic a => Nullable(a.Body),
        RegexNode.Backreference => true,
        _ => true,
    };

    sealed class Parser
    {
        readonly string s;
        int i;
        RegexFlags flags;
        readonly bool named;
        public int Groups;
        public readonly List<string?> Names = [null];
        public RegexFeatures Features;
        readonly List<(int Offset, string? Name, int Index, int Relative)> refs = [];

        public Parser(string pattern, RegexFlags f)
        {
            s = pattern;
            flags = f;
            if ((f & RegexFlags.IgnoreCase) != 0) Features |= RegexFeatures.CaseInsensitive;
            named = HasNamedGroup(pattern);
        }

        RegexParseException Err(string msg, int at = -1) => new(msg, s, at < 0 ? i : at);

        static bool HasNamedGroup(string p)
        {
            bool inClass = false;
            for (int k = 0; k < p.Length; k++)
            {
                char c = p[k];
                if (c == '\\') { k++; continue; }
                if (inClass) { if (c == ']') inClass = false; continue; }
                if (c == '[') { inClass = true; if (k + 1 < p.Length && p[k + 1] == ']') k++; continue; }
                if (c == '(' && k + 3 < p.Length && p[k + 1] == '?')
                {
                    if (p[k + 2] == '\'' ) return true;
                    if (p[k + 2] == '<' && p[k + 3] is not ('=' or '!')) return true;
                }
            }
            return false;
        }

        bool Extended => (flags & RegexFlags.Extended) != 0;
        bool IgnoreCase => (flags & RegexFlags.IgnoreCase) != 0;

        public RegexNode ParseTop()
        {
            var n = ParseAlternation();
            if (i < s.Length) throw Err(s[i] == ')' ? "unmatched close parenthesis" : "unexpected character");
            foreach (var (off, name, idx, rel) in refs)
            {
                if (name != null && !Names.Contains(name)) throw Err($"undefined name <{name}> reference", off);
                if (name == null && (idx < 1 || idx > Groups)) throw Err("invalid backref number", off);
            }
            return Resolve(n);
        }

        // Backreference names resolve to the last group of that name (Onigmo tries the duplicates from the last).
        RegexNode Resolve(RegexNode n) => n switch
        {
            RegexNode.Backreference { Name: { } nm } b when b.Index == 0 => new RegexNode.Backreference(Names.LastIndexOf(nm), nm),
            RegexNode.Concat c => new RegexNode.Concat(c.Items.Select(Resolve).ToImmutableArray()),
            RegexNode.Alternation a => new RegexNode.Alternation(a.Alternatives.Select(Resolve).ToImmutableArray()),
            RegexNode.Repeat r => r with { Body = Resolve(r.Body) },
            RegexNode.Capture c => c with { Body = Resolve(c.Body) },
            RegexNode.FlagScope f => f with { Body = Resolve(f.Body) },
            RegexNode.Atomic a => new RegexNode.Atomic(Resolve(a.Body)),
            RegexNode.Lookaround l => l with { Body = Resolve(l.Body) },
            _ => n,
        };

        RegexNode ParseAlternation()
        {
            var first = ParseConcat();
            if (i >= s.Length || s[i] != '|') return first;
            var alts = ImmutableArray.CreateBuilder<RegexNode>();
            alts.Add(first);
            while (i < s.Length && s[i] == '|')
            {
                i++;
                alts.Add(ParseConcat());
            }
            Features |= RegexFeatures.Alternation;
            return new RegexNode.Alternation(alts.ToImmutable());
        }

        void SkipExtended()
        {
            if (!Extended) return;
            while (i < s.Length)
            {
                char c = s[i];
                if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v') { i++; continue; }
                if (c == '#') { while (i < s.Length && s[i] != '\n') i++; continue; }
                break;
            }
        }

        RegexNode ParseConcat()
        {
            var items = new List<RegexNode>();
            while (true)
            {
                SkipExtended();
                if (i >= s.Length || s[i] is '|' or ')') break;
                if (s[i] == '(' && i + 1 < s.Length && s[i + 1] == '?' && TryOptionOnly(out var on, out var off))
                {
                    // `(?imx-imx)`: the rest of the enclosing group, alternatives included, is the body
                    var saved = flags;
                    flags = (flags | on) & ~off;
                    if ((on & RegexFlags.IgnoreCase) != 0) Features |= RegexFeatures.CaseInsensitive;
                    var body = ParseAlternation();
                    flags = saved;
                    items.Add(new RegexNode.FlagScope(on, off, body));
                    break;
                }
                int atomAt = i;
                var atom = ParseAtom();
                if (atom == null) continue;
                atom = ParseQuantifiers(atom, atomAt);
                items.Add(atom);
            }
            return MakeConcat(items);
        }

        static RegexNode MakeConcat(List<RegexNode> items)
        {
            // merge adjacent literals with the same case mode
            var merged = new List<RegexNode>(items.Count);
            foreach (var it in items)
            {
                if (it is RegexNode.Empty) continue;
                if (it is RegexNode.Concat inner) { foreach (var x in inner.Items) Push(merged, x); continue; }
                Push(merged, it);
            }
            return merged.Count switch
            {
                0 => new RegexNode.Empty(),
                1 => merged[0],
                _ => new RegexNode.Concat(merged.ToImmutableArray()),
            };

            static void Push(List<RegexNode> m, RegexNode x)
            {
                if (x is RegexNode.Literal l && m.Count > 0 && m[^1] is RegexNode.Literal p && p.IgnoreCase == l.IgnoreCase)
                    m[^1] = new RegexNode.Literal(p.Text + l.Text, l.IgnoreCase);
                else m.Add(x);
            }
        }

        bool TryOptionOnly(out RegexFlags on, out RegexFlags off)
        {
            on = off = RegexFlags.None;
            int k = i + 2;
            bool neg = false;
            while (k < s.Length)
            {
                char c = s[k];
                if (c == ')') { i = k + 1; return true; }
                if (c == ':') return false;
                if (c == '-') { neg = true; k++; continue; }
                var f = FlagOf(c, k);
                if (f == null) return false;
                if (neg) off |= f.Value; else on |= f.Value;
                k++;
            }
            return false;
        }

        RegexFlags? FlagOf(char c, int at) => c switch
        {
            'i' => RegexFlags.IgnoreCase,
            'm' => RegexFlags.DotAll,
            'x' => RegexFlags.Extended,
            'a' or 'd' => RegexFlags.None,   // ASCII shorthands: already Ruby's default
            'u' => throw Err("(?u) Unicode shorthands are not supported (use \\p{Word}, [[:digit:]], [[:space:]])", at),
            _ => null,
        };

        RegexNode ParseQuantifiers(RegexNode atom, int atomAt)
        {
            while (true)
            {
                SkipExtended();
                if (i >= s.Length) return atom;
                char c = s[i];
                int min, max;
                bool brace = false;
                if (c == '*') { min = 0; max = -1; i++; }
                else if (c == '+') { min = 1; max = -1; i++; }
                else if (c == '?') { min = 0; max = 1; i++; }
                else if (c == '{' && TryBrace(out min, out max)) brace = true;
                else return atom;
                if (atom is RegexNode.Assertion or RegexNode.Lookaround)
                    throw Err("target of repeat operator is invalid", atomAt);
                var mode = RegexGreediness.Greedy;
                if (i < s.Length && s[i] == '?') { mode = RegexGreediness.Lazy; i++; Features |= RegexFeatures.LazyQuantifiers; }
                else if (!brace && i < s.Length && s[i] == '+') { mode = RegexGreediness.Possessive; i++; Features |= RegexFeatures.PossessiveQuantifiers; }
                if (brace) Features |= (max > 32 || min > 32) ? RegexFeatures.LargeCountedRepeat : RegexFeatures.CountedRepeat;
                atom = new RegexNode.Repeat(atom, min, max, mode);
            }
        }

        bool TryBrace(out int min, out int max)
        {
            min = max = 0;
            int k = i + 1;
            int a = ReadInt(ref k, out bool hasA);
            bool comma = false;
            int b = -1;
            bool hasB = false;
            if (k < s.Length && s[k] == ',') { comma = true; k++; b = ReadInt(ref k, out hasB); }
            if (k >= s.Length || s[k] != '}' || (!hasA && !hasB)) return false;
            min = hasA ? a : 0;
            max = comma ? (hasB ? b : -1) : min;
            if (min > 100000 || max > 100000) throw Err("too big number for repeat range");
            if (max >= 0 && max < min) throw Err("upper bound must be greater than lower bound");
            i = k + 1;
            return true;
        }

        int ReadInt(ref int k, out bool any)
        {
            int v = 0;
            any = false;
            while (k < s.Length && s[k] is >= '0' and <= '9')
            {
                v = Math.Min(v * 10 + (s[k] - '0'), 1_000_000);
                k++;
                any = true;
            }
            return v;
        }

        RegexNode Lit(int cp)
        {
            if (cp >= 0x80) Features |= RegexFeatures.NonAscii;
            return new RegexNode.Literal(char.ConvertFromUtf32(cp), IgnoreCase);
        }

        RegexNode? ParseAtom()
        {
            char c = s[i];
            switch (c)
            {
                case '(':
                    return ParseGroup();
                case '[':
                    i++;
                    return ParseClass();
                case '.':
                    i++;
                    return new RegexNode.AnyChar((flags & RegexFlags.DotAll) != 0);
                case '^':
                    i++;
                    Features |= RegexFeatures.LineAnchors;
                    return new RegexNode.Assertion(RegexAssertionKind.LineStart);
                case '$':
                    i++;
                    Features |= RegexFeatures.LineAnchors;
                    return new RegexNode.Assertion(RegexAssertionKind.LineEnd);
                case '\\':
                    return ParseEscape();
                case '*' or '+' or '?':
                    throw Err("target of repeat operator is not specified");
                default:
                    {
                        int cp = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(c, s[i + 1]) : c;
                        if (cp is >= 0xD800 and <= 0xDFFF) throw Err("invalid (lone surrogate) character");
                        i += cp > 0xFFFF ? 2 : 1;
                        return Lit(cp);
                    }
            }
        }

        RegexNode? ParseGroup()
        {
            int open = i;
            i++;   // '('
            if (i < s.Length && s[i] == '?')
            {
                i++;
                if (i >= s.Length) throw Err("end pattern in group");
                char k = s[i];
                switch (k)
                {
                    case '#':
                        while (i < s.Length && s[i] != ')') i++;
                        if (i >= s.Length) throw Err("end pattern in group");
                        i++;
                        return null;
                    case ':':
                        i++;
                        return Close(ParseAlternation(), open);
                    case '>':
                        i++;
                        Features |= RegexFeatures.AtomicGroups;
                        return new RegexNode.Atomic(Close(ParseAlternation(), open));
                    case '=' or '!':
                        i++;
                        Features |= RegexFeatures.Lookahead;
                        return new RegexNode.Lookaround(Close(ParseAlternation(), open), false, k == '!');
                    case '<' when i + 1 < s.Length && s[i + 1] is '=' or '!':
                        {
                            bool neg = s[i + 1] == '!';
                            i += 2;
                            Features |= RegexFeatures.Lookbehind;
                            var body = Close(ParseAlternation(), open);
                            var alts = body is RegexNode.Alternation a ? a.Alternatives : [body];
                            foreach (var alt in alts)
                                if (FixedLength(alt) == null) throw Err("invalid pattern in look-behind", open);
                            return new RegexNode.Lookaround(body, true, neg);
                        }
                    case '<' or '\'':
                        {
                            char end = k == '<' ? '>' : '\'';
                            int st = ++i;
                            while (i < s.Length && s[i] != end) i++;
                            if (i >= s.Length) throw Err("invalid group name");
                            string name = s[st..i];
                            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                                throw Err($"invalid group name <{name}>", st);
                            i++;
                            int idx = ++Groups;
                            Names.Add(name);
                            Features |= RegexFeatures.Captures | RegexFeatures.NamedCaptures;
                            return new RegexNode.Capture(idx, name, Close(ParseAlternation(), open));
                        }
                    case '~':
                        throw Err("absent operator (?~...) is not supported");
                    case '(':
                        throw Err("conditional groups (?(cond)...) are not supported");
                    default:
                        {
                            // (?imx-imx:...)
                            var on = RegexFlags.None;
                            var off = RegexFlags.None;
                            bool neg = false;
                            while (i < s.Length && s[i] != ':')
                            {
                                if (s[i] == '-') { neg = true; i++; continue; }
                                var f = FlagOf(s[i], i) ?? throw Err("undefined group option");
                                if (neg) off |= f; else on |= f;
                                i++;
                            }
                            if (i >= s.Length) throw Err("end pattern in group");
                            i++;
                            var saved = flags;
                            flags = (flags | on) & ~off;
                            if ((on & RegexFlags.IgnoreCase) != 0) Features |= RegexFeatures.CaseInsensitive;
                            var body = Close(ParseAlternation(), open);
                            flags = saved;
                            return new RegexNode.FlagScope(on, off, body);
                        }
                }
            }
            if (named) return Close(ParseAlternation(), open);
            int index = ++Groups;
            Names.Add(null);
            Features |= RegexFeatures.Captures;
            return new RegexNode.Capture(index, null, Close(ParseAlternation(), open));
        }

        RegexNode Close(RegexNode body, int open)
        {
            if (i >= s.Length || s[i] != ')') throw Err("end pattern with unmatched parenthesis", open);
            i++;
            return body;
        }

        RegexNode ParseEscape()
        {
            int at = i;
            i++;   // '\'
            if (i >= s.Length) throw Err("too short escape sequence");
            char c = s[i];
            switch (c)
            {
                case 'A': i++; Features |= RegexFeatures.TextAnchors; return new RegexNode.Assertion(RegexAssertionKind.TextStart);
                case 'z': i++; Features |= RegexFeatures.TextAnchors; return new RegexNode.Assertion(RegexAssertionKind.TextEnd);
                case 'Z': i++; Features |= RegexFeatures.TextAnchors; return new RegexNode.Assertion(RegexAssertionKind.TextEndOrFinalNewline);
                case 'b': i++; Features |= RegexFeatures.WordBoundary; return new RegexNode.Assertion(RegexAssertionKind.WordBoundary);
                case 'B': i++; Features |= RegexFeatures.WordBoundary; return new RegexNode.Assertion(RegexAssertionKind.NotWordBoundary);
                case 'G': i++; Features |= RegexFeatures.SearchAnchor; return new RegexNode.Assertion(RegexAssertionKind.SearchStart);
                case 'R':
                    {
                        // (?>\x0D\x0A|[\x0A-\x0D\x{85}\x{2028}\x{2029}])
                        i++;
                        Features |= RegexFeatures.AtomicGroups | RegexFeatures.Alternation;
                        var nl = new RegexNode.CharClass([new(0x0A, 0x0D), new(0x85, 0x85), new(0x2028, 0x2029)], [], false);
                        return new RegexNode.Atomic(new RegexNode.Alternation([new RegexNode.Literal("\r\n", false), nl]));
                    }
                case 'X': throw Err("\\X (grapheme cluster) is not supported", at);
                case 'K': throw Err("\\K (keep) is not supported", at);
                case 'g': throw Err("\\g<name> subexpression calls are not supported", at);
                case 'k':
                    {
                        i++;
                        if (i >= s.Length || s[i] is not ('<' or '\'')) throw Err("invalid backref", at);
                        char end = s[i] == '<' ? '>' : '\'';
                        int st = ++i;
                        while (i < s.Length && s[i] != end) i++;
                        if (i >= s.Length) throw Err("invalid backref", at);
                        string name = s[st..i];
                        i++;
                        Features |= RegexFeatures.Backreferences;
                        if (int.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int num))
                        {
                            if (named) throw Err("numbered backref/call is not allowed. (use name)", at);
                            int idx = num < 0 ? Groups + 1 + num : num;
                            refs.Add((at, null, idx, num));
                            return new RegexNode.Backreference(idx, null);
                        }
                        refs.Add((at, name, 0, 0));
                        return new RegexNode.Backreference(0, name);
                    }
                case >= '1' and <= '9':
                    {
                        int k = i, v = 0;
                        while (k < s.Length && s[k] is >= '0' and <= '9' && v < 1000) { v = v * 10 + (s[k] - '0'); k++; }
                        if (v <= 9 || v <= Groups)
                        {
                            i = k;
                            if (named) throw Err("numbered backref/call is not allowed. (use name)", at);
                            Features |= RegexFeatures.Backreferences;
                            refs.Add((at, null, v, 0));
                            return new RegexNode.Backreference(v, null);
                        }
                        // otherwise octal (\12 with fewer than 12 groups)
                        if (s[i] is >= '8') throw Err("invalid backref number", at);
                        return Lit(ReadOctal());
                    }
            }
            i = at;
            var set = ParseClassEscape(out int cp, inClass: false);
            if (set != null) return set;
            return Lit(cp);
        }

        int ReadOctal()
        {
            int v = 0, n = 0;
            while (i < s.Length && n < 3 && s[i] is >= '0' and <= '7') { v = v * 8 + (s[i] - '0'); i++; n++; }
            return v;
        }

        int ReadHex(int maxDigits, bool required)
        {
            int v = 0, n = 0;
            while (i < s.Length && n < maxDigits && Uri.IsHexDigit(s[i])) { v = v * 16 + Convert.ToInt32(s[i].ToString(), 16); i++; n++; }
            if (required && n == 0) throw Err("invalid code point value");
            return v;
        }

        int CheckScalar(int v, int at)
        {
            if (v > RangeSet.MaxScalar || v is >= 0xD800 and <= 0xDFFF) throw Err("invalid Unicode scalar value", at);
            return v;
        }

        /// <summary>An escape valid in and out of brackets: a set (returned) or a single scalar (<paramref name="cp"/>).</summary>
        RegexNode.CharClass? ParseClassEscape(out int cp, bool inClass)
        {
            int at = i;
            i++;   // '\'
            if (i >= s.Length) throw Err("too short escape sequence");
            char c = s[i++];
            cp = -1;
            switch (c)
            {
                case 't': cp = '\t'; return null;
                case 'n': cp = '\n'; return null;
                case 'r': cp = '\r'; return null;
                case 'f': cp = '\f'; return null;
                case 'v': cp = '\v'; return null;
                case 'a': cp = 7; return null;
                case 'e': cp = 0x1B; return null;
                case 'b' when inClass: cp = 8; return null;
                case '0': cp = ReadOctal(); return null;
                case >= '1' and <= '7' when inClass: i--; cp = ReadOctal(); return null;
                case 'x':
                    if (i < s.Length && s[i] == '{')
                    {
                        i++;
                        cp = ReadHex(8, true);
                        if (i >= s.Length || s[i] != '}') throw Err("invalid code point value", at);
                        i++;
                    }
                    else cp = ReadHex(2, true);
                    CheckScalar(cp, at);
                    return null;
                case 'u':
                    if (i < s.Length && s[i] == '{')
                    {
                        i++;
                        while (i < s.Length && s[i] == ' ') i++;
                        cp = CheckScalar(ReadHex(8, true), at);
                        while (i < s.Length && s[i] == ' ') i++;
                        if (i >= s.Length || s[i] != '}') throw Err("\\u{...} with several code points is not supported", at);
                        i++;
                    }
                    else
                    {
                        int st = i;
                        cp = ReadHex(4, true);
                        if (i - st != 4) throw Err("invalid Unicode escape", at);
                        CheckScalar(cp, at);
                    }
                    return null;
                case 'c':
                    if (i >= s.Length) throw Err("end pattern at control", at);
                    cp = s[i++] & 0x1F;
                    return null;
                case 'd': return Shorthand(DigitRanges, false);
                case 'D': return Shorthand(DigitRanges, true);
                case 'w': return Shorthand(WordRanges, false);
                case 'W': return Shorthand(WordRanges, true);
                case 's': return Shorthand(SpaceRanges, false);
                case 'S': return Shorthand(SpaceRanges, true);
                case 'h': return Shorthand(HexRanges, false);
                case 'H': return Shorthand(HexRanges, true);
                case 'p' or 'P':
                    {
                        if (i >= s.Length || s[i] != '{') throw Err("invalid character property name {", at);
                        int st = ++i;
                        while (i < s.Length && s[i] != '}') i++;
                        if (i >= s.Length) throw Err("invalid character property name", at);
                        string name = s[st..i];
                        i++;
                        bool neg = c == 'P';
                        if (name.StartsWith('^')) { neg = !neg; name = name[1..]; }
                        if (UnicodeTables.Property(name) == null) throw Err($"invalid character property name {{{name}}}", at);
                        Features |= RegexFeatures.UnicodeProperties | RegexFeatures.NonAscii;
                        return new RegexNode.CharClass([], [name], neg);
                    }
                default:
                    if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i])) { cp = char.ConvertToUtf32(c, s[i]); i++; }
                    else cp = c;
                    return null;
            }
        }

        RegexNode.CharClass Shorthand(ImmutableArray<RegexRange> r, bool neg)
        {
            if (neg) Features |= RegexFeatures.NonAscii;
            return new RegexNode.CharClass(r, [], neg);
        }

        // after '['
        RegexNode ParseClass()
        {
            var set = ParseClassBody();
            var arr = set.ToImmutableArray();
            foreach (var r in arr) if (r.Last >= 0x80) { Features |= RegexFeatures.NonAscii; break; }
            return new RegexNode.CharClass(arr, [], false);
        }

        // parses up to and including the closing ']' ; returns the resolved scalar set
        List<RegexRange> ParseClassBody()
        {
            int open = i - 1;
            bool neg = false;
            if (i < s.Length && s[i] == '^') { neg = true; i++; }
            var chars = new List<RegexRange>();
            var sets = new List<RegexRange>();
            bool first = true;
            while (true)
            {
                if (i >= s.Length) throw Err("premature end of char-class", open);
                char c = s[i];
                if (c == ']' && !first) { i++; break; }
                first = false;
                if (c == '&' && i + 1 < s.Length && s[i + 1] == '&')
                {
                    i += 2;
                    var left = Combine(chars, sets);
                    // the right operand runs to the closing bracket of this class
                    var right = ParseIntersectionRest(open);
                    var r = RangeSet.Intersect(left, right);
                    return neg ? RangeSet.Complement(r) : r;
                }
                if (c == '[')
                {
                    if (i + 1 < s.Length && s[i + 1] == ':' && TryPosix(out var posix)) { sets.AddRange(posix); continue; }
                    i++;
                    sets.AddRange(ParseClassBody());
                    continue;
                }
                int lo;
                if (c == '\\')
                {
                    var sh = ParseClassEscape(out lo, inClass: true);
                    if (sh != null) { sets.AddRange(SyntaxResolve(sh)); continue; }
                }
                else lo = ReadChar();
                // range?
                if (i + 1 < s.Length && s[i] == '-' && s[i + 1] != ']')
                {
                    int save = i;
                    i++;
                    int hi;
                    if (s[i] == '\\')
                    {
                        var sh = ParseClassEscape(out hi, inClass: true);
                        if (sh != null) throw Err("char-class value at end of range", save);
                    }
                    else if (s[i] == '[') { i = save; chars.Add(new(lo, lo)); continue; }
                    else hi = ReadChar();
                    if (hi < lo) throw Err("empty range in char class", save);
                    chars.Add(new(lo, hi));
                    continue;
                }
                chars.Add(new(lo, lo));
            }
            var all = Combine(chars, sets);
            return neg ? RangeSet.Complement(all) : all;
        }

        List<RegexRange> ParseIntersectionRest(int open)
        {
            // like ParseClassBody, but the caller already consumed '[' and an operand
            var chars = new List<RegexRange>();
            var sets = new List<RegexRange>();
            while (true)
            {
                if (i >= s.Length) throw Err("premature end of char-class", open);
                char c = s[i];
                if (c == ']') { i++; break; }
                if (c == '&' && i + 1 < s.Length && s[i + 1] == '&')
                {
                    i += 2;
                    var left = Combine(chars, sets);
                    return RangeSet.Intersect(left, ParseIntersectionRest(open));
                }
                if (c == '[')
                {
                    if (i + 1 < s.Length && s[i + 1] == ':' && TryPosix(out var posix)) { sets.AddRange(posix); continue; }
                    i++;
                    sets.AddRange(ParseClassBody());
                    continue;
                }
                int lo;
                if (c == '\\')
                {
                    var sh = ParseClassEscape(out lo, inClass: true);
                    if (sh != null) { sets.AddRange(SyntaxResolve(sh)); continue; }
                }
                else lo = ReadChar();
                if (i + 1 < s.Length && s[i] == '-' && s[i + 1] != ']')
                {
                    i++;
                    int hi;
                    if (s[i] == '\\') { if (ParseClassEscape(out hi, inClass: true) != null) throw Err("char-class value at end of range"); }
                    else hi = ReadChar();
                    if (hi < lo) throw Err("empty range in char class");
                    chars.Add(new(lo, hi));
                    continue;
                }
                chars.Add(new(lo, lo));
            }
            return Combine(chars, sets);
        }

        List<RegexRange> SyntaxResolve(RegexNode.CharClass c) => RegexSyntax.Resolve(c);

        List<RegexRange> Combine(List<RegexRange> chars, List<RegexRange> sets)
        {
            var ch = RangeSet.RemoveSurrogates(RangeSet.Normalize(new List<RegexRange>(chars)));
            if (IgnoreCase) ch = CaseFold.Close(ch);
            return RangeSet.Union(ch, sets);
        }

        int ReadChar()
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i += 2; return char.ConvertToUtf32(c, s[i - 1]); }
            if (char.IsSurrogate(c)) throw Err("invalid (lone surrogate) character");
            i++;
            return c;
        }

        bool TryPosix(out List<RegexRange> set)
        {
            set = [];
            int k = i + 2;
            bool neg = false;
            if (k < s.Length && s[k] == '^') { neg = true; k++; }
            int st = k;
            while (k < s.Length && char.IsAsciiLetterLower(s[k])) k++;
            if (k + 1 >= s.Length || s[k] != ':' || s[k + 1] != ']') return false;
            string name = s[st..k];
            var r = name switch
            {
                "alnum" or "alpha" or "ascii" or "blank" or "cntrl" or "digit" or "graph" or "lower" or "print" or "punct" or "space" or "upper" or "xdigit" or "word"
                    => UnicodeTables.Property(name),
                _ => null,
            } ?? throw Err($"invalid POSIX bracket type [:{name}:]");
            Features |= RegexFeatures.UnicodeProperties;
            i = k + 2;
            set = neg ? RangeSet.Complement(r) : r;
            return true;
        }
    }
}
