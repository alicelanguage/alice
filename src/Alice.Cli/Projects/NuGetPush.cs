using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Alice.Cli.Projects;

/// <summary>
/// `alice publish` to a NuGet feed over HTTPS (the NuGet push protocol): the v3 service index names the
/// `PackagePublish/2.0.0` resource, and the package is PUT there as multipart form data with the key in the
/// `X-NuGet-ApiKey` header. `dotnet nuget push` would take the key as a command-line argument, which other local
/// users can read from the process list.
/// </summary>
public static class NuGetPush
{
    public enum Outcome { Published, AlreadyExists }

    public sealed class PushException(string message) : Exception(message);

    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Pushes <paramref name="nupkg"/>; 409 (that version exists) is <see cref="Outcome.AlreadyExists"/>, like --skip-duplicate.</summary>
    public static async Task<Outcome> PushAsync(string source, string apiKey, string nupkg, CancellationToken ct)
    {
        RequireSecure(source);
        string endpoint = source.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? await PublishEndpoint(source, ct).ConfigureAwait(false) : source;
        RequireSecure(endpoint);
        if (!endpoint.EndsWith('/')) endpoint += "/";

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(nupkg, ct).ConfigureAwait(false));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "package", Path.GetFileName(nupkg));
        using var req = new HttpRequestMessage(HttpMethod.Put, endpoint) { Content = form };
        req.Headers.Add("X-NuGet-ApiKey", apiKey);
        req.Headers.Add("X-NuGet-Protocol-Version", "4.1.0");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("alice-cli", App.Version));
        using var resp = await Send(req, endpoint, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Conflict) return Outcome.AlreadyExists;
        if (!resp.IsSuccessStatusCode)
            throw new PushException($"{endpoint} answered {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? " (check the API key and its package scope)" : ""));
        return Outcome.Published;
    }

    /// <summary>The `PackagePublish/2.0.0` URL from a v3 service index.</summary>
    static async Task<string> PublishEndpoint(string index, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, index);
        using var resp = await Send(req, index, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new PushException($"{index} answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var r in doc.RootElement.GetProperty("resources").EnumerateArray())
                if (r.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.StartsWith("PackagePublish/", StringComparison.Ordinal)
                    && r.TryGetProperty("@id", out var id) && id.ValueKind == JsonValueKind.String)
                    return id.GetString()!;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PushException($"{index} is not a NuGet v3 service index");
        }
        throw new PushException($"{index} has no PackagePublish resource (the feed does not accept pushes)");
    }

    static async Task<HttpResponseMessage> Send(HttpRequestMessage req, string url, CancellationToken ct)
    {
        try
        {
            var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            // redirects are not followed: the key header would go along to wherever they point
            if ((int)resp.StatusCode is >= 300 and < 400) { resp.Dispose(); throw new PushException($"{url} redirected; use the final feed URL"); }
            return resp;
        }
        catch (HttpRequestException e) { throw new PushException($"could not reach {url} ({e.Message})"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new PushException($"{url} timed out"); }
    }

    /// <summary>The key travels in a header, so only https (plain http just for a feed on this machine).</summary>
    static void RequireSecure(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
            throw new PushException($"refusing to send the API key to {url}: the feed must use https");
    }
}
