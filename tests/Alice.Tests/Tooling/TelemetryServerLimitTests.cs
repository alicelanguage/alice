using Alice.TelemetryServer;

namespace Alice.Tests.Tooling;

/// <summary>Collector rate limits (tools/telemetry-server): install ids are client-chosen, so a global cap bounds the database.</summary>
public class TelemetryServerLimitTests
{
    [Fact]
    public void Fresh_install_ids_cannot_bypass_the_global_cap()
    {
        var limiter = new RateLimiter(perHour: 100, globalPerHour: 3);
        var now = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3; i++) Assert.True(limiter.Allow("id-" + i, 1, now));
        Assert.False(limiter.Allow("id-new", 1, now));
        Assert.True(limiter.Allow("id-new", 1, now.AddHours(1)));
    }

    [Fact]
    public void A_rejected_install_does_not_use_up_the_global_budget()
    {
        var limiter = new RateLimiter(perHour: 1, globalPerHour: 2);
        var now = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(limiter.Allow("a", 1, now));
        Assert.False(limiter.Allow("a", 1, now));
        Assert.True(limiter.Allow("b", 1, now));
    }
}
