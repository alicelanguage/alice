using System.Diagnostics;
using System.Globalization;
using System.Text;
using Alice.Runtime;

namespace Alice.Core.Time;

/// <summary>
/// Ruby's `Time`: an instant with a UTC offset (a <see cref="DateTimeOffset"/>, 100 ns resolution) plus Ruby's UTC
/// flag (`Time.now.utc?` is false even when the local offset is 0). Arithmetic with <see cref="Duration"/> and seconds,
/// `strftime` that writes into spans without allocating (<see cref="TryStrftime"/>).
/// </summary>
public readonly struct Time : IEquatable<Time>, IComparable<Time>, ISpanFormattable
{
    readonly DateTimeOffset value;

    /// <summary>Ruby `utc?`.</summary>
    public bool IsUtc { get; }

    Time(DateTimeOffset v, bool utc) { value = v; IsUtc = utc; }

    /// <summary>Wraps a <see cref="DateTimeOffset"/> (UTC flag set when its offset is 0 and <paramref name="utc"/>).</summary>
    public static Time FromDateTimeOffset(DateTimeOffset v, bool utc = false) => new(v, utc && v.Offset == TimeSpan.Zero);

    /// <summary>Ruby `Time.now` (local time).</summary>
    public static Time Now => new(DateTimeOffset.Now, false);

    /// <summary>The current time in UTC.</summary>
    public static Time UtcNow => new(DateTimeOffset.UtcNow, true);

    /// <summary>Ruby `Time.at(seconds)` (local time).</summary>
    [Pure] public static Time At(long seconds) => new(DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(), false);

    /// <summary>Ruby `Time.at(float_seconds)` (local time, 100 ns resolution).</summary>
    [Pure] public static Time At(double seconds) => new(DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)).ToLocalTime(), false);

    /// <summary>Ruby `Time.utc(year, month, day, hour, min, sec)` (also `Time.gm`).</summary>
    [Pure]
    public static Time Utc(int year, int month = 1, int day = 1, int hour = 0, int minute = 0, double second = 0)
        => new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).AddTicks((long)Math.Round(second * TimeSpan.TicksPerSecond)), true);

    /// <summary>Ruby `Time.local(...)` / `Time.new(...)`: in the local zone.</summary>
    public static Time Local(int year, int month = 1, int day = 1, int hour = 0, int minute = 0, double second = 0)
    {
        var dt = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local).AddTicks((long)Math.Round(second * TimeSpan.TicksPerSecond));
        return new(new DateTimeOffset(dt), false);
    }

    /// <summary>Ruby `Time.new(year, ..., utc_offset)` with a fixed offset in seconds.</summary>
    [Pure]
    public static Time WithOffset(int year, int month, int day, int hour, int minute, double second, int utcOffsetSeconds)
        => new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromSeconds(utcOffsetSeconds)).AddTicks((long)Math.Round(second * TimeSpan.TicksPerSecond)), false);

    /// <summary>Ruby `Time.parse`: ISO 8601 and the common RFC forms (invariant culture); a missing offset means local.</summary>
    public static Time Parse(string text)
    {
        var v = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal);
        bool utc = v.Offset == TimeSpan.Zero && (text.EndsWith('Z') || text.EndsWith("UTC", StringComparison.Ordinal) || text.EndsWith("GMT", StringComparison.Ordinal));
        return new(v, utc);
    }

    /// <summary>Like <see cref="Parse"/>; false when the text is not a time.</summary>
    public static bool TryParse(string text, out Time result)
    {
        try { result = Parse(text); return true; }
        catch (FormatException) { result = default; return false; }
    }

    /// <summary>Ruby `Time.measure { }` (ActiveSupport `Benchmark.realtime`): monotonic wall time of <paramref name="body"/>.</summary>
    public static Duration Measure(Action body)
    {
        long t0 = Stopwatch.GetTimestamp();
        body();
        return Clock.Since(t0);
    }

#pragma warning disable CS1591 // Ruby accessors are self-describing
    public int Year => value.Year;
    public int Month => value.Month;
    public int Day => value.Day;
    public int Hour => value.Hour;
    public int Minute => value.Minute;
    public int Second => value.Second;
    /// <summary>Microseconds within the second (Ruby `usec`).</summary>
    public int Usec => (int)(value.Ticks % TimeSpan.TicksPerSecond / 10);
    /// <summary>Nanoseconds within the second (Ruby `nsec`; 100 ns resolution).</summary>
    public int Nsec => (int)(value.Ticks % TimeSpan.TicksPerSecond * 100);
    /// <summary>Day of the week, Sunday = 0 (Ruby `wday`).</summary>
    public int Wday => (int)value.DayOfWeek;
    /// <summary>Day of the year, 1..366 (Ruby `yday`).</summary>
    public int Yday => value.DayOfYear;
    /// <summary>Offset from UTC in seconds (Ruby `utc_offset`).</summary>
    public int UtcOffset => (int)value.Offset.TotalSeconds;
    /// <summary>Ruby `zone`: "UTC" for UTC times, otherwise the offset as +hh:mm (.NET has no zone abbreviations).</summary>
    public string Zone => IsUtc ? "UTC" : value.Offset.ToString((value.Offset < TimeSpan.Zero ? "\\-" : "\\+") + "hh\\:mm", CultureInfo.InvariantCulture);

    public static Time operator +(Time t, Duration d) => new(t.value.AddTicks(d.Nanoseconds / 100), t.IsUtc);
    public static Time operator -(Time t, Duration d) => new(t.value.AddTicks(-d.Nanoseconds / 100), t.IsUtc);
    public static Time operator +(Time t, double seconds) => new(t.value.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)), t.IsUtc);
    public static Time operator -(Time t, double seconds) => t + -seconds;
    public static Duration operator -(Time a, Time b) => new(checked((a.value.UtcTicks - b.value.UtcTicks) * 100));
    public static bool operator <(Time a, Time b) => a.value < b.value;
    public static bool operator >(Time a, Time b) => a.value > b.value;
    public static bool operator <=(Time a, Time b) => a.value <= b.value;
    public static bool operator >=(Time a, Time b) => a.value >= b.value;
    public static bool operator ==(Time a, Time b) => a.value == b.value;
    public static bool operator !=(Time a, Time b) => a.value != b.value;
