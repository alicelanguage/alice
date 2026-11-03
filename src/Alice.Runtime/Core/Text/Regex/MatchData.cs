using System.Collections.Immutable;
using System.Text;

namespace Alice.Core.Text;

/// <summary>
/// Ruby MatchData: the subject, the regex and every group's offsets (code units of the subject: chars of a string,
/// bytes of UTF-8 input). One allocation per match plus the offsets array. Group values are created on access.
/// </summary>
public sealed class MatchData
{
    readonly string? text;
    readonly byte[]? utf8;
    readonly int[] offs;   // 2 per group, -1 unset
    readonly ImmutableArray<string?> names;   // per group index (0 and unnamed: null)

    internal MatchData(Regex regex, string text, int[] slots) { Regex = regex; this.text = text; offs = slots; names = regex.GroupNames; }
    internal MatchData(Regex regex, byte[] utf8, int[] slots) { Regex = regex; this.utf8 = utf8; offs = slots; names = regex.GroupNames; }
    MatchData(string text, int[] slots, ImmutableArray<string?> names) { this.text = text; offs = slots; this.names = names; }

    /// <summary>A MatchData over offsets computed elsewhere (2 per group, -1 when a group did not take part), with
    /// the group names by index (null entries for unnamed groups); <see cref="Regex"/> is null.</summary>
    public static MatchData FromOffsets(string subject, ReadOnlySpan<int> offsets, IReadOnlyList<string?>? groupNames = null)
    {
        var n = new string?[offsets.Length / 2];
        if (groupNames != null) for (int i = 0; i < n.Length && i < groupNames.Count; i++) n[i] = groupNames[i];
        return new(subject, offsets.ToArray(), n.ToImmutableArray());
    }

    /// <summary>The regex that matched (Ruby `regexp`); null for a MatchData built from offsets.</summary>
    public Regex? Regex { get; }

    /// <summary>The subject (Ruby `string`); UTF-8 subjects are decoded.</summary>
    public string String => text ?? Encoding.UTF8.GetString(utf8!);

    /// <summary>Number of groups including group 0 (Ruby `size`/`length`).</summary>
    public int Size => offs.Length / 2;

    /// <summary>Ruby `size`.</summary>
    public int Length => Size;

    string Slice(int s, int e) => text != null ? text[s..e] : Encoding.UTF8.GetString(utf8!, s, e - s);

    /// <summary>Group <paramref name="i"/>'s text (negative counts from the end), null when it did not take part.</summary>
    public string? this[int i]
    {
        get
        {
            if (i < 0) i += Size;
            if (i < 0 || i >= Size || offs[2 * i] < 0 || offs[2 * i + 1] < 0) return null;
            return Slice(offs[2 * i], offs[2 * i + 1]);
        }
    }

    /// <summary>The named group's text (the last participating group of that name), null when unset.</summary>
    public string? this[string name]
    {
        get
        {
            int found = -1;
            for (int g = Size - 1; g >= 1; g--)
                if (names[g] == name) { found = g; if (offs[2 * g] >= 0) return this[g]; }
            if (found < 0) throw new IndexOutOfRangeException("undefined group name reference: " + name);
            return null;
        }
    }

    /// <summary>Start offset of group i (-1 when unset). Ruby `begin(i)`.</summary>
    public int Begin(int i) => i >= 0 && i < Size ? offs[2 * i] : throw new IndexOutOfRangeException($"index {i} out of matches");

    /// <summary>End offset (exclusive) of group i (-1 when unset). Ruby `end(i)`.</summary>
    public int End(int i) => i >= 0 && i < Size ? offs[2 * i + 1] : throw new IndexOutOfRangeException($"index {i} out of matches");

    /// <summary>`[begin, end]` of group i. Ruby `offset(i)`.</summary>
    public int[] Offset(int i) => [Begin(i), End(i)];

    /// <summary>Text before the match (Ruby `pre_match`, `` $` ``).</summary>
    public string PreMatch => Slice(0, offs[0]);

    /// <summary>Text after the match (Ruby `post_match`, `$'`).</summary>
    public string PostMatch => text != null ? text[offs[1]..] : Slice(offs[1], utf8!.Length);

    /// <summary>Groups 1..n (null for groups that did not take part). Ruby `captures`.</summary>
    public string?[] Captures
    {
        get
        {
            var r = new string?[Size - 1];
            for (int g = 1; g < Size; g++) r[g - 1] = this[g];
            return r;
        }
    }

    /// <summary>Distinct group names. Ruby `names`.</summary>
    public string[] Names => names.Where(n => n != null).Distinct().ToArray()!;

    /// <summary>Name to text (the last participating group of each name). Ruby `named_captures`.</summary>
    public Dictionary<string, string?> NamedCaptures
    {
        get
        {
            var d = new Dictionary<string, string?>();
            foreach (var n in Names) d[n] = this[n];
            return d;
        }
    }

    /// <summary>Whole match followed by the groups. Ruby `to_a`.</summary>
    public string?[] ToArray()
    {
        var r = new string?[Size];
        for (int g = 0; g < Size; g++) r[g] = this[g];
        return r;
    }

    /// <summary>Ruby `to_a` (the .NET spelling of an Alice `to_a`, ADR 0019).</summary>
    public string?[] ToA() => ToArray();

    /// <summary>Ruby `values_at(i, j...)`.</summary>
    public string?[] ValuesAt(params int[] indexes) => indexes.Select(i => this[i]).ToArray();

    /// <summary>The whole match (Ruby `to_s`).</summary>
    public override string ToString() => this[0] ?? "";
}
