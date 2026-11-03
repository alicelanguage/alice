using System.Globalization;
using Alice.Runtime;

namespace Alice.Core.Time;

/// <summary>
/// A span of time in nanoseconds (Ruby/ActiveSupport `5.minutes`, Go's `time.Duration`): a 64-bit count, so about
/// +-292 years. Converts implicitly to <see cref="TimeSpan"/> (100 ns resolution).
/// </summary>
public readonly struct Duration : IEquatable<Duration>, IComparable<Duration>, ISpanFormattable
{
    /// <summary>Nanoseconds.</summary>
    public long Nanoseconds { get; }

    /// <summary>A duration of <paramref name="nanoseconds"/>.</summary>
    public Duration(long nanoseconds) => Nanoseconds = nanoseconds;

    /// <summary>Zero.</summary>
    public static Duration Zero => default;

#pragma warning disable CS1591 // unit constructors are self-describing
    [Pure] public static Duration FromNanoseconds(long n) => new(n);
    [Pure] public static Duration FromMicroseconds(double n) => new(checked((long)Math.Round(n * 1e3)));
    [Pure] public static Duration FromMilliseconds(double n) => new(checked((long)Math.Round(n * 1e6)));
    [Pure] public static Duration FromSeconds(double n) => new(checked((long)Math.Round(n * 1e9)));
    [Pure] public static Duration FromMinutes(double n) => new(checked((long)Math.Round(n * 60e9)));
    [Pure] public static Duration FromHours(double n) => new(checked((long)Math.Round(n * 3600e9)));
    [Pure] public static Duration FromDays(double n) => new(checked((long)Math.Round(n * 86400e9)));
    [Pure] public static Duration FromWeeks(double n) => new(checked((long)Math.Round(n * 604800e9)));
    [Pure] public static Duration FromTimeSpan(TimeSpan t) => new(checked(t.Ticks * 100));

    public double TotalSeconds => Nanoseconds / 1e9;
    public double TotalMilliseconds => Nanoseconds / 1e6;
    public double TotalMicroseconds => Nanoseconds / 1e3;
    public double TotalMinutes => Nanoseconds / 60e9;
    public double TotalHours => Nanoseconds / 3600e9;
    public double TotalDays => Nanoseconds / 86400e9;

    public static Duration operator +(Duration a, Duration b) => new(checked(a.Nanoseconds + b.Nanoseconds));
    public static Duration operator -(Duration a, Duration b) => new(checked(a.Nanoseconds - b.Nanoseconds));
    public static Duration operator -(Duration a) => new(checked(-a.Nanoseconds));
    public static Duration operator *(Duration a, double k) => new(checked((long)Math.Round(a.Nanoseconds * k)));
    public static Duration operator *(double k, Duration a) => a * k;
    public static Duration operator /(Duration a, double k) => new(checked((long)Math.Round(a.Nanoseconds / k)));
    public static double operator /(Duration a, Duration b) => (double)a.Nanoseconds / b.Nanoseconds;
    public static bool operator <(Duration a, Duration b) => a.Nanoseconds < b.Nanoseconds;
    public static bool operator >(Duration a, Duration b) => a.Nanoseconds > b.Nanoseconds;
    public static bool operator <=(Duration a, Duration b) => a.Nanoseconds <= b.Nanoseconds;
    public static bool operator >=(Duration a, Duration b) => a.Nanoseconds >= b.Nanoseconds;
    public static bool operator ==(Duration a, Duration b) => a.Nanoseconds == b.Nanoseconds;
    public static bool operator !=(Duration a, Duration b) => a.Nanoseconds != b.Nanoseconds;
    public static implicit operator TimeSpan(Duration d) => TimeSpan.FromTicks(d.Nanoseconds / 100);
#pragma warning restore CS1591

    /// <summary>The <see cref="TimeSpan"/> (truncated to 100 ns).</summary>
    public TimeSpan ToTimeSpan() => this;

    /// <summary>ActiveSupport `5.minutes.ago`: now minus this duration.</summary>
    public Time Ago() => Time.Now - this;

    /// <summary>ActiveSupport `5.minutes.from_now`.</summary>
    public Time FromNow() => Time.Now + this;

    /// <summary>Ruby `to_i`: whole seconds.</summary>
    public long ToI() => Nanoseconds / 1_000_000_000;

    /// <summary>Ruby `to_f`: seconds.</summary>
    public double ToF() => TotalSeconds;

    /// <inheritdoc/>
    public bool Equals(Duration other) => Nanoseconds == other.Nanoseconds;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Duration d && Equals(d);

    /// <inheritdoc/>
    public override int GetHashCode() => Nanoseconds.GetHashCode();

    /// <inheritdoc/>
    public int CompareTo(Duration other) => Nanoseconds.CompareTo(other.Nanoseconds);

    /// <summary>Go-style text: <c>1h2m3.5s</c>, <c>250ms</c>, <c>0s</c>.</summary>
    public override string ToString()
    {
        Span<char> buf = stackalloc char[48];
        TryFormat(buf, out int n);
        return new string(buf[..n]);
    }

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Writes the Go-style text (no allocation).</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        var inv = CultureInfo.InvariantCulture;
        charsWritten = 0;
        long ns = Nanoseconds;
        int w = 0;
        if (ns == 0) return Put("0s", destination, ref w, out charsWritten);
        if (ns < 0) { if (destination.Length < 1) return false; destination[w++] = '-'; ns = ns == long.MinValue ? long.MaxValue : -ns; }
        if (ns < 1_000_000_000)
        {
            (double v, string unit) = ns < 1_000 ? (ns, "ns") : ns < 1_000_000 ? (ns / 1e3, "us") : (ns / 1e6, "ms");
            if (!v.TryFormat(destination[w..], out int k, "0.######", inv)) return false;
            w += k;
            return Put(unit, destination, ref w, out charsWritten);
        }
        long h = ns / 3_600_000_000_000, m = ns / 60_000_000_000 % 60;
        double s = ns % 60_000_000_000 / 1e9;
        int k2;
        if (h > 0) { if (!h.TryFormat(destination[w..], out k2, default, inv)) return false; w += k2; if (!Put("h", destination, ref w, out _)) return false; }
        if (h > 0 || m > 0) { if (!m.TryFormat(destination[w..], out k2, default, inv)) return false; w += k2; if (!Put("m", destination, ref w, out _)) return false; }
        if (!s.TryFormat(destination[w..], out k2, "0.#########", inv)) return false;
        w += k2;
        return Put("s", destination, ref w, out charsWritten);
    }

    static bool Put(ReadOnlySpan<char> s, Span<char> d, ref int w, out int written)
    {
        written = 0;
        if (!s.TryCopyTo(d[w..])) return false;
        w += s.Length;
        written = w;
        return true;
    }
}
