using System.Text.Json.Nodes;

namespace Alice.Cli.Toolchains;

/// <summary>
/// Once a day, in the background, asks GitHub for the newest stable release and caches it in `ALICE_HOME/update-check.json`.
/// The next interactive command prints a one-line notice. Off in CI and with `ALICE_NO_UPDATE_CHECK=1`.
/// </summary>
public static class UpdateNotifier
{
    static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    static string CachePath(ToolchainHome h) => Path.Combine(h.Root, "update-check.json");

    public static bool Enabled(Func<string, string?> env) =>
        string.IsNullOrEmpty(env("CI")) && string.IsNullOrEmpty(env("ALICE_NO_UPDATE_CHECK")) && string.IsNullOrEmpty(env("GITHUB_ACTIONS"));

    public static bool IsStale(ToolchainHome h, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(CachePath(h))) return true;
            var at = JsonNode.Parse(File.ReadAllText(CachePath(h)))?["checked"]?.GetValue<DateTimeOffset>();
            return at == null || now - at > Interval;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or FormatException or InvalidOperationException) { return true; }
    }

    public static void Save(ToolchainHome h, string latest, DateTimeOffset now)
    {
        Directory.CreateDirectory(h.Root);
        File.WriteAllText(CachePath(h), new JsonObject { ["checked"] = now, ["latest"] = latest }.ToJsonString());
    }

    public static string? Latest(ToolchainHome h)
    {
        try { return File.Exists(CachePath(h)) ? JsonNode.Parse(File.ReadAllText(CachePath(h)))?["latest"]?.GetValue<string>() : null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Notice text when the cached latest stable is newer than `current` (nightlies newer than stable stay quiet).</summary>
    public static string? Notice(ToolchainHome h, string current)
    {
        var latest = ToolchainVersion.TryParse(Latest(h));
        var cur = ToolchainVersion.TryParse(current);
        if (latest == null || cur == null || latest <= cur) return null;
        if (cur.Pre != null && new ToolchainVersion(cur.Major, cur.Minor, cur.Patch, null) >= latest) return null;
        return $"Alice {latest} is available (you have {current}). Run `alice self update`.";
    }

    /// <summary>Starts a background refresh when the cache is stale. Never throws, never blocks the command.</summary>
    public static Task? RefreshInBackground(ToolchainHome h)
    {
        if (!Enabled(Environment.GetEnvironmentVariable) || !IsStale(h, DateTimeOffset.UtcNow)) return null;
        return Task.Run(() =>
        {
            try
            {
                var latest = ReleaseFeed.Pick(new GitHubReleaseSource(TimeSpan.FromSeconds(3)).List(), "stable");
                Save(h, latest?.Version.ToString() ?? App.Version, DateTimeOffset.UtcNow);
            }
            catch (Exception) { /* offline: try again next day */ try { Save(h, Latest(h) ?? App.Version, DateTimeOffset.UtcNow); } catch (IOException) { } }
        });
    }
}
