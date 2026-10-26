using Alice.TelemetryServer;
using Alice.Telemetry.Wire;

// Self-hostable collector for the alice CLI's opt-in telemetry (ADR 0031, docs/telemetry.md "Running the server").
// Privacy: no request logging, no forwarded headers, no IP is read or stored; only the validated body reaches storage.
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole().AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = TelemetryWire.MaxBodyBytes;
    k.AddServerHeader = false;
});

string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
var dbPath = Env("ALICE_TELEMETRY_DB", "/data/telemetry.db");
int retention = int.Parse(Env("ALICE_TELEMETRY_RETENTION_DAYS", "180"));
int perHour = int.Parse(Env("ALICE_TELEMETRY_RATE_PER_HOUR", "600"));
var store = new EventStore(dbPath);
var collector = new Collector(store, new RateLimiter(perHour), Environment.GetEnvironmentVariable("ALICE_TELEMETRY_ADMIN_TOKEN"));

var app = builder.Build();

app.MapPost("/v1/events", async (HttpRequest req) =>
{
    if (req.ContentLength is > TelemetryWire.MaxBodyBytes) return Results.StatusCode(413);
    using var ms = new MemoryStream();
    try { await req.Body.CopyToAsync(ms); }
    catch (BadHttpRequestException) { return Results.StatusCode(413); }
    var (status, body) = collector.Ingest(ms.ToArray());
    return Results.Content(body, "application/json", statusCode: status);
});

app.MapGet("/v1/stats", (HttpRequest req) =>
{
    var (status, body) = collector.Stats(req.Headers.Authorization.ToString());
    return body is null ? Results.StatusCode(status) : Results.Json(body);
});

app.MapGet("/healthz", () => Results.Ok());

// daily retention purge
_ = Task.Run(async () =>
{
    using var t = new PeriodicTimer(TimeSpan.FromHours(24));
    do store.Purge(retention, DateTime.UtcNow); while (await t.WaitForNextTickAsync());
});

app.Run();
