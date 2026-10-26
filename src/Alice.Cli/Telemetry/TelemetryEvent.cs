using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Alice.Cli.Telemetry;

/// <summary>One CLI invocation. Every field is listed in docs/telemetry.md; nothing else is ever sent.</summary>
public sealed record TelemetryEvent(
    int Schema, string InstallId, string Command, string AliceVersion, string Os, string Arch, string Dotnet,
    int ExitCode, long DurationMs, string? CrashType, string? CrashStack, Dictionary<string, int> ErrorCodes, Dictionary<string, long>? Phases)
{
    static readonly HashSet<string> Known =
        ["new", "build", "run", "check", "test", "add", "remove", "search", "list", "install", "pack", "publish", "repl", "cache", "clean",
         "emit", "explain", "prove", "guide", "agents", "fmt", "update", "version", "lsp", "toolchain", "self", "doctor", "completions",
         "gen", "upgrade", "telemetry", "help"];

    static readonly HashSet<string> Subs =
        ["update", "upgrade", "check", "uninstall", "install", "list", "default", "use", "pin", "which", "prune", "clean",
         "on", "off", "status", "show", "reset", "perf"];

    /// <summary>The command name only, from a fixed list (never arguments, paths or typos); `alice file.al` is `run`.</summary>
    public static string CommandName(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return "repl";
        var a = args[0];
        if (a.EndsWith(".al")) return "run";
        if (a is "-V" or "--version") return "version";
        if (a is "-h" or "--help") return "help";
        if (!Known.Contains(a)) return "other";
        if (a is "self" or "toolchain" or "cache" or "telemetry" && args.Count > 1 && Subs.Contains(args[1])) return a + " " + args[1];
        return a;
    }

    public static TelemetryEvent Create(string command, string version, string installId, int exitCode, TimeSpan duration,
        Exception? crash, Dictionary<string, int> codes, Dictionary<string, long>? phases) =>
        new(TelemetryPolicy.PolicyVersion, installId, command, version,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), Environment.Version.ToString(),
            exitCode, (long)duration.TotalMilliseconds,
            crash == null ? null : Sanitizer.Type(crash), crash == null ? null : Sanitizer.Stack(crash.ToString()), codes, phases);

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("install_id", InstallId);
            w.WriteString("command", Command);
            w.WriteString("alice_version", AliceVersion);
            w.WriteString("os", Os);
            w.WriteString("arch", Arch);
            w.WriteString("dotnet", Dotnet);
            w.WriteNumber("exit_code", ExitCode);
            w.WriteNumber("duration_ms", DurationMs);
            if (CrashType != null)
            {
                w.WriteStartObject("crash");
                w.WriteString("type", CrashType);
                w.WriteString("stack", CrashStack);
                w.WriteEndObject();
            }
            w.WriteStartObject("error_codes");
            foreach (var (k, v) in ErrorCodes) w.WriteNumber(k, v);
            w.WriteEndObject();
            if (Phases != null)
            {
                w.WriteStartObject("phases_ms");
                foreach (var (k, v) in Phases) w.WriteNumber(k, v);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
