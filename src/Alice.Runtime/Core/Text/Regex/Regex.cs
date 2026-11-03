using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Alice.Core.Text;

/// <summary>
/// An Alice regular expression (Ruby/Onigmo syntax and semantics, ADR 0036), matched by Alice's own engines over
/// UTF-16 (`string`, <see cref="ReadOnlySpan{T}"/> of char) and UTF-8 (<see cref="ReadOnlySpan{T}"/> of byte, no
/// transcoding). Offsets are code units of the input (chars or bytes). Immutable and thread-safe; the per-encoding
/// engines are built on first use. Match-only calls (<see cref="IsMatch(string)"/>, <see cref="Find(ReadOnlySpan{char}, out Range)"/>,
/// <see cref="Count(string)"/>, <see cref="Matches(ReadOnlySpan{char})"/>, <see cref="TryMatch(ReadOnlySpan{char}, Span{Range})"/>) do not allocate.
/// </summary>
public sealed class Regex
{
    readonly bool forceBacktracking;
    Engine<char>? e16;
    Engine<byte>? e8;
    (string Text, Template T)? lastTemplate;

    /// <summary>Parses <paramref name="pattern"/> (Ruby syntax, no delimiters).</summary>
    public Regex(string pattern, RegexFlags flags = RegexFlags.None) : this(RegexSyntax.Parse(pattern, flags), false) { }

    /// <summary>Parses <paramref name="pattern"/> with Ruby flag letters (`"imx"`).</summary>
    public Regex(string pattern, string flags) : this(RegexSyntax.Parse(pattern, RegexSyntax.ParseFlags(flags)), false) { }

    Regex(RegexPattern p, bool backtrack)
    {
        Syntax = p;
        forceBacktracking = backtrack;
    }

    /// <summary>A regex for an already parsed pattern.</summary>
    public static Regex FromPattern(RegexPattern pattern) => new(pattern, false);

    /// <summary>Runs <paramref name="pattern"/> on the given tier (Backtracking accepts every pattern; Regular throws
    /// for patterns that need backtracking). For differential testing and benchmarks.</summary>
    public static Regex WithTier(string pattern, RegexFlags flags, RegexTier tier)
    {
        var p = RegexSyntax.Parse(pattern, flags);
        if (tier == RegexTier.Regular && p.Tier != RegexTier.Regular) throw new ArgumentException("pattern needs the backtracking tier: /" + pattern + "/");
        return new Regex(p, tier == RegexTier.Backtracking);
    }

    static class CacheHolder { public static readonly ConcurrentDictionary<(string, RegexFlags), Regex> Value = new(); }

    /// <summary>A shared instance for (pattern, flags), parsed once (internal tools, patterns built from names).
    /// Bounded: past 1024 distinct patterns the cache starts over.</summary>
    public static Regex Cached(string pattern, RegexFlags flags = RegexFlags.None)
    {
        var d = CacheHolder.Value;
        if (d.TryGetValue((pattern, flags), out var r)) return r;
        if (d.Count >= 1024) d.Clear();
        return d.GetOrAdd((pattern, flags), static k => new Regex(k.Item1, k.Item2));
    }

    /// <summary>The parsed pattern.</summary>
    public RegexPattern Syntax { get; }

    /// <summary>The pattern text (Ruby syntax).</summary>
    public string Pattern => Syntax.Source;

    /// <summary>The flags after the literal.</summary>
    public RegexFlags Options => Syntax.Flags;

    /// <summary>The engine tier: Regular (automata) or Backtracking.</summary>
    public RegexTier Tier => forceBacktracking ? RegexTier.Backtracking : Engine16.IsBacktracking ? RegexTier.Backtracking : RegexTier.Regular;

    /// <summary>Number of capture groups (group 0 excluded).</summary>
    public int GroupCount => Syntax.CaptureCount;

    /// <summary>Group names by index (index 0 and unnamed groups: null).</summary>
    public ImmutableArray<string?> GroupNames => Syntax.GroupNames;

    /// <summary>The distinct group names in order of definition (Ruby `names`).</summary>
    public string[] Names => Syntax.GroupNames.Where(n => n != null).Distinct().ToArray()!;

    /// <summary>Index of the last group named <paramref name="name"/>, or -1.</summary>
    public int GroupNumber(string name) => Syntax.GroupNames.LastIndexOf(name);

    /// <summary>Ruby `inspect`: `/pattern/flags`.</summary>
    public override string ToString() => "/" + Pattern + "/" + RegexSyntax.FlagText(Options);

