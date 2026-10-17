using System.Text.Json;

namespace Alice.Cli.Lsp;

/// <summary>On-demand public NuGet metadata. No restore, assembly download, or background polling.</summary>
public sealed class NuGetService : IDisposable
{
    readonly HttpClient client;
    readonly bool ownsClient;
    readonly Dictionary<string, (DateTime At, string[] Values)> cache = new();
    public bool Enabled { get; set; } = true;
    public NuGetService(HttpClient? client = null)
    {
        ownsClient = client == null;
        this.client = client ?? new HttpClient();
    }
    public string[] Ids(string prefix) => prefix.Length < 2 ? [] : Fetch("https://azuresearch-usnc.nuget.org/autocomplete?take=40&prerelease=false&q=" + Uri.EscapeDataString(prefix), "data");
    public string[] Versions(string id) => Fetch("https://api.nuget.org/v3-flatcontainer/" + Uri.EscapeDataString(id.ToLowerInvariant()) + "/index.json", "versions")
        .Where(v => !v.Contains('-')).Reverse().Take(40).ToArray();
    string[] Fetch(string url, string field)
    {
        if (!Enabled) return [];
        if (cache.TryGetValue(url, out var entry) && DateTime.UtcNow - entry.At < TimeSpan.FromMinutes(10)) return entry.Values;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var json = client.GetStringAsync(url, timeout.Token).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var values = doc.RootElement.GetProperty(field).EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (cache.Count >= 128) cache.Clear();
            cache[url] = (DateTime.UtcNow, values);
            return values;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return []; }
    }
    public void Dispose() { if (ownsClient) client.Dispose(); cache.Clear(); }
}
