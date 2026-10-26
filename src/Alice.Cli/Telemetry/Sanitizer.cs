using System.Text;
using System.Text.RegularExpressions;

namespace Alice.Cli.Telemetry;

/// <summary>Strips anything personal from crash data: messages, paths, user names, source text, non-Alice frames.</summary>
public static partial class Sanitizer
{
    [GeneratedRegex(@"^\s*at (?<frame>(Alice|System|Microsoft|Spectre)\.[^\s(]+\([^)]*\))")]
    private static partial Regex Frame();

    [GeneratedRegex(@"^(AL|CS)\d{4}$")]
    private static partial Regex Code();

    [GeneratedRegex(@"^(System|Alice|Microsoft)\.[A-Za-z0-9_.`+]*$")]
    private static partial Regex TypeName();

    /// <summary>Exception type (never its message) plus Alice/.NET frames without file or line; other frames become `at <external>`.</summary>
    public static string Stack(string raw)
    {
        var lines = raw.Replace("\r", "").Split('\n');
        var sb = new StringBuilder(SafeType(lines[0].Split(':')[0].Trim()));
        string? last = null;
        foreach (var l in lines.Skip(1))
        {
            if (!l.TrimStart().StartsWith("at ")) continue;
            var m = Frame().Match(l);
            var f = m.Success ? "at " + m.Groups["frame"].Value : "at <external>";
            if (f == last && !m.Success) continue;
            sb.Append("\n   ").Append(f);
            last = f;
        }
        var s = sb.ToString();
        return s.Length > 4000 ? s[..4000] : s;
    }

    static string SafeType(string t) => TypeName().IsMatch(t) ? t : "Exception";

    public static string Type(Exception e) => SafeType(e.GetType().FullName ?? "");

    /// <summary>Counts of well-formed diagnostic codes only (AL#### / CS####); anything else is dropped.</summary>
    public static Dictionary<string, int> Codes(IEnumerable<string> codes)
    {
        var d = new Dictionary<string, int>();
        foreach (var c in codes) if (Code().IsMatch(c)) d[c] = d.GetValueOrDefault(c) + 1;
        return d;
    }
}
