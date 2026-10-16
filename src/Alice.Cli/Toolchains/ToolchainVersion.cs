namespace Alice.Cli.Toolchains;

/// <summary>`MAJOR.MINOR.PATCH[-pre]` (leading `v` allowed). Prereleases sort before their release; `-nightly.N` is the nightly channel.</summary>
public sealed record ToolchainVersion(int Major, int Minor, int Patch, string? Pre) : IComparable<ToolchainVersion>
{
    public string Channel => Pre == null ? "stable" : Pre.StartsWith("nightly") ? "nightly" : "preview";

    public static ToolchainVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; if (pre.Length == 0) return null; }
        var parts = s.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b) || !int.TryParse(parts[2], out int c)) return null;
        if (a < 0 || b < 0 || c < 0) return null;
        return new ToolchainVersion(a, b, c, pre);
    }

    public static ToolchainVersion Parse(string text) =>
        TryParse(text) ?? throw new ToolchainException($"'{text}' is not a toolchain version", "use a version like 0.10.1, or a channel: stable, nightly");

    public int CompareTo(ToolchainVersion? o)
    {
        if (o is null) return 1;
        int c = Major.CompareTo(o.Major);
        if (c == 0) c = Minor.CompareTo(o.Minor);
        if (c == 0) c = Patch.CompareTo(o.Patch);
        if (c != 0) return c;
        if (Pre == o.Pre) return 0;
        if (Pre == null) return 1;
        if (o.Pre == null) return -1;
        return ComparePre(Pre, o.Pre);
    }

    static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool nx = long.TryParse(x[i], out long lx), ny = long.TryParse(y[i], out long ly);
            int c = nx && ny ? lx.CompareTo(ly) : nx ? -1 : ny ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator <(ToolchainVersion a, ToolchainVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ToolchainVersion a, ToolchainVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ToolchainVersion a, ToolchainVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ToolchainVersion a, ToolchainVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre != null ? "-" + Pre : "");
}

/// <summary>A toolchain operation failed in a way the user can act on.</summary>
public sealed class ToolchainException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}
