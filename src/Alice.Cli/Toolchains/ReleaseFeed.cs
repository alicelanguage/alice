using System.Net.Http.Headers;
using System.Text.Json;

namespace Alice.Cli.Toolchains;

public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record Release(ToolchainVersion Version, string Tag, DateTimeOffset Published, string Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string rid) => Assets.FirstOrDefault(a => a.Name == Platform.ArchiveName(Version.ToString(), rid));
    public ReleaseAsset? Checksum(string rid) => Assets.FirstOrDefault(a => a.Name == Platform.ArchiveName(Version.ToString(), rid) + ".sha256");
}

/// <summary>Where releases come from: GitHub in production, a fake in tests.</summary>
public interface IReleaseSource
{
    IReadOnlyList<Release> List();
    Stream Open(string url);
}

public static class ReleaseFeed
{
    public const string DefaultRepo = "alicelanguage/alice";

    /// <summary>Parses the GitHub `GET /repos/{repo}/releases` response; drafts and unversioned tags are skipped.</summary>
    public static List<Release> Parse(string json)
    {
        var list = new List<Release>();
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var arr))
                foreach (var a in arr.EnumerateArray())
                    assets.Add(new ReleaseAsset(Str(a, "name") ?? "", Str(a, "browser_download_url") ?? "", a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
            // nightlies share the moving `nightly` tag; their version lives in the asset names
            var version = ToolchainVersion.TryParse(Str(r, "tag_name")) ?? FromAssets(assets);
            if (version == null) continue;
            var published = DateTimeOffset.TryParse(Str(r, "published_at"), out var p) ? p : DateTimeOffset.MinValue;
            list.Add(new Release(version, Str(r, "tag_name") ?? "", published, Str(r, "body") ?? "", assets));
        }
        return list;
    }

    static ToolchainVersion? FromAssets(List<ReleaseAsset> assets)
    {
        foreach (var a in assets)
        {
            if (!a.Name.StartsWith("alice-")) continue;
            var rest = a.Name["alice-".Length..];
            foreach (var rid in new[] { "-linux-", "-win-", "-osx-" })
                if (rest.IndexOf(rid, StringComparison.Ordinal) is int i and > 0 && ToolchainVersion.TryParse(rest[..i]) is { } v) return v;
        }
        return null;
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>`stable` = newest release, `nightly` = newest nightly, otherwise an exact version.</summary>
    public static Release? Pick(IEnumerable<Release> releases, string spec)
    {
        spec = spec.Trim();
        return spec switch
        {
            "stable" or "latest" => releases.Where(r => r.Version.Pre == null).MaxBy(r => r.Version),
            "nightly" => releases.Where(r => r.Version.Channel == "nightly").MaxBy(r => r.Version),
            _ => ToolchainVersion.TryParse(spec) is { } v ? releases.FirstOrDefault(r => r.Version.Equals(v)) : null,
        };
    }
}

/// <summary>GitHub Releases over HTTPS. `ALICE_RELEASES_REPO` points at a fork; `GITHUB_TOKEN` raises rate limits.</summary>
public sealed class GitHubReleaseSource : IReleaseSource
{
    static readonly HttpClient Http = CreateClient();
    readonly string repo;
    readonly TimeSpan timeout;

    public GitHubReleaseSource(TimeSpan? timeout = null)
    {
        repo = Environment.GetEnvironmentVariable("ALICE_RELEASES_REPO") is { Length: > 0 } r ? r : ReleaseFeed.DefaultRepo;
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("alice-cli", App.Version));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
            h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return h;
    }

    public IReadOnlyList<Release> List()
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var resp = Http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=50", cts.Token).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                throw new ToolchainException($"GitHub answered {(int)resp.StatusCode} for {repo} releases",
                    (int)resp.StatusCode == 403 ? "rate limited: set GITHUB_TOKEN or retry later" : "check the network or ALICE_RELEASES_REPO");
            return ReleaseFeed.Parse(resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ToolchainException($"could not reach GitHub releases ({e.Message})", "check your connection; offline installs: scripts/install.sh --archive FILE");
        }
    }

    public Stream Open(string url)
    {
        var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new ToolchainException($"download failed ({(int)resp.StatusCode}): {url}");
        return resp.Content.ReadAsStream();
    }
}
