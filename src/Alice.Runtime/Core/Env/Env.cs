using System.Collections;
using System.Runtime.InteropServices;
using Alice.Runtime;

namespace Alice.Core.Env;

/// <summary>
/// The process environment with Ruby's `ENV` surface (`ENV["X"]`, `ENV.fetch("X", "d")`, `ENV.key?`) over
/// <see cref="Environment"/>: reads and writes go to the live environment (no snapshot). The Prelude `ENV` object
/// keeps its Hash shape; this is the API it lowers to and what C# calls.
/// </summary>
public static class Env
{
    /// <summary>Ruby `ENV[name]`: the value or null.</summary>
    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>Ruby `ENV[name] = value`; null deletes.</summary>
    public static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    /// <summary>Ruby `ENV.fetch(name)`: throws <see cref="KeyNotFoundException"/> when unset.</summary>
    public static string Fetch(string name) => Get(name) ?? throw new KeyNotFoundException($"key not found: \"{name}\"");

    /// <summary>Ruby `ENV.fetch(name, default)`.</summary>
    public static string Fetch(string name, string fallback) => Get(name) ?? fallback;

    /// <summary>Ruby `ENV.key?(name)`.</summary>
    public static bool HasKey(string name) => Get(name) is not null;

    /// <summary>Ruby `ENV.delete(name)`: removes and returns the old value.</summary>
    public static string? Delete(string name)
    {
        var old = Get(name);
        Set(name, null);
        return old;
    }

    /// <summary>An integer variable, or <paramref name="fallback"/> when unset or not a number.</summary>
    public static long FetchInt(string name, long fallback) => long.TryParse(Get(name), out long v) ? v : fallback;

    /// <summary>A Boolean variable: 1/true/yes/on (any case) are true; unset gives <paramref name="fallback"/>.</summary>
    public static bool FetchBool(string name, bool fallback = false) => Get(name) is { } s
        ? s.Equals("1", StringComparison.Ordinal) || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase)
        : fallback;

    /// <summary>Ruby `ENV.to_h`: a snapshot of every variable.</summary>
    public static Dictionary<string, string> ToH()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) d[(string)e.Key] = (string?)e.Value ?? "";
        return d;
    }

    /// <summary>Ruby `ENV.keys`.</summary>
    public static string[] Keys() => [.. ToH().Keys];
}

/// <summary>Ruby `ARGV`: the program arguments (the Prelude `ARGV` array, set by the generated entry point).</summary>
public static class Argv
{
    /// <summary>The arguments after the program name.</summary>
    public static string[] Values => Prelude.ARGV;

    /// <summary>Number of arguments.</summary>
    public static int Count => Prelude.ARGV.Length;

    /// <summary>Argument <paramref name="i"/>, or null when absent (Ruby `ARGV[i]`).</summary>
    public static string? At(int i) => (uint)i < (uint)Prelude.ARGV.Length ? Prelude.ARGV[i] : null;

    /// <summary>Ruby `$PROGRAM_NAME` / `$0`.</summary>
    public static string ProgramName => Environment.GetCommandLineArgs() is { Length: > 0 } a ? a[0] : "";
}

/// <summary>Host facts (Ruby `RUBY_PLATFORM`, `Etc.nprocessors`) from <see cref="RuntimeInformation"/>.</summary>
public static class Platform
{
    /// <summary>"linux", "windows", "macos", "freebsd", "wasi", "browser" or "unknown".</summary>
    public static string Os =>
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsFreeBSD() ? "freebsd" : OperatingSystem.IsWasi() ? "wasi" : OperatingSystem.IsBrowser() ? "browser" : "unknown";

    /// <summary>"x64", "arm64", "x86", "arm", "wasm", ... (lowercase <see cref="Architecture"/>).</summary>
    public static string Arch => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>Logical processors available to the process (Ruby `Etc.nprocessors`).</summary>
    public static int Cores => Environment.ProcessorCount;

    /// <summary>True on Linux.</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>True on Windows.</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>True on macOS.</summary>
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>The runtime description, e.g. ".NET 10.0.1".</summary>
    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>The host name.</summary>
    public static string Hostname => Environment.MachineName;

    /// <summary>The current process id (Ruby `Process.pid`, `$$`).</summary>
    public static int Pid => Environment.ProcessId;

    /// <summary>Ruby `RUBY_PLATFORM`-style triple, e.g. "x64-linux".</summary>
    public static string Triple => Arch + "-" + Os;
}
