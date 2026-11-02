using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Object protocol for every type: `hash`, `eql?`, `equal?`, and Tuple `size`/`to_a`. These take a bare `T` receiver,
/// so they live outside Prelude: there they would claim the name on every .NET type and hide its own member
/// (the binder routes a name Prelude accepts for a receiver to Prelude).
/// </summary>
public static class ObjectProtocol
{
    /// <summary>`x.hash`: the value's hash code (the type's `hash`/GetHashCode; no box for value types).</summary>
    [Pure] public static int hash<T>(this T v) => v is null ? 0 : EqualityComparer<T>.Default.GetHashCode(v);

    /// <summary>`a.eql?(b)`: value equality (the type's `==`/Equals), no numeric conversion.</summary>
    [Pure] public static bool eql_q<T>(this T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

    /// <summary>`a.equal?(b)`: the same object (identity).</summary>
    [Pure] public static bool equal_q<T>(this T a, T b) where T : class => ReferenceEquals(a, b);

    /// <summary>`t.size` of a Tuple / NamedTuple: its arity.</summary>
    [Pure] public static int size<T>(this T t) where T : struct, ITuple => t.Length;

    /// <summary>`t.to_a`: the elements as an array.</summary>
    [Pure]
    public static object?[] to_a<T>(this T t) where T : struct, ITuple
    {
        var r = new object?[t.Length];
        for (int i = 0; i < r.Length; i++) r[i] = t[i];
        return r;
    }
}

/// <summary>`p a, b`, `pp`, `p nil`; `ENV`, `STDIN`/`STDOUT`/`STDERR`, `at_exit`.</summary>
public static partial class Prelude
{
    /// <summary>`p nil` (and any value whose type C# cannot infer): prints `nil`.</summary>
    [Ambient] public static object? p(object? v) { Console.Out.WriteLine(inspect(v)); return v; }

    /// <summary>`p a, b, ...`: each value inspected on its own line; returns them as an array (Ruby).</summary>
    [Ambient] public static object?[] p(object? a, object? b, params object?[] rest)
    {
        var all = new object?[rest.Length + 2];
        all[0] = a;
        all[1] = b;
        rest.CopyTo(all, 2);
        foreach (var v in all) Console.Out.WriteLine(inspect(v));
        return all;
    }

    /// <summary>`pp x`: like `p` (values print on one line).</summary>
    public static T pp<T>(T v) => p(v);
    public static object? pp(object? v) => p(v);
    public static object?[] pp(object? a, object? b, params object?[] rest) => p(a, b, rest);

    /// <summary>`ENV["HOME"]`, `ENV.fetch("X", "d")`, `ENV["X"] = "v"`: the process environment as a Hash(String, String).</summary>
    public static EnvHash ENV => EnvHolder.Value;

    public static IOStream STDOUT => StdHolder.Out;
    public static IOStream STDERR => StdHolder.Err;
    public static IOStream STDIN => StdHolder.In;

    static class EnvHolder { public static readonly EnvHash Value = EnvHash.Snapshot(); }

    static class StdHolder
    {
        public static readonly IOStream Out = new(() => Console.Out, null);
        public static readonly IOStream Err = new(() => Console.Error, null);
        public static readonly IOStream In = new(null, () => Console.In);
    }

    static class ExitHolder
    {
        public static readonly List<Action> Handlers = [];
        public static bool Hooked;
    }

    /// <summary>`at_exit { ... }`: runs when the program exits (normally or via `exit`), last registered first.</summary>
    public static void at_exit(Action f)
    {
        lock (ExitHolder.Handlers)
        {
            ExitHolder.Handlers.Add(f);
            if (!ExitHolder.Hooked) { ExitHolder.Hooked = true; AppDomain.CurrentDomain.ProcessExit += (_, _) => run_at_exit(); }
        }
    }

    /// <summary>Runs (and clears) the `at_exit` handlers, last registered first. Called on process exit.</summary>
    public static void run_at_exit()
    {
        while (true)
        {
            Action f;
            lock (ExitHolder.Handlers)
            {
                if (ExitHolder.Handlers.Count == 0) break;
                f = ExitHolder.Handlers[^1];
                ExitHolder.Handlers.RemoveAt(ExitHolder.Handlers.Count - 1);
            }
            f();
        }
        Console.Out.Flush();
    }
}

/// <summary>
/// `ENV`: a Hash(String, String) of the environment at first use. Missing keys raise (Crystal; use `fetch(k, d)` or
/// `key?`); assignment through `ENV[k] = v` also sets the process variable, `delete` (Prelude) only the hash.
/// </summary>
public sealed class EnvHash : Dictionary<string, string>
{
    EnvHash() : base(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { }

    internal static EnvHash Snapshot()
    {
        var e = new EnvHash();
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            if (kv.Key is string k && kv.Value is string v) e.Add(k, v);
        return e;
    }

    public new string this[string key]
    {
        get => base[key];
        set { base[key] = value; Environment.SetEnvironmentVariable(key, value); }
    }
}

/// <summary>`STDOUT`/`STDERR`/`STDIN`: Ruby IO methods over the current Console streams (redirects are honored).</summary>
public sealed class IOStream
{
    readonly Func<TextWriter>? w;
    readonly Func<TextReader>? r;

    internal IOStream(Func<TextWriter>? w, Func<TextReader>? r) { this.w = w; this.r = r; }

    TextWriter W => w?.Invoke() ?? throw new IOException("not opened for writing");
    TextReader R => r?.Invoke() ?? throw new IOException("not opened for reading");

    public void puts() => W.WriteLine();
    public void puts<T>(T v) => W.WriteLine(Prelude.str(v));
    public void puts(params object?[] vs) { foreach (var v in vs) W.WriteLine(Prelude.str(v)); }
    public void print<T>(T v) => W.Write(Prelude.str(v));
    public void print(params object?[] vs) { foreach (var v in vs) W.Write(Prelude.str(v)); }
    public void write<T>(T v) => W.Write(Prelude.str(v));
    public void flush() => W.Flush();
    public string? gets() => R.ReadLine();
    public string read() => R.ReadToEnd();
    public string[] read_lines()
    {
        var lines = new List<string>();
        while (R.ReadLine() is string l) lines.Add(l);
        return lines.ToArray();
    }
    public void each_line(Action<string> f) { while (R.ReadLine() is string l) f(l); }
}
