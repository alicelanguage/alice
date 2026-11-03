using System.Security;
using System.Text;

namespace Alice.Cli.Projects;

/// <summary>
/// alice.toml values reach the generated .alproj as literal text: validated where they have a grammar
/// (package ids, versions, target frameworks), XML- and MSBuild-escaped where they are free text.
/// </summary>
public static class MsBuildText
{
    /// <summary>NuGet package id: dot/dash-separated runs of letters, digits and `_` (also rules out `/`, `\` and `..`).</summary>
    public static readonly Alice.Core.Text.Regex PackageId = new(@"\A[A-Za-z0-9_]+(?:[.-][A-Za-z0-9_]+)*\z");

    /// <summary>NuGet version or range: `1.2.3`, `3.8.*`, `[1.0, 2.0)`.</summary>
    public static readonly Alice.Core.Text.Regex VersionRange = new(@"\A[0-9A-Za-z.+*\[\](), -]+\z");

    /// <summary>Target framework moniker: `net10.0`, `net10.0-windows`.</summary>
    public static readonly Alice.Core.Text.Regex TargetFramework = new(@"\A[A-Za-z0-9.-]+\z");

    /// <summary>
    /// Literal text for an MSBuild property value or (with <paramref name="item"/>) an item spec: `%XX` for the characters
    /// MSBuild expands (`$` `@` `%`, plus `;` `*` `?` in items), then XML-escaped.
    /// </summary>
    public static string Escape(string s, bool item = false)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '%' or '$' or '@' || (item && c is ';' or '*' or '?')) sb.Append('%').Append(((int)c).ToString("X2"));
            else sb.Append(c);
        }
        return SecurityElement.Escape(sb.ToString());
    }
}
