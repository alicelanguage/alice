using System.Diagnostics;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;

namespace Alice.Cli.Ui;

/// <summary>Cargo-style status lines: right-aligned green verb, then detail.</summary>
public static class Ui
{
    public static bool Quiet;

    public static void Status(string verb, string detail, string color = "green")
    {
        if (Quiet) return;
        AnsiConsole.MarkupLine($"[bold {color}]{Markup.Escape(verb),12}[/] {Markup.Escape(detail)}");
    }

    public static void Error(string message, string? hint)
    {
        Error(message);
        if (hint != null) AnsiConsole.MarkupLine($"  [cyan]hint[/]: {Markup.Escape(hint)}");
    }

    public static void Error(string message)
    {
        AnsiConsole.MarkupLine($"[bold red]{T("error")}[/][bold]: {Markup.Escape(message)}[/]");
    }

    public static string Elapsed(Stopwatch sw) =>
        sw.Elapsed.TotalSeconds >= 1 ? $"{sw.Elapsed.TotalSeconds:0.00}s" : $"{sw.Elapsed.TotalMilliseconds:0}ms";

    /// <summary>Runs work under an animated spinner (skipped when output is redirected).</summary>
    public static T Spin<T>(string text, Func<T> work)
    {
        if (Quiet || !AnsiConsole.Profile.Capabilities.Interactive) return work();
        T result = default!;
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("cyan"))
            .Start($"[cyan]{Markup.Escape(text)}[/]", _ => { result = work(); });
        return result;
    }
}
