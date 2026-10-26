using System.Text;

namespace Alice.Cli.Telemetry;

/// <summary>Batched, fire-and-forget upload with a short timeout. Failures are silent and never change a command's result.</summary>
public static class TelemetrySender
{
    /// <summary>
    /// TODO(telemetry): placeholder for the project's own collector (tools/telemetry-server). It is NOT used while
    /// <see cref="ServerLive"/> is false, so nothing is sent unless an endpoint is configured explicitly
    /// (`endpoint = "https://..."` in telemetry.toml, or ALICE_TELEMETRY_ENDPOINT). Flip ServerLive only after a real
    /// deployment is confirmed, and update docs/telemetry.md.
    /// </summary>
    public const string DefaultEndpoint = "https://telemetry.alicelang.org/v1/events";

    /// <summary>TODO(telemetry): false until the server above is actually deployed. Keeps the default non-sending.</summary>
    public const bool ServerLive = false;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static string? ResolveEndpoint(Consent c, Func<string, string?> env)
    {
        var e = env("ALICE_TELEMETRY_ENDPOINT") is { Length: > 0 } v ? v : c.Endpoint ?? (ServerLive ? DefaultEndpoint : "");
        return Uri.TryCreate(e, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? e : null;
    }

    /// <summary>Posts the queued batch as `{"events":[...]}`; on failure the batch is re-queued (still capped).</summary>
    public static Task FlushAsync(TelemetryQueue queue, string endpoint)
    {
        var batch = queue.Drain();
        if (batch.Count == 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = Timeout };
                var body = "{\"events\":[" + string.Join(",", batch) + "]}";
                using var r = await http.PostAsync(endpoint, new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) Requeue(queue, batch);
            }
            catch (Exception) { Requeue(queue, batch); }
        });
    }

    static void Requeue(TelemetryQueue q, List<string> batch)
    {
        foreach (var l in batch) q.Append(l);
    }
}
