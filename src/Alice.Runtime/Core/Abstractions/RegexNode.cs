using System.Collections.Immutable;

namespace Alice.Core.Text;

// Frozen contract (ADR 0036): the regex front-end (task C1.1, RegexSyntax) produces these shapes and every engine
// (Tier R lazy DFA, Tier B BCL translation, the compile-time matcher emitter) consumes them. Pure data: no engine code.

/// <summary>Pattern flags with Ruby/Onigmo meaning. Ruby `m` is <see cref="DotAll"/> (`.` matches `\n`); `^`/`$`
/// are always line anchors in Ruby, so there is no separate multiline flag.</summary>
[Flags]
public enum RegexFlags : byte
{
    /// <summary>No flag.</summary>
    None = 0,
    /// <summary>`i`: Unicode simple case folding.</summary>
    IgnoreCase = 1,
    /// <summary>`m` (Ruby): `.` also matches `\n`.</summary>
    DotAll = 2,
    /// <summary>`x`: whitespace and `#` comments in the pattern are ignored.</summary>
    Extended = 4,
}

/// <summary>What a parsed pattern needs. Computed once by the front-end; selects the engine tier
/// (<see cref="RegexFeaturesExtensions.Tier"/>) and lets the compiler reject unsupported constructs early.</summary>
[Flags]
public enum RegexFeatures : uint
{
    /// <summary>Literal-only or simple classes.</summary>
    None = 0,
    /// <summary>Numbered capture groups.</summary>
    Captures = 1u << 0,
    /// <summary>`(?&lt;name&gt;...)`.</summary>
    NamedCaptures = 1u << 1,
    /// <summary>`a|b`.</summary>
    Alternation = 1u << 2,
    /// <summary>`{n}`, `{n,m}` with m &lt;= 32.</summary>
    CountedRepeat = 1u << 3,
    /// <summary>`{n,m}` with m &gt; 32 (DFA state blow-up): Tier B.</summary>
    LargeCountedRepeat = 1u << 4,
    /// <summary>`*?`, `+?`, `??`, `{n,m}?` (leftmost-first semantics; Tier R can do it).</summary>
    LazyQuantifiers = 1u << 5,
    /// <summary>`*+`, `++`, `?+`: Tier B.</summary>
    PossessiveQuantifiers = 1u << 6,
    /// <summary>`(?&gt;...)`: Tier B.</summary>
    AtomicGroups = 1u << 7,
    /// <summary>`(?=...)`, `(?!...)`: Tier B.</summary>
    Lookahead = 1u << 8,
    /// <summary>`(?&lt;=...)`, `(?&lt;!...)`: Tier B.</summary>
    Lookbehind = 1u << 9,
    /// <summary>`\1`, `\k&lt;name&gt;`: Tier B.</summary>
    Backreferences = 1u << 10,
    /// <summary>`^`, `$` (line anchors).</summary>
    LineAnchors = 1u << 11,
    /// <summary>`\A`, `\z`, `\Z`.</summary>
    TextAnchors = 1u << 12,
    /// <summary>`\b`, `\B`.</summary>
    WordBoundary = 1u << 13,
    /// <summary>`\G`: Tier B.</summary>
    SearchAnchor = 1u << 14,
    /// <summary>Non-ASCII literals or class ranges.</summary>
    NonAscii = 1u << 15,
    /// <summary>`\p{L}`, `\p{Greek}`, POSIX brackets beyond ASCII.</summary>
    UnicodeProperties = 1u << 16,
    /// <summary>`i` anywhere in the pattern (flag or inline group).</summary>
    CaseInsensitive = 1u << 17,

    /// <summary>Any of these forces Tier B (backtracking, BCL engine).</summary>
    BacktrackingOnly = LargeCountedRepeat | PossessiveQuantifiers | AtomicGroups | Lookahead | Lookbehind | Backreferences | SearchAnchor,
}

/// <summary>Engine tier (ADR 0036). R: own automata engine, UTF-8 and UTF-16, 0-alloc. B: BCL `Regex`
/// (`Compiled` at runtime by default, `[GeneratedRegex]` under `--native`).</summary>
public enum RegexTier : byte
{
    /// <summary>Regular: prefilter + lazy DFA (+ Pike VM for captures).</summary>
    Regular,
    /// <summary>Backtracking: translated to .NET syntax, `System.Text.RegularExpressions`.</summary>
    Backtracking,
}

/// <summary>Tier selection, the single rule shared by the runtime and the compiler.</summary>
public static class RegexFeaturesExtensions
{
    /// <summary>Tier B when any <see cref="RegexFeatures.BacktrackingOnly"/> feature is present, else Tier R.</summary>
    public static RegexTier Tier(this RegexFeatures features) =>
        (features & RegexFeatures.BacktrackingOnly) != 0 ? RegexTier.Backtracking : RegexTier.Regular;
}

/// <summary>The front-end's result: the AST plus everything engines need without re-walking it.</summary>
/// <param name="Source">The pattern text as written (without delimiters).</param>
/// <param name="Flags">Flags after the closing delimiter (`/re/imx`).</param>
/// <param name="Root">The pattern AST.</param>
/// <param name="CaptureCount">Number of capture groups, group 0 (the whole match) excluded.</param>
/// <param name="GroupNames">Name per group index 1..CaptureCount (index 0 unused); null for unnamed groups.</param>
/// <param name="Features">What the pattern uses.</param>
public sealed record RegexPattern(string Source, RegexFlags Flags, RegexNode Root, int CaptureCount, ImmutableArray<string?> GroupNames, RegexFeatures Features)
{
    /// <summary>The engine tier for this pattern.</summary>
    public RegexTier Tier => Features.Tier();
}

