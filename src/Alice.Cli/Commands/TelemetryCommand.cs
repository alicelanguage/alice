using Alice.Cli.Telemetry;
using Spectre.Console;

namespace Alice.Cli.Commands;

/// <summary>`alice telemetry on|off|status|show|reset|perf on|off` (ADR 0031).</summary>
public static class TelemetryCommand
{
    public static int Run(List<string> a)
    {
        var dir = TelemetrySession.Dir;
        var c = ConsentStore.Load(dir);
        Func<string, string?> env = Environment.GetEnvironmentVariable;
        if (!TelemetryPolicy.Available)
        {
            Ui.Ui.Status("Telemetry", "disabled in this release (nothing is asked, recorded or sent)");
            return 0;
        }
        switch (a.FirstOrDefault())
        {
            case "on": ConsentStore.Save(dir, c.With(ConsentState.Granted)); Ui.Ui.Status("Telemetry", "on (thank you)"); break;
            case "off": ConsentStore.Save(dir, c.With(ConsentState.Denied)); Ui.Ui.Status("Telemetry", "off"); break;
            case "reset": ConsentStore.Save(dir, c.ResetId()); Ui.Ui.Status("Telemetry", "new install id"); break;
            case "perf" when a.Count > 1 && a[1] is "on" or "off":
                ConsentStore.Save(dir, c with { Perf = a[1] == "on" }); Ui.Ui.Status("Perf counters", a[1]); break;
            case "status" or null:
                var s = c.PolicyVersion == TelemetryPolicy.PolicyVersion ? c.State : ConsentState.Unset;
                AnsiConsole.MarkupLine($"  consent    [bold]{s.ToString().ToLowerInvariant()}[/]");
                AnsiConsole.MarkupLine($"  sending    [bold]{(TelemetryPolicy.Enabled(c, env) ? "yes" : "no")}[/]" +
                    (TelemetryPolicy.EnvOptOut(env) ? " [grey](ALICE_TELEMETRY_OPTOUT/DO_NOT_TRACK set)[/]" : ""));
                AnsiConsole.MarkupLine($"  endpoint   {Markup.Escape(TelemetrySender.ResolveEndpoint(c, env) ?? "none configured (nothing is sent)")}");
                AnsiConsole.MarkupLine($"  perf       {(c.Perf ? "on" : "off")}");
                AnsiConsole.MarkupLine($"  config     {Markup.Escape(Path.Combine(dir, ConsentStore.FileName))}");
                break;
            case "show":
                // exactly the JSON one invocation would add to the queue (this one, as an example)
                Console.WriteLine(TelemetrySession.Build(["telemetry", "show"], 0, c).ToJson());
                break;
            default:
                throw new UsageException("usage: alice telemetry <on|off|status|show|reset|perf on|off>", "alice telemetry status");
        }
        return 0;
    }
}