    /// <summary>The pattern in .NET syntax with Ruby semantics (see <see cref="RegexSyntax.ToDotNet"/>).</summary>
    public string ToDotNetPattern() => RegexSyntax.ToDotNet(Syntax);

    /// <summary>Escapes metacharacters (Ruby `Regexp.escape`).</summary>
    public static string Escape(string text) => RegexSyntax.Escape(text);

    /// <summary>Marks a group that did not take part in the match (`^0..^0`).</summary>
    public static readonly Range Unset = new(Index.FromEnd(0), Index.FromEnd(0));

    internal Engine<char> Engine16 => e16 ?? Init(ref e16);
    internal Engine<byte> Engine8 => e8 ?? Init(ref e8);

    Engine<T> Init<T>(ref Engine<T>? slot) where T : unmanaged
    {
        var e = new Engine<T>(Syntax, forceBacktracking);
        return Interlocked.CompareExchange(ref slot, e, null) ?? e;
    }

    /// <summary>Prefilter in use for the encoding (diagnostics).</summary>
    public string Describe(bool utf8 = false) =>
        $"tier={(utf8 ? (Engine8.IsBacktracking ? "B" : "R") : (Engine16.IsBacktracking ? "B" : "R"))} prefilter={(utf8 ? Engine8.PrefilterKind : Engine16.PrefilterKind)}";

    // ------------------------------------------------------------------ core generic entry points