/// <summary>Zero-width assertions.</summary>
public enum RegexAssertionKind : byte
{
    /// <summary>`^` (Ruby: always start of line).</summary>
    LineStart,
    /// <summary>`$` (Ruby: always end of line).</summary>
    LineEnd,
    /// <summary>`\A`.</summary>
    TextStart,
    /// <summary>`\z`.</summary>
    TextEnd,
    /// <summary>`\Z`: end of text or before a final `\n`.</summary>
    TextEndOrFinalNewline,
    /// <summary>`\b` (Unicode word characters).</summary>
    WordBoundary,
    /// <summary>`\B`.</summary>
    NotWordBoundary,
    /// <summary>`\G`: where the search started.</summary>
    SearchStart,
}

/// <summary>Quantifier mode.</summary>
public enum RegexGreediness : byte
{
    /// <summary>`*`, `+`, `?`, `{n,m}`.</summary>
    Greedy,
    /// <summary>`*?` etc.</summary>
    Lazy,
    /// <summary>`*+` etc. (Tier B).</summary>
    Possessive,
}

/// <summary>An inclusive range of Unicode scalar values.</summary>
public readonly record struct RegexRange(int First, int Last);

/// <summary>
/// Regex AST. Case-insensitive matching is resolved by the front-end where possible (literals keep
/// <see cref="Literal.IgnoreCase"/>, classes are case-closed); `x` comments and whitespace are gone.
/// Records compare structurally, including their child lists.
/// </summary>
public abstract record RegexNode
{
    private protected RegexNode() { }

    /// <summary>Matches the empty string.</summary>
    public sealed record Empty : RegexNode;

    /// <summary>A literal string (one or more scalars).</summary>
    public sealed record Literal(string Text, bool IgnoreCase) : RegexNode;

    /// <summary>`.`: any scalar except `\n`, or any scalar when <paramref name="DotAll"/>.</summary>
    public sealed record AnyChar(bool DotAll) : RegexNode;

    /// <summary>A character class: the union of <paramref name="Ranges"/> (sorted, non-overlapping) and the
    /// Unicode <paramref name="Properties"/> (`L`, `Greek`, `Alpha`...), complemented when <paramref name="Negated"/>.
    /// Shorthands (`\d \w \s \h`) and POSIX brackets are expanded into these by the front-end.</summary>
    public sealed record CharClass(ImmutableArray<RegexRange> Ranges, ImmutableArray<string> Properties, bool Negated) : RegexNode
    {
        /// <inheritdoc/>
        public bool Equals(CharClass? other) => other is not null && Negated == other.Negated
            && Ranges.AsSpan().SequenceEqual(other.Ranges.AsSpan()) && Properties.AsSpan().SequenceEqual(other.Properties.AsSpan());
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Ranges.Length, Properties.Length, Negated);
    }

    /// <summary>Items matched one after another.</summary>
    public sealed record Concat(ImmutableArray<RegexNode> Items) : RegexNode
    {
        /// <inheritdoc/>
        public bool Equals(Concat? other) => other is not null && Items.AsSpan().SequenceEqual(other.Items.AsSpan());
        /// <inheritdoc/>
        public override int GetHashCode() => Items.Length;
    }

    /// <summary>`a|b|c`, leftmost-first (Ruby semantics).</summary>
    public sealed record Alternation(ImmutableArray<RegexNode> Alternatives) : RegexNode
    {
        /// <inheritdoc/>
        public bool Equals(Alternation? other) => other is not null && Alternatives.AsSpan().SequenceEqual(other.Alternatives.AsSpan());
        /// <inheritdoc/>
        public override int GetHashCode() => Alternatives.Length;
    }

    /// <summary>`Body{Min,Max}`; <paramref name="Max"/> = -1 means unbounded.</summary>
    public sealed record Repeat(RegexNode Body, int Min, int Max, RegexGreediness Mode) : RegexNode;

    /// <summary>A capture group; <paramref name="Index"/> &gt;= 1, <paramref name="Name"/> for `(?&lt;name&gt;...)`.</summary>
    public sealed record Capture(int Index, string? Name, RegexNode Body) : RegexNode;

    /// <summary>`(?i-m:...)` or `(?i)` to the end of the enclosing group.</summary>
    public sealed record FlagScope(RegexFlags Enable, RegexFlags Disable, RegexNode Body) : RegexNode;

    /// <summary>`(?&gt;...)`.</summary>
    public sealed record Atomic(RegexNode Body) : RegexNode;

    /// <summary>`(?=...)`, `(?!...)`, `(?&lt;=...)`, `(?&lt;!...)`.</summary>
    public sealed record Lookaround(RegexNode Body, bool Behind, bool Negated) : RegexNode;

    /// <summary>`\1` or `\k&lt;name&gt;` (resolved to an index; <paramref name="Name"/> kept for messages).</summary>
    public sealed record Backreference(int Index, string? Name) : RegexNode;

    /// <summary>A zero-width assertion.</summary>
    public sealed record Assertion(RegexAssertionKind Kind) : RegexNode;
}
