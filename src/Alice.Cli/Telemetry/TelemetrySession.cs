using System.Diagnostics;
using Spectre.Console;

namespace Alice.Cli.Telemetry;

/// <summary>Per-process collector wired into App. Collects in memory; writes or sends only when consent is granted.</summary>
public static class TelemetrySession
{
    static readonly Stopwatch Clock = new();
    static Exception? crash;
    static readonly List<string> codes = [];
    static readonly Dictionary<string, long> phases = [];

    static Func<string, string?> Env => Environment.GetEnvironmentVariable;

    public static string Dir => ConsentStore.ConfigDir(Env);

    public static bool Interactive =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Interactive;

    public static void Start()
    {
        Clock.Restart();
        crash = null;
        lock (codes) codes.Clear();
        lock (phases) phases.Clear();
    }

    public static void Crash(Exception e) => crash = e;

    public static void Codes(IEnumerable<string> c) { lock (codes) codes.AddRange(c); }

    /// <summary>Compile-phase timing; sent only when the user opted into perf counters (`alice telemetry perf on`).</summary>
    public static void Phase(string name, long ms) { lock (phases) phases[name] = phases.GetValueOrDefault(name) + ms; }

    public static TelemetryEvent Build(IReadOnlyList<string> args, int exitCode, Consent c)
    {
        Dictionary<string, int> counts; Dictionary<string, long>? p;
        lock (codes) counts = Sanitizer.Codes(codes);
        lock (phases) p = c.Perf && phases.Count > 0 ? new(phases) : null;
        return TelemetryEvent.Create(TelemetryEvent.CommandName(args), App.Version, c.InstallId, exitCode, Clock.Elapsed, crash, counts, p);
    }

    /// <summary>Asks the one-paragraph question (TTY only, never in CI); the answer is stored and not asked again.</summary>
    public static ConsentState MaybeAsk() => !TelemetryPolicy.Available ? ConsentState.Unset :
        TelemetryPolicy.AfterUpdate(Dir, Env, Interactive, () =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(TelemetryPolicy.Prompt)}[/]");
            return AnsiConsole.Confirm("Send anonymous usage data?", false);
        });

    /// <summary>End of a command: enqueue and start a short, bounded flush. Never throws, never changes the exit code.</summary>
    public static void Finish(IReadOnlyList<string> args, int exitCode)
    {
        if (!TelemetryPolicy.Available) return;
        try
        {
            var c = ConsentStore.Load(Dir);
            if (!TelemetryPolicy.Enabled(c, Env)) return;
            var q = new TelemetryQueue(Dir);
            q.Append(Build(args, exitCode, c).ToJson());
            if (TelemetrySender.ResolveEndpoint(c, Env) is { } endpoint)
                TelemetrySender.FlushAsync(q, endpoint).Wait(TimeSpan.FromMilliseconds(300));
        }
        catch (Exception) { }
    }
}
