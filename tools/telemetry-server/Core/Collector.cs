using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Alice.Telemetry.Wire;

namespace Alice.TelemetryServer;

/// <summary>Fixed-window rate limit keyed by install id (never by IP).</summary>
public sealed class RateLimiter(int perHour)
{
    readonly ConcurrentDictionary<string, (long Hour, int Count)> windows = new();

    public bool Allow(string installId, int events, DateTime nowUtc)
    {
        long hour = nowUtc.Ticks / TimeSpan.TicksPerHour;
        bool ok = false;
        windows.AddOrUpdate(installId,
            _ => { ok = events <= perHour; return (hour, ok ? events : 0); },
            (_, w) =>
            {
                (long Hour, int Count) cur = w.Hour == hour ? w : (hour, 0);
                ok = cur.Count + events <= perHour;
                return ok ? (hour, cur.Count + events) : cur;
            });
        if (windows.Count > 100_000) foreach (var k in windows.Where(kv => kv.Value.Hour < hour).Select(kv => kv.Key).ToList()) windows.TryRemove(k, out _);
        return ok;
    }
}

/// <summary>Transport-independent request handling, so the HTTP host stays a thin shell and tests need no ASP.NET runtime.</summary>
public sealed class Collector(EventStore store, RateLimiter limiter, string? adminToken, Func<DateTime>? clock = null)
{
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);

    /// <summary>POST /v1/events. Only the body is seen: no headers, no remote address.</summary>
    public (int Status, string Body) Ingest(ReadOnlySpan<byte> body)
    {
        if (body.Length > TelemetryWire.MaxBodyBytes) return (413, "{\"error\":\"body too large\"}");
        if (!TelemetryWire.TryParseBatch(body, out var events, out var error))
            return (400, "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(error) + "}");
        foreach (var g in events.GroupBy(e => e.InstallId))
            if (!limiter.Allow(g.Key, g.Count(), now())) return (429, "{\"error\":\"rate limited\"}");
        store.Insert(events, now());
        return (202, "{\"accepted\":" + events.Count + "}");
    }

    /// <summary>GET /v1/stats with `Authorization: Bearer TOKEN`. Disabled when no token is configured.</summary>
    public (int Status, object? Body) Stats(string? authorization)
    {
        if (string.IsNullOrEmpty(adminToken)) return (404, null);
        var expected = Encoding.UTF8.GetBytes("Bearer " + adminToken);
        var given = Encoding.UTF8.GetBytes(authorization ?? "");
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return (401, null);
        return (200, store.Stats());
    }
}
