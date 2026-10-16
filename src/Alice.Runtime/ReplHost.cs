namespace Alice.Runtime;

/// <summary>REPL support called from generated session code: replayed inputs run muted; `show` records the result.</summary>
public static class ReplHost
{
    public static TextWriter? Out;
    public static string? Shown;
    public static string? ShownType;

    public static void mute() => Console.SetOut(TextWriter.Null);

    public static void unmute()
    {
        if (Out != null) Console.SetOut(Out);
        Shown = null;
        ShownType = null;
    }

    public static T show<T>(T value)
    {
        Shown = Prelude.inspect(value);
        ShownType = value?.GetType().Name ?? typeof(T).Name;
        return value;
    }
}
