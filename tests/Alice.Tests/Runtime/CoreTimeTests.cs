using System.Text;
using Alice.Core.Time;

namespace Alice.Tests.Runtime;

/// <summary>Alice.Core.Time: Ruby Time, strftime, Duration, Clock.</summary>
[Collection("console")]
public class CoreTimeTests
{
    // Friday 2 January 2026, 03:04:05.25 at +09:00
    static readonly Time T = Time.WithOffset(2026, 1, 2, 3, 4, 5.25, 9 * 3600);

    [Theory]
    [InlineData("%Y-%m-%d %H:%M:%S", "2026-01-02 03:04:05")]
    [InlineData("%y %C %j %e|%-d|%_m|%-m", "26 20 002  2|2| 1|1")]
    [InlineData("%I %l %p %P", "03  3 AM am")]
    [InlineData("%a %A %b %B %h", "Fri Friday Jan January Jan")]
    [InlineData("%^a %^B %#b %10A|%-10A|", "FRI JANUARY JAN     Friday|Friday|")]
    [InlineData("%u %w %U %W %V %G %g", "5 5 00 00 01 2026 26")]
    [InlineData("%L %N %3N %6N", "250 250000000 250 250000")]
    [InlineData("%z %:z %::z %Z", "+0900 +09:00 +09:00:00 +09:00")]
    [InlineData("%c", "Fri Jan  2 03:04:05 2026")]
    [InlineData("%D %F %T %R %r %x %X", "01/02/26 2026-01-02 03:04:05 03:04 03:04:05 AM 01/02/26 03:04:05")]
    [InlineData("%v", " 2-JAN-2026")]
    [InlineData("100%% %n%t|%Q", "100% \n\t|1767290645250")]
    [InlineData("%k", " 3")]
    [InlineData("%q %", "%q %")]
    public void Strftime_follows_ruby(string fmt, string expected) => Assert.Equal(expected, T.Strftime(fmt));

    [Fact]
    public void Epoch_and_conversions()
    {
        Assert.Equal(T.ToI().ToString(), T.Strftime("%s"));
        Assert.Equal(1767290645L, T.ToI());
        Assert.Equal(T.ToUnixMilliseconds().ToString(), T.Strftime("%Q"));
        Assert.Equal(1767290645.25, T.ToF(), 6);
        var u = Time.Utc(2026, 1, 2, 3, 4, 5);
        Assert.True(u.IsUtc);
        Assert.Equal("2026-01-02 03:04:05 UTC", u.ToString());
        Assert.Equal("2026-01-02 03:04:05 +0900", Time.WithOffset(2026, 1, 2, 3, 4, 5, 9 * 3600).ToString());
        Assert.Equal("2026-01-02T03:04:05Z", u.Iso8601());
        Assert.Equal("2026-01-02T03:04:05.250+09:00", T.Iso8601(3));
        Assert.Equal(u, Time.Parse("2026-01-02T03:04:05Z"));
        Assert.True(Time.Parse("2026-01-02T03:04:05Z").IsUtc);
        Assert.Equal(5, T.Wday);
        Assert.Equal(2, T.Yday);
        Assert.Equal(250000, T.Usec);
        Assert.Equal(9 * 3600, T.UtcOffset);
        Assert.Equal(T.ToI(), T.ToUtc().ToI());
        Assert.Equal(18, T.ToUtc().Hour);
    }

    [Fact]
    public void Arithmetic_with_durations()
    {
        var u = Time.Utc(2026, 1, 2);
        Assert.Equal(Time.Utc(2026, 1, 2, 0, 5), u + Duration.FromMinutes(5));
        Assert.Equal(Duration.FromHours(1), Time.Utc(2026, 1, 2, 1) - u);
        Assert.Equal(Time.Utc(2026, 1, 1, 23, 59, 59.5), u - 0.5);
        Assert.True(u < u + 1.0);
        Assert.Equal("1h2m3.5s", (Duration.FromHours(1) + Duration.FromMinutes(2) + Duration.FromSeconds(3.5)).ToString());
        Assert.Equal("250ms", Duration.FromMilliseconds(250).ToString());
        Assert.Equal("-1.5s", (-Duration.FromSeconds(1.5)).ToString());
        Assert.Equal("0s", Duration.Zero.ToString());
        Assert.Equal(300.0, Duration.FromMinutes(5).TotalSeconds);
        Assert.Equal(2.0, Duration.FromMinutes(10) / Duration.FromMinutes(5));
        TimeSpan ts = Duration.FromSeconds(2);
        Assert.Equal(TimeSpan.FromSeconds(2), ts);
        Assert.True(Clock.Since(Clock.Timestamp()) >= Duration.Zero);
        Assert.True(Time.Measure(() => Thread.Sleep(1)) > Duration.Zero);
    }

    [Fact]
    public void Prelude_sugar_builds_durations()
    {
        Assert.Equal(Duration.FromMinutes(5), Alice.Runtime.Prelude.minutes(5));
        Assert.Equal(Duration.FromSeconds(1.5), Alice.Runtime.Prelude.seconds(1.5));
        Assert.Equal(Duration.FromHours(2), Alice.Runtime.Prelude.hours(2));
        Assert.Equal(Duration.FromDays(1), Alice.Runtime.Prelude.days(1));
        Assert.Equal(Duration.FromMilliseconds(30), Alice.Runtime.Prelude.milliseconds(30));
        Assert.Equal(Duration.FromWeeks(1), Alice.Runtime.Prelude.weeks(1));
    }

    [Fact]
    public void Alice_programs_use_time_and_durations()
    {
        string src = "using Alice.Core.Time\nt = Time.utc(2026, 1, 2, 3, 4, 5)\nputs t.strftime(\"%Y-%m-%d %H:%M\")\nputs t.add(5.minutes).strftime(\"%H:%M\")\nputs 90.seconds.to_s";
        Assert.Equal("2026-01-02 03:04\n03:09\n1m30s", Alice.Compiler.AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n"));
    }
}

/// <summary>strftime into spans allocates nothing.</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class CoreTimeAllocationTests
{
    [Fact]
    public void TryStrftime_allocates_nothing()
    {
        var t = Time.WithOffset(2026, 1, 2, 3, 4, 5.25, 3600);
        var chars = new char[128];
        var bytes = new byte[128];
        int sink = 0;
        void Body()
        {
            for (int i = 0; i < 100; i++)
            {
                t.TryStrftime(chars, out int n, "%Y-%m-%dT%H:%M:%S.%3N%:z %a %B %j %c");
                t.TryStrftimeUtf8(bytes, out int b, "%F %T %z");
                Duration.FromSeconds(i).TryFormat(chars, out int d);
                sink += n + b + d;
            }
        }
        Body();
        Assert.Equal(0, AllocationProbe.Least(Body));
        Assert.NotEqual(0, sink);
        Assert.Equal("2026-01-02 03:04:05 +0100", Encoding.UTF8.GetString(bytes.AsSpan(0, Encoding.UTF8.GetByteCount("2026-01-02 03:04:05 +0100"))));
    }
}