    internal bool FindAt<T>(ReadOnlySpan<T> h, int pos, out int s, out int e) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            var eng = Engine8;
            var c = eng.Rent();
            try { return eng.Find(MemoryMarshal.Cast<T, byte>(h), pos, out s, out e, c); }
            finally { eng.Return(c); }
        }
        else
        {
            var eng = Engine16;
            var c = eng.Rent();
            try { return eng.Find(MemoryMarshal.Cast<T, char>(h), pos, out s, out e, c); }
            finally { eng.Return(c); }
        }
    }

    internal bool MatchAny<T>(ReadOnlySpan<T> h, int pos) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            var eng = Engine8;
            var c = eng.Rent();
            try { return eng.IsMatch(MemoryMarshal.Cast<T, byte>(h), pos, c); }
            finally { eng.Return(c); }
        }
        else
        {
            var eng = Engine16;
            var c = eng.Rent();
            try { return eng.IsMatch(MemoryMarshal.Cast<T, char>(h), pos, c); }
            finally { eng.Return(c); }
        }
    }

    /// <summary>Capture slots (2 per group, -1 unset) of the match at or after pos.</summary>
    internal bool CapturesAt<T>(ReadOnlySpan<T> h, int pos, Span<int> slots) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            var eng = Engine8;
            var c = eng.Rent();
            try { return eng.Captures(MemoryMarshal.Cast<T, byte>(h), pos, slots, c); }
            finally { eng.Return(c); }
        }
        else
        {
            var eng = Engine16;
            var c = eng.Rent();
            try { return eng.Captures(MemoryMarshal.Cast<T, char>(h), pos, slots, c); }
            finally { eng.Return(c); }
        }
    }

    int SlotCount => 2 * (Syntax.CaptureCount + 1);

    // ------------------------------------------------------------------ IsMatch / Find / Count

    /// <summary>True when the pattern matches somewhere in <paramref name="input"/> (Ruby `match?`, sets no `$~`).</summary>
    public bool IsMatch(string input) => MatchAny<char>(input.AsSpan(), 0);
    /// <inheritdoc cref="IsMatch(string)"/>
    public bool IsMatch(ReadOnlySpan<char> input) => MatchAny(input, 0);
    /// <inheritdoc cref="IsMatch(string)"/>
    public bool IsMatch(ReadOnlySpan<byte> utf8) => MatchAny(utf8, 0);

    /// <summary>The first match at or after <paramref name="start"/>.</summary>
    public bool Find(ReadOnlySpan<char> input, out Range match) => Find(input, 0, out match);
    /// <inheritdoc cref="Find(ReadOnlySpan{char}, out Range)"/>
    public bool Find(ReadOnlySpan<char> input, int start, out Range match)
    {
        bool ok = FindAt(input, start, out int s, out int e);
        match = ok ? new Range(s, e) : default;
        return ok;
    }
    /// <inheritdoc cref="Find(ReadOnlySpan{char}, out Range)"/>
    public bool Find(ReadOnlySpan<byte> utf8, out Range match) => Find(utf8, 0, out match);
    /// <inheritdoc cref="Find(ReadOnlySpan{char}, out Range)"/>
    public bool Find(ReadOnlySpan<byte> utf8, int start, out Range match)
    {
        bool ok = FindAt(utf8, start, out int s, out int e);
        match = ok ? new Range(s, e) : default;
        return ok;
    }

    /// <summary>Number of non-overlapping matches.</summary>
    public int Count(string input) => Count(input.AsSpan());
    /// <inheritdoc cref="Count(string)"/>
    public int Count(ReadOnlySpan<char> input) => CountCore(input);
    /// <inheritdoc cref="Count(string)"/>
    public int Count(ReadOnlySpan<byte> utf8) => CountCore(utf8);

    int CountCore<T>(ReadOnlySpan<T> h) where T : unmanaged
    {
        if (typeof(T) == typeof(byte)) return CountWith(Engine8, MemoryMarshal.Cast<T, byte>(h));
        return CountWith(Engine16, MemoryMarshal.Cast<T, char>(h));
    }

    static int CountWith<T>(Engine<T> eng, ReadOnlySpan<T> h) where T : unmanaged
    {
        var c = eng.Rent();
        try
        {
            int n = 0, pos = 0;
            if (eng.EndOnly)
            {
                // non-empty matches: the next search starts at the previous end, no start needed
                while (eng.FindEnd(h, pos, out int e, c)) { n++; pos = e; }
                return n;
            }
            while (eng.Find(h, pos, out int s, out int e, c))
            {
                n++;
                pos = e > s ? e : Engine<T>.NextBoundary(h, e);
            }
            return n;
        }
        finally { eng.Return(c); }
    }

    // ------------------------------------------------------------------ Matches (struct enumerators)

    /// <summary>The non-overlapping matches as ranges, without allocation (`foreach (var r in re.Matches(s))`).</summary>
    public MatchEnumerator<char> Matches(ReadOnlySpan<char> input) => new(this, input);
    /// <inheritdoc cref="Matches(ReadOnlySpan{char})"/>
    public MatchEnumerator<byte> Matches(ReadOnlySpan<byte> utf8) => new(this, utf8);

    /// <summary>Enumerates matches (ranges in code units).</summary>
    public ref struct MatchEnumerator<T> where T : unmanaged
    {
        readonly Regex re;
        readonly ReadOnlySpan<T> h;
        int pos;
        Range cur;

        internal MatchEnumerator(Regex re, ReadOnlySpan<T> h) { this.re = re; this.h = h; pos = 0; cur = default; }

        /// <summary>This enumerator.</summary>
        public readonly MatchEnumerator<T> GetEnumerator() => this;

        /// <summary>The current match.</summary>
        public readonly Range Current => cur;

        /// <summary>Advances to the next match.</summary>
        public bool MoveNext()
        {
            if (pos > h.Length || !re.FindAt(h, pos, out int s, out int e)) { pos = h.Length + 1; return false; }
            cur = new Range(s, e);
            pos = e > s ? e : Engine<T>.NextBoundary(h, e);
            return true;
        }
    }

    // ------------------------------------------------------------------ captures

    /// <summary>The first match and its groups, without allocation: <paramref name="groups"/>[i] is group i
    /// (<see cref="Unset"/> when it did not take part); extra entries are left as Unset, missing ones are skipped.</summary>
    public bool TryMatch(ReadOnlySpan<char> input, Span<Range> groups) => TryMatch(input, 0, groups);
    /// <inheritdoc cref="TryMatch(ReadOnlySpan{char}, Span{Range})"/>
    public bool TryMatch(ReadOnlySpan<char> input, int start, Span<Range> groups) => TryMatchCore(input, start, groups);
    /// <inheritdoc cref="TryMatch(ReadOnlySpan{char}, Span{Range})"/>
    public bool TryMatch(ReadOnlySpan<byte> utf8, Span<Range> groups) => TryMatchCore(utf8, 0, groups);
    /// <inheritdoc cref="TryMatch(ReadOnlySpan{char}, Span{Range})"/>
    public bool TryMatch(ReadOnlySpan<byte> utf8, int start, Span<Range> groups) => TryMatchCore(utf8, start, groups);

    bool TryMatchCore<T>(ReadOnlySpan<T> h, int start, Span<Range> groups) where T : unmanaged
    {
        int want = Math.Min(groups.Length, Syntax.CaptureCount + 1);
        Span<int> slots = want <= 16 ? stackalloc int[2 * Math.Max(1, want)] : new int[2 * want];
        bool ok = CapturesAt(h, start, slots);
        for (int i = 0; i < groups.Length; i++)
            groups[i] = ok && i < want && slots[2 * i] >= 0 && slots[2 * i + 1] >= 0 ? new Range(slots[2 * i], slots[2 * i + 1]) : Unset;
        return ok;
    }

    /// <summary>The first match at or after <paramref name="start"/> as Ruby MatchData, or null.</summary>
    public MatchData? Match(string input, int start = 0)
    {
        var slots = new int[SlotCount];
        return CapturesAt<char>(input.AsSpan(), start, slots) ? new MatchData(this, input, slots) : null;
    }

    /// <inheritdoc cref="Match(string, int)"/>
    public MatchData? Match(ReadOnlySpan<char> input, int start = 0)
    {
        var slots = new int[SlotCount];
        return CapturesAt(input, start, slots) ? new MatchData(this, input.ToString(), slots) : null;
    }

    /// <summary>The first match in UTF-8 input; the MatchData keeps a copy of the input and byte offsets.</summary>
    public MatchData? Match(ReadOnlySpan<byte> utf8, int start = 0)
    {
        var slots = new int[SlotCount];
        return CapturesAt(utf8, start, slots) ? new MatchData(this, utf8.ToArray(), slots) : null;
    }

    /// <summary>Every match as MatchData (Ruby `scan` with groups, `to_enum(:scan)`).</summary>
    public List<MatchData> MatchAll(string input)
    {
        var r = new List<MatchData>();
        int pos = 0;
        var h = input.AsSpan();
        while (pos <= h.Length)
        {
            var slots = new int[SlotCount];
            if (!CapturesAt(h, pos, slots)) break;
            r.Add(new MatchData(this, input, slots));
            pos = slots[1] > slots[0] ? slots[1] : Engine<char>.NextBoundary(h, slots[1]);
        }
        return r;
    }

    // ------------------------------------------------------------------ Split (Ruby String#split(regexp))

    /// <summary>Ruby `split(re, limit)`: captures are included; limit 0 drops trailing empty fields, a positive
    /// limit caps the number of fields, a negative one keeps everything.</summary>
    public string[] Split(string input, int limit = 0)
    {
        var result = new List<string>();
        var h = input.AsSpan();
        int len = input.Length;
        if (limit == 1) return len == 0 ? [] : [input];
        int start = 0, beg = 0, fields = 0;
        bool lastNull = false;
        var slots = new int[SlotCount];
        while (start <= len && CapturesAt(h, start, slots))
        {
            int ms = slots[0], me = slots[1];
            if (start == ms && ms == me)
            {
                if (len == 0) { result.Add(""); break; }
                if (lastNull)
                {
                    result.Add(input[beg..start]);
                    beg = start;
                }
                else
                {
                    start = start >= len ? start + 1 : Engine<char>.NextBoundary(h, start);
                    lastNull = true;
                    continue;
                }
            }
            else
            {
                result.Add(input[beg..ms]);
                beg = start = me;
            }
            lastNull = false;
            for (int g = 1; g <= Syntax.CaptureCount; g++)
                if (slots[2 * g] >= 0 && slots[2 * g + 1] >= 0) result.Add(input[slots[2 * g]..slots[2 * g + 1]]);
            if (limit > 0 && limit <= ++fields + 1) break;
        }
        if (len > 0 && (limit != 0 || len > beg || limit < 0)) result.Add(input[beg..]);
        if (limit == 0) while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return result.ToArray();
    }

    // ------------------------------------------------------------------ Replace

    Template TemplateFor(string replacement)
    {
        if (lastTemplate is { } lt && ReferenceEquals(lt.Text, replacement)) return lt.T;
        var t = Template.Parse(replacement, this);
        lastTemplate = (replacement, t);
        return t;
    }

    /// <summary>Ruby `gsub`/`sub` (count 1) with a replacement template: `\0` `\&amp;` whole match, `\1`..`\9`,
    /// `\k&lt;name&gt;`, `` \` `` pre-match, `\'` post-match, `\\` a backslash.</summary>
    public string Replace(string input, string replacement, int count = -1)
    {
        var t = TemplateFor(replacement);
        var eng = Engine16;
        var c = eng.Rent();
        try
        {
            var h = input.AsSpan();
            Span<int> slots = t.MaxGroup > 0 ? new int[SlotCount] : stackalloc int[2];
            StringBuilder? sb = null;
            int pos = 0, last = 0, done = 0;
            while ((count < 0 || done < count) && pos <= h.Length)
            {
                bool ok = t.MaxGroup > 0 ? eng.Captures(h, pos, slots, c) : eng.Find(h, pos, out slots[0], out slots[1], c);
                if (!ok) break;
                int ms = slots[0], me = slots[1];
                sb ??= new StringBuilder(input.Length + 16);
                sb.Append(h[last..ms]);
                t.Append(sb, input, slots);
                last = me;
                done++;
                if (me == ms)
                {
                    if (me >= h.Length) break;
                    int nb = Engine<char>.NextBoundary(h, me);
                    sb.Append(h[me..nb]);
                    last = pos = nb;
                }
                else pos = me;
            }
            if (sb == null) return input;
            sb.Append(h[last..]);
            return sb.ToString();
        }
        finally { eng.Return(c); }
    }

    /// <summary>Each match replaced by <paramref name="evaluator"/>'s value (Ruby `gsub { |m| }` with `$~`).</summary>
    public string Replace(string input, Func<MatchData, string> evaluator, int count = -1)
    {
        var sb = new StringBuilder(input.Length + 16);
        var h = input.AsSpan();
        int pos = 0, last = 0, done = 0;
        bool any = false;
        while ((count < 0 || done < count) && pos <= h.Length)
        {
            var slots = new int[SlotCount];
            if (!CapturesAt(h, pos, slots)) break;
            any = true;
            int ms = slots[0], me = slots[1];
            sb.Append(h[last..ms]);
            sb.Append(evaluator(new MatchData(this, input, slots)));
            last = me;
            done++;
            if (me == ms)
            {
                if (me >= h.Length) break;
                int nb = Engine<char>.NextBoundary(h, me);
                sb.Append(h[me..nb]);
                last = pos = nb;
            }
            else pos = me;
        }
        if (!any) return input;
        sb.Append(h[last..]);
        return sb.ToString();
    }

    /// <summary>UTF-8 replace into <paramref name="output"/> (no intermediate strings); returns the number of
    /// replacements. Same template syntax as <see cref="Replace(string, string, int)"/> (UTF-8 encoded).</summary>
    public int Replace(ReadOnlySpan<byte> utf8, ReadOnlySpan<byte> replacement, IBufferWriter<byte> output, int count = -1)
    {
        var t = Template.Parse(Encoding.UTF8.GetString(replacement), this);
        var eng = Engine8;
        var c = eng.Rent();
        try
        {
            var h = utf8;
            Span<int> slots = t.MaxGroup > 0 ? new int[SlotCount] : stackalloc int[2];
            int pos = 0, last = 0, done = 0;
            while ((count < 0 || done < count) && pos <= h.Length)
            {
                bool ok = t.MaxGroup > 0 ? eng.Captures(h, pos, slots, c) : eng.Find(h, pos, out slots[0], out slots[1], c);
                if (!ok) break;
                int ms = slots[0], me = slots[1];
                Write(output, h[last..ms]);
                t.Append(output, h, slots);
                last = me;
                done++;
                if (me == ms)
                {
                    if (me >= h.Length) break;
                    int nb = Engine<byte>.NextBoundary(h, me);
                    Write(output, h[me..nb]);
                    last = pos = nb;
                }
                else pos = me;
            }
            Write(output, h[last..]);
            return done;
        }
        finally { eng.Return(c); }
    }

    /// <summary>UTF-16 replace into <paramref name="destination"/>; false when it is too small.</summary>
    public bool TryReplace(ReadOnlySpan<char> input, ReadOnlySpan<char> replacement, Span<char> destination, out int written, int count = -1)
    {
        var t = Template.Parse(replacement.ToString(), this);
        var eng = Engine16;
        var c = eng.Rent();
        written = 0;
        try
        {
            var h = input;
            Span<int> slots = t.MaxGroup > 0 ? new int[SlotCount] : stackalloc int[2];
            int pos = 0, last = 0, done = 0, w = 0;
            while ((count < 0 || done < count) && pos <= h.Length)
            {
                bool ok = t.MaxGroup > 0 ? eng.Captures(h, pos, slots, c) : eng.Find(h, pos, out slots[0], out slots[1], c);
                if (!ok) break;
                int ms = slots[0], me = slots[1];
                if (!Put(destination, ref w, h[last..ms]) || !t.Append(destination, ref w, h, slots)) return false;
                last = me;
                done++;
                if (me == ms)
                {
                    if (me >= h.Length) break;
                    int nb = Engine<char>.NextBoundary(h, me);
                    if (!Put(destination, ref w, h[me..nb])) return false;
                    last = pos = nb;
                }
                else pos = me;
            }
            if (!Put(destination, ref w, h[last..])) return false;
            written = w;
            return true;
        }
        finally { eng.Return(c); }
    }

    internal static bool Put(Span<char> d, ref int w, ReadOnlySpan<char> s)
    {
        if (w + s.Length > d.Length) return false;
        s.CopyTo(d[w..]);
        w += s.Length;
        return true;
    }

    internal static void Write(IBufferWriter<byte> w, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        data.CopyTo(w.GetSpan(data.Length));
        w.Advance(data.Length);
    }
}

