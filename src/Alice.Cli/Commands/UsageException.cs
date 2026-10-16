namespace Alice.Cli.Commands;

/// <summary>A user mistake (missing argument, file, project): one-line error + hint, never a stack trace.</summary>
public sealed class UsageException(string message, string? hint = null, int exitCode = 2) : Exception(message)
{
    public string? Hint { get; } = hint;
    public int ExitCode { get; } = exitCode;

    public static UsageException NoProject() => new(Localization.Strings.T("no.project"), Localization.Strings.T("hint.no.project"));

    public static UsageException Usage(string usage) => new("usage: " + usage);

    /// <summary>Closest candidate within edit distance 2 (case-insensitive), for "did you mean".</summary>
    public static string? Closest(string word, IEnumerable<string> candidates) =>
        candidates.Select(c => (c, d: Distance(word.ToLowerInvariant(), c.ToLowerInvariant())))
            .Where(x => x.d <= Math.Max(1, Math.Min(2, word.Length / 3)))
            .OrderBy(x => x.d).Select(x => x.c).FirstOrDefault();

    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);   // transposition
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Throws "file not found" (exit 1) with a close-name suggestion from the same directory.</summary>
    public static void RequireFile(string path)
    {
        if (File.Exists(path)) return;
        string dir = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";
        var near = Directory.Exists(dir) ? Closest(Path.GetFileName(path), Directory.GetFiles(dir, "*.al").Select(Path.GetFileName)!) : null;
        throw new UsageException(Localization.Strings.T("file.not.found", path),
            near != null ? Localization.Strings.T("did.you.mean", Path.Combine(dir == "." ? "" : dir, near)) : null, 1);
    }
}
