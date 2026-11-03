using Alice.Core.Time;

namespace Alice.Runtime;

/// <summary>
/// ActiveSupport-style duration literals (`5.minutes`, `1.5.seconds`, `2.hours.ago`) lowering to
/// <see cref="Duration"/> (Alice.Core.Time). Pure: no state.
/// </summary>
public static partial class Prelude
{
#pragma warning disable CS1591 // unit names are self-describing
    [Pure] public static Duration nanoseconds(this long n) => Duration.FromNanoseconds(n);
    [Pure] public static Duration nanoseconds(this int n) => Duration.FromNanoseconds(n);
    [Pure] public static Duration microseconds(this int n) => Duration.FromMicroseconds(n);
    [Pure] public static Duration microseconds(this double n) => Duration.FromMicroseconds(n);
    [Pure] public static Duration milliseconds(this int n) => Duration.FromMilliseconds(n);
    [Pure] public static Duration milliseconds(this long n) => Duration.FromMilliseconds(n);
    [Pure] public static Duration milliseconds(this double n) => Duration.FromMilliseconds(n);
    [Pure] public static Duration seconds(this int n) => Duration.FromSeconds(n);
    [Pure] public static Duration seconds(this long n) => Duration.FromSeconds(n);
    [Pure] public static Duration seconds(this double n) => Duration.FromSeconds(n);
    [Pure] public static Duration minutes(this int n) => Duration.FromMinutes(n);
    [Pure] public static Duration minutes(this long n) => Duration.FromMinutes(n);
    [Pure] public static Duration minutes(this double n) => Duration.FromMinutes(n);
    [Pure] public static Duration hours(this int n) => Duration.FromHours(n);
    [Pure] public static Duration hours(this long n) => Duration.FromHours(n);
    [Pure] public static Duration hours(this double n) => Duration.FromHours(n);
    [Pure] public static Duration days(this int n) => Duration.FromDays(n);
    [Pure] public static Duration days(this long n) => Duration.FromDays(n);
    [Pure] public static Duration days(this double n) => Duration.FromDays(n);
    [Pure] public static Duration weeks(this int n) => Duration.FromWeeks(n);
    [Pure] public static Duration weeks(this double n) => Duration.FromWeeks(n);
#pragma warning restore CS1591
}
