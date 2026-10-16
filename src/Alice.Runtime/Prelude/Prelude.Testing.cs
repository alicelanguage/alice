namespace Alice.Runtime;

public sealed class AssertionError(string message) : Exception(message);

/// <summary>Assertions used by `alice test` (test_* functions).</summary>
public static partial class Prelude
{
    public static void assert(bool condition, string message = "assertion failed")
    {
        if (!condition) throw new AssertionError(message);
    }

    public static void assert_equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionError($"expected {inspect(expected)}, got {inspect(actual)}");
    }
}