#pragma warning restore CS1591

    /// <summary>`t + d` as a method (for callers without operator support).</summary>
    [Pure] public Time Add(Duration d) => this + d;

    /// <summary>`t - d` as a method.</summary>
    [Pure] public Time Subtract(Duration d) => this - d;

    /// <summary>`a - b` as a method: the duration between two times.</summary>
    [Pure] public Duration Since(Time earlier) => this - earlier;

    /// <summary>Ruby `to_i`: whole seconds since the Unix epoch.</summary>
    [Pure] public long ToI() => value.ToUnixTimeSeconds();

    /// <summary>Ruby `to_f`: seconds since the Unix epoch.</summary>
    [Pure] public double ToF() => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    /// <summary>Milliseconds since the Unix epoch.</summary>
    [Pure] public long ToUnixMilliseconds() => value.ToUnixTimeMilliseconds();

    /// <summary>Ruby `utc` / `getutc`: the same instant in UTC.</summary>
    [Pure] public Time ToUtc() => new(value.ToUniversalTime(), true);

    /// <summary>Ruby `localtime` / `getlocal`: the same instant in the local zone.</summary>
    public Time ToLocal() => new(value.ToLocalTime(), false);

    /// <summary>The underlying <see cref="DateTimeOffset"/>.</summary>
    public DateTimeOffset ToDateTimeOffset() => value;

    /// <summary>Ruby `strftime(fmt)`: allocates only the result.</summary>
    public string Strftime(string format)
    {
        Span<char> buf = stackalloc char[256];
        if (Core.Time.Strftime.TryFormat(buf, out int n, format, this)) return new string(buf[..n]);
        var big = new char[Math.Max(4096, format.Length * 64)];
        if (Core.Time.Strftime.TryFormat(big, out n, format, this)) return new string(big, 0, n);
        throw new FormatException("strftime result too long");
    }

    /// <summary>Ruby `strftime` into <paramref name="destination"/>: 0 bytes allocated.</summary>
    public bool TryStrftime(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format)
        => Core.Time.Strftime.TryFormat(destination, out charsWritten, format, this);

    /// <summary>Ruby `strftime` as UTF-8 into <paramref name="destination"/>: 0 bytes allocated.</summary>
    public bool TryStrftimeUtf8(Span<byte> destination, out int bytesWritten, ReadOnlySpan<char> format)
    {
        Span<char> buf = stackalloc char[512];
        if (Core.Time.Strftime.TryFormat(buf, out int n, format, this)) return Encoding.UTF8.TryGetBytes(buf[..n], destination, out bytesWritten);
        bytesWritten = 0;
        return false;
    }

    /// <summary>Ruby `iso8601` / `xmlschema` (fractional digits optional).</summary>
    public string Iso8601(int fractionDigits = 0)
        => Strftime(fractionDigits <= 0 ? (IsUtc ? "%Y-%m-%dT%H:%M:%SZ" : "%Y-%m-%dT%H:%M:%S%:z")
            : (IsUtc ? $"%Y-%m-%dT%H:%M:%S.%{fractionDigits}NZ" : $"%Y-%m-%dT%H:%M:%S.%{fractionDigits}N%:z"));

    /// <summary>Ruby `to_s`: <c>2026-01-02 03:04:05 +0900</c>, or <c>... UTC</c>.</summary>
    public override string ToString() => Strftime(IsUtc ? "%Y-%m-%d %H:%M:%S UTC" : "%Y-%m-%d %H:%M:%S %z");

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => format is null ? ToString() : Strftime(format);

    /// <summary>Formats with a strftime <paramref name="format"/> (Ruby `to_s` when empty).</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => Core.Time.Strftime.TryFormat(destination, out charsWritten, format.IsEmpty ? (IsUtc ? "%Y-%m-%d %H:%M:%S UTC" : "%Y-%m-%d %H:%M:%S %z") : format, this);

    /// <inheritdoc/>
    public bool Equals(Time other) => value == other.value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Time t && Equals(t);

    /// <inheritdoc/>
    public override int GetHashCode() => value.UtcTicks.GetHashCode();

    /// <inheritdoc/>
    public int CompareTo(Time other) => value.CompareTo(other.value);
}

/// <summary>Monotonic clock (Ruby `Process.clock_gettime(Process::CLOCK_MONOTONIC)`), immune to wall-clock changes.</summary>
public static class Clock
{
    /// <summary>A monotonic timestamp (<see cref="Stopwatch"/> ticks).</summary>
    public static long Timestamp() => Stopwatch.GetTimestamp();

    /// <summary>Monotonic time in seconds since an arbitrary origin.</summary>
    public static double Monotonic() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Time elapsed since <paramref name="timestamp"/> (from <see cref="Timestamp"/>).</summary>
    public static Duration Since(long timestamp) => FromTicks(Stopwatch.GetTimestamp() - timestamp);

    /// <summary>Stopwatch ticks to a duration.</summary>
    public static Duration FromTicks(long ticks) => new((long)(ticks * (1e9 / Stopwatch.Frequency)));
}
