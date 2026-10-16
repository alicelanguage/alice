using System.Collections;
using System.Text.RegularExpressions;

namespace Alice.Runtime;

/// <summary>A failed expectation; Expected/Actual feed the runner's diff.</summary>
public sealed class ExpectationFailed(string message, string? expected = null, string? actual = null) : Exception(message)
{
    public string? Expected { get; } = expected;
    public string? Actual { get; } = actual;
}

/// <summary>A matcher: tests a value and explains failure both ways (`to` / `not_to`).</summary>
public class Matcher(string description, Func<object?, bool> test, string? expected = null)
{
    public string Description { get; } = description;
    public string? Expected { get; } = expected;
    public virtual bool Matches(object? actual) => test(actual);
    public Matcher and(Matcher other) => new($"{Description} and {other.Description}", a => Matches(a) && other.Matches(a));
    public Matcher or(Matcher other) => new($"{Description} or {other.Description}", a => Matches(a) || other.Matches(a));
}

public sealed class Expectation(object? actual, Action? block = null)
{
    public void to(Matcher m)
    {
        if (block != null && m is RaiseMatcher rm) { rm.Run(block, true); return; }
        object? a = block != null ? Capture(block) : actual;
        if (!m.Matches(a))
            throw new ExpectationFailed($"expected {Prelude.inspect(a)} to {m.Description}", m.Expected, Prelude.inspect(a));
    }

    public void not_to(Matcher m)
    {
        if (block != null && m is RaiseMatcher rm) { rm.Run(block, false); return; }
        object? a = block != null ? Capture(block) : actual;
        if (m.Matches(a)) throw new ExpectationFailed($"expected {Prelude.inspect(a)} not to {m.Description}");
    }

    public void to_not(Matcher m) => not_to(m);

    static object? Capture(Action b) { b(); return null; }
}

/// <summary>`expect { ... }.to raise_error(ArgumentException)` (subclasses match; optional message substring).</summary>
public sealed class RaiseMatcher(Type? type, string? message) : Matcher(
    "raise " + (type?.Name ?? "an error") + (message != null ? $" with message containing {Prelude.inspect(message)}" : ""), _ => false)
{
    public void Run(Action block, bool positive)
    {
        Exception? got = null;
        try { block(); }
        catch (ExpectationFailed) { throw; }
        catch (Exception e) { got = e is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : e; }
        bool ok = got != null && (type == null || type.IsInstanceOfType(got)) && (message == null || got.Message.Contains(message));
        if (positive && !ok)
            throw new ExpectationFailed(got == null ? $"expected block to {Description}, but nothing was raised"
                : $"expected block to {Description}, got {got.GetType().Name}: {got.Message}");
        if (!positive && got != null && (type == null || type.IsInstanceOfType(got)))
            throw new ExpectationFailed($"expected block not to {Description}, got {got.GetType().Name}: {got.Message}");
    }
}

public sealed class WithinMatcher(double delta)
{
    public Matcher of(double expected) =>
        new($"be within {delta} of {expected}", a => a != null && Math.Abs(Convert.ToDouble(a) - expected) <= delta, Prelude.inspect(expected));
}

/// <summary>expect + matchers (RSpec names; `be_gt` etc. stand in for `be > x`).</summary>
public static partial class Prelude
{
    public static Expectation expect(object? actual) => new(actual);
    /// <summary>`expect { ... }` (the compiler routes block-only `expect` here, so `expect(nil)` stays unambiguous).</summary>
    public static Expectation expect_block(Action block) => new(null, block);