/// <summary>A parsed replacement template (Ruby `sub`/`gsub` syntax).</summary>
internal sealed class Template
{
    // Group >= 0: that group; -1: pre-match; -2: post-match; -3: literal Text
    readonly (int Group, string Text, byte[] Utf8)[] parts;
    public readonly int MaxGroup;

    Template((int, string, byte[])[] p, int max) { parts = p; MaxGroup = max; }

    public static Template Parse(string rep, Regex re)
    {
        var parts = new List<(int, string, byte[])>();
        var lit = new StringBuilder();
        int max = 0;
        void Flush() { if (lit.Length > 0) { var s = lit.ToString(); parts.Add((-3, s, Encoding.UTF8.GetBytes(s))); lit.Clear(); } }
        void Ref(int g) { Flush(); parts.Add((g, "", [])); if (g > max) max = g; }
        for (int i = 0; i < rep.Length; i++)
        {
            char c = rep[i];
            if (c != '\\' || i + 1 >= rep.Length) { lit.Append(c); continue; }
            char d = rep[++i];
            switch (d)
            {
                case >= '0' and <= '9': Ref(d - '0'); break;
                case '&': Ref(0); break;
                case '`': Flush(); parts.Add((-1, "", [])); break;
                case '\'': Flush(); parts.Add((-2, "", [])); break;
                case '\\': lit.Append('\\'); break;
                case 'k' when i + 1 < rep.Length && rep[i + 1] == '<' && rep.IndexOf('>', i + 2) is var close and > 0:
                    {
                        string name = rep[(i + 2)..close];
                        int g = re.GroupNumber(name);
                        if (g < 0) throw new IndexOutOfRangeException("undefined group name reference: " + name);
                        Ref(g);
                        i = close;
                        break;
                    }
                default: lit.Append('\\').Append(d); break;
            }
        }
        Flush();
        return new Template(parts.ToArray(), max);
    }

