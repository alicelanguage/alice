using System.Net;
using System.Net.Sockets;
using System.Text;
using Alice.Cli.Telemetry;
using Alice.TelemetryServer;

namespace Alice.Tests.Integration;

/// <summary>Client -> in-process collector round trip (tools/telemetry-server core over a loopback HTTP listener).</summary>
public class TelemetryServerTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "alice-telsrv-" + Guid.NewGuid().ToString("N"));
    readonly EventStore store;

    public TelemetryServerTests()
    {
        Directory.CreateDirectory(dir);
        store = new EventStore(Path.Combine(dir, "t.db"));
    }

    public void Dispose()
    {
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static Exception Crash()
    {
        try { throw new InvalidOperationException("boom in /home/bob/secret/main.al: val x = 42"); }
        catch (Exception e) { return e; }
    }

    [Fact]
    public async Task RoundTripStoresOnlySanitizedFields()
    {
        var collector = new Collector(store, new RateLimiter(100), "tok");
        int port = FreePort();
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            var (status, body) = collector.Ingest(ms.ToArray());
            ctx.Response.StatusCode = status;
            var b = Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(b);
            ctx.Response.Close();
        });

        var q = new TelemetryQueue(dir);
        var ev = TelemetryEvent.Create("build", "0.13.2", Consent.NewId(), 70, TimeSpan.FromMilliseconds(120), Crash(),
            new() { ["AL0202"] = 2 }, new() { ["transpile"] = 30, ["emit"] = 80 });
        q.Append(ev.ToJson());
        await TelemetrySender.FlushAsync(q, $"http://127.0.0.1:{port}/v1/events");
        await serve.WaitAsync(TimeSpan.FromSeconds(10));

        var text = string.Join("\n", store.AllText());
        Assert.Contains("build", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("AL0202", text);
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("bob", text);
        Assert.DoesNotContain("main.al", text);
        Assert.DoesNotContain("val x", text);
        Assert.DoesNotContain("/", text.Replace("transpile", ""));
        Assert.Empty(q.Drain()); // sent, not re-queued

        var (code, stats) = collector.Stats("Bearer tok");
        Assert.Equal(200, code);
        var s = (Dictionary<string, object>)stats!;
        Assert.Equal(1L, s["events"]);
        Assert.Equal(2L, ((Dictionary<string, long>)s["error_codes"])["AL0202"]);
        Assert.Equal(120L, ((Dictionary<string, long>)s["duration_ms"])["p95"]);
    }

    [Theory]
    [InlineData("{\"events\":[],\"ip\":\"1.2.3.4\"}")]
    [InlineData("{\"events\":[{\"schema\":1,\"path\":\"/home/bob\"}]}")]
    [InlineData("not json")]
    public void RejectsUnknownOrMalformed(string body)
    {
        var c = new Collector(store, new RateLimiter(100), null);
        Assert.Equal(400, c.Ingest(Encoding.UTF8.GetBytes(body)).Status);
        Assert.Empty(store.AllText());
    }

    [Fact]
    public void RejectsUnsanitizedStackAndOversizeBody()
    {
        var c = new Collector(store, new RateLimiter(100), null);
        var json = TelemetryEvent.Create("run", "0.13.2", Consent.NewId(), 70, TimeSpan.Zero, null, [], null).ToJson()
            .Replace("\"error_codes\"", "\"crash\":{\"type\":\"System.Exception\",\"stack\":\"System.Exception\\n   at Alice.X() in /home/bob/a.cs:line 1\"},\"error_codes\"");
        Assert.Equal(400, c.Ingest(Encoding.UTF8.GetBytes("{\"events\":[" + json + "]}")).Status);
        Assert.Equal(413, c.Ingest(new byte[Alice.Telemetry.Wire.TelemetryWire.MaxBodyBytes + 1]).Status);
    }

    [Fact]
    public void RateLimitsPerInstallIdAndStatsNeedToken()
    {
        var c = new Collector(store, new RateLimiter(2), "tok");
        string Batch(string id) => "{\"events\":[" + TelemetryEvent.Create("run", "0.13.2", id, 0, TimeSpan.Zero, null, [], null).ToJson() + "]}";
        var a = Consent.NewId();
        Assert.Equal(202, c.Ingest(Encoding.UTF8.GetBytes(Batch(a))).Status);
        Assert.Equal(202, c.Ingest(Encoding.UTF8.GetBytes(Batch(a))).Status);
        Assert.Equal(429, c.Ingest(Encoding.UTF8.GetBytes(Batch(a))).Status);
        Assert.Equal(202, c.Ingest(Encoding.UTF8.GetBytes(Batch(Consent.NewId()))).Status);
        Assert.Equal(401, c.Stats("Bearer nope").Status);
        Assert.Equal(404, new Collector(store, new RateLimiter(1), null).Stats("Bearer tok").Status);
    }

    [Fact]
    public void RetentionPurgesOldEvents()
    {
        var old = new Collector(store, new RateLimiter(100), null, () => DateTime.UtcNow.AddDays(-200));
        var json = "{\"events\":[" + TelemetryEvent.Create("run", "0.13.2", Consent.NewId(), 0, TimeSpan.Zero, null, new() { ["AL0101"] = 1 }, null).ToJson() + "]}";
        Assert.Equal(202, old.Ingest(Encoding.UTF8.GetBytes(json)).Status);
        Assert.Equal(1, store.Purge(180, DateTime.UtcNow));
        Assert.Empty(store.AllText());
    }
}