    /// <summary>Equality with numeric widening (1 == 1L == 1.0) and sequence equality for collections.</summary>
    public static bool Same(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Equals(b)) return true;
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDecimal(a) == Convert.ToDecimal(b);
        if (a is IEnumerable ea && b is IEnumerable eb && a is not string && b is not string)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = eb.Cast<object?>().ToList();
            return la.Count == lb.Count && la.Zip(lb).All(p => Same(p.First, p.Second));
        }
        return false;
    }

    static bool IsNumber(object o) => o is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    static int Compare(object? a, object? b) =>
        a is not null && b is not null && IsNumber(a) && IsNumber(b) ? Convert.ToDouble(a).CompareTo(Convert.ToDouble(b))
        : Comparer<object?>.Default.Compare(a, b);

    public static Matcher eq(object? expected) => new($"eq {inspect(expected)}", a => Same(a, expected), inspect(expected));
    public static Matcher be(object? expected) => new($"be {inspect(expected)}", a => ReferenceEquals(a, expected) || (a is ValueType or string && Same(a, expected)), inspect(expected));
    public static Matcher be_nil() => new("be nil", a => a == null, "nil");
    public static Matcher be_true() => new("be true", a => a is true, "true");
    public static Matcher be_false() => new("be false", a => a is false, "false");
    public static Matcher be_truthy() => new("be truthy", a => a is not (null or false));
    public static Matcher be_falsy() => new("be falsy", a => a is null or false);
    public static Matcher be_empty() => new("be empty", a => a is string s ? s.Length == 0 : a is IEnumerable e && !e.Cast<object>().Any());
    public static Matcher be_gt(object? x) => new($"be > {inspect(x)}", a => Compare(a, x) > 0);
    public static Matcher be_ge(object? x) => new($"be >= {inspect(x)}", a => Compare(a, x) >= 0);
    public static Matcher be_lt(object? x) => new($"be < {inspect(x)}", a => Compare(a, x) < 0);
    public static Matcher be_le(object? x) => new($"be <= {inspect(x)}", a => Compare(a, x) <= 0);
    public static Matcher be_between(object? lo, object? hi) => new($"be between {inspect(lo)} and {inspect(hi)}", a => Compare(a, lo) >= 0 && Compare(a, hi) <= 0);
    public static WithinMatcher be_within(double delta) => new(delta);
    public static Matcher be_a(Type t) => new($"be a {t.Name}", a => t.IsInstanceOfType(a));
    public static Matcher be_kind_of(Type t) => be_a(t);
    public static Matcher be_instance_of(Type t) => new($"be an instance of {t.Name}", a => a?.GetType() == t);
    public static Matcher have_size(int n) => new($"have size {n}", a => (a is string s ? s.Length : a is IEnumerable e ? e.Cast<object>().Count() : -1) == n, n.ToString());
    public static Matcher start_with(string prefix) => new($"start with {inspect(prefix)}", a => a is string s && s.StartsWith(prefix, StringComparison.Ordinal));
    public static Matcher end_with(string suffix) => new($"end with {inspect(suffix)}", a => a is string s && s.EndsWith(suffix, StringComparison.Ordinal));
    public static Matcher match(string pattern) => new($"match /{pattern}/", a => a is string s && Regex.IsMatch(s, pattern));
    public static Matcher satisfy(Func<object?, bool> pred) => new("satisfy the block", pred);
    public static Matcher respond_to(string name) => new($"respond to {name}", a => respond_to_q(a, name));

    public static Matcher include(params object?[] items) => new($"include {string.Join(", ", items.Select(inspect))}", a => a switch
    {
        string s => items.All(i => i is string x ? s.Contains(x) : i is char c && s.Contains(c)),
        IDictionary d => items.All(i => i != null && d.Contains(i)),
        IEnumerable e => items.All(i => e.Cast<object?>().Any(x => Same(x, i))),
        _ => false,
    });

    public static Matcher contain_exactly(params object?[] items) => new($"contain exactly {string.Join(", ", items.Select(inspect))}", a =>
    {
        if (a is not IEnumerable e) return false;
        var rest = e.Cast<object?>().ToList();
        foreach (var i in items)
        {
            int k = rest.FindIndex(x => Same(x, i));
            if (k < 0) return false;
            rest.RemoveAt(k);
        }
        return rest.Count == 0;
    }, "[" + string.Join(", ", items.Select(inspect)) + "]");

    public static Matcher all(Matcher m) => new($"all {m.Description}", a => a is IEnumerable e && e.Cast<object?>().All(m.Matches));

    public static RaiseMatcher raise_error() => new(null, null);
    public static RaiseMatcher raise_error(Type type) => new(type, null);
    public static RaiseMatcher raise_error(Type type, string message) => new(type, message);
    public static RaiseMatcher raise_error(string message) => new(null, message);
}