    public void Append(StringBuilder sb, string input, ReadOnlySpan<int> slots)
    {
        foreach (var (g, text, _) in parts)
        {
            if (g == -3) sb.Append(text);
            else if (g == -1) sb.Append(input.AsSpan(0, slots[0]));
            else if (g == -2) sb.Append(input.AsSpan(slots[1]));
            else if (2 * g + 1 < slots.Length && slots[2 * g] >= 0 && slots[2 * g + 1] >= 0) sb.Append(input.AsSpan(slots[2 * g], slots[2 * g + 1] - slots[2 * g]));
        }
    }

    public void Append(IBufferWriter<byte> w, ReadOnlySpan<byte> input, ReadOnlySpan<int> slots)
    {
        foreach (var (g, _, u8) in parts)
        {
            if (g == -3) Regex.Write(w, u8);
            else if (g == -1) Regex.Write(w, input[..slots[0]]);
            else if (g == -2) Regex.Write(w, input[slots[1]..]);
            else if (2 * g + 1 < slots.Length && slots[2 * g] >= 0 && slots[2 * g + 1] >= 0) Regex.Write(w, input[slots[2 * g]..slots[2 * g + 1]]);
        }
    }

    public bool Append(Span<char> d, ref int w, ReadOnlySpan<char> input, ReadOnlySpan<int> slots)
    {
        foreach (var (g, text, _) in parts)
        {
            bool ok = g switch
            {
                -3 => Regex.Put(d, ref w, text),
                -1 => Regex.Put(d, ref w, input[..slots[0]]),
                -2 => Regex.Put(d, ref w, input[slots[1]..]),
                _ => !(2 * g + 1 < slots.Length && slots[2 * g] >= 0 && slots[2 * g + 1] >= 0) || Regex.Put(d, ref w, input[slots[2 * g]..slots[2 * g + 1]]),
            };
            if (!ok) return false;
        }
        return true;
    }
}
