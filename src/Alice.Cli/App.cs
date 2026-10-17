using Alice.Cli.Commands;
using Alice.Cli.Localization;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;

namespace Alice.Cli;

/// <summary>Composition root: global flags, command dispatch, help.</summary>
public static class App
{
    /// <summary>Informational version without build metadata, e.g. `0.10.1` or `0.10.2-nightly.20261016`.</summary>
    public static string Version { get; } =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.10.0").Split('+')[0];

    static readonly (string Name, string Key)[] Commands =
    [
        ("new", "cmd.new"), ("build", "cmd.build"), ("run", "cmd.run"), ("check", "cmd.check"), ("test", "cmd.test"),
        ("add", "cmd.add"), ("remove", "cmd.remove"), ("search", "cmd.search"), ("list", "cmd.list"), ("install", "cmd.install"), ("repl", "cmd.repl"), ("cache", "cmd.cache"), ("clean", "cmd.clean"), ("emit", "cmd.emit"), ("fmt", "cmd.fmt"), ("update", "cmd.update"), ("version", "cmd.version"),
        ("lsp", "cmd.lsp"), ("toolchain", "cmd.toolchain"), ("self", "cmd.self"), ("doctor", "cmd.doctor"), ("completions", "cmd.completions"),
    ];

    public static IEnumerable<string> CommandNames => Commands.Select(c => c.Name);

    /// <summary>`--verbose`: show internal stack traces (otherwise every failure is a one-line error + hint).</summary>
    public static bool Verbose;

    public static int Run(string[] argv)
    {
        var args = new List<string>(argv);
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--lang" && i + 1 < args.Count) { Strings.Use(args[i + 1]); args.RemoveRange(i, 2); i--; }
            else if (args[i] is "-q" or "--quiet") { Ui.Ui.Quiet = true; args.RemoveAt(i); i--; }
            else if (args[i] == "--no-color") { AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors; args.RemoveAt(i); i--; }
            else if (args[i] == "--verbose") { Verbose = true; args.RemoveAt(i); i--; }
            else if (args[i] == "--") break;
        }
        try
        {
            if (ToolchainCommands.Dispatch(args, argv) is int forwarded) return forwarded;
        }
        catch (UsageException e) { Ui.Ui.Error(e.Message, e.Hint); return e.ExitCode; }
        var refresh = args.FirstOrDefault() is "self" or "completions" or "lsp" ? null : Toolchains.UpdateNotifier.RefreshInBackground(Toolchains.ToolchainHome.Current());
        int code = Dispatch(args);
        Notify(args, refresh);
        return code;
    }

    /// <summary>After an interactive command, a one-line notice when a newer stable release is known.</summary>
    static void Notify(List<string> args, Task? refresh)
    {
        refresh?.Wait(TimeSpan.FromMilliseconds(150));
        if (Ui.Ui.Quiet || Console.IsErrorRedirected || args.FirstOrDefault() is "self" or "completions" or "repl" or "lsp" || args.Count == 0) return;
        if (!Toolchains.UpdateNotifier.Enabled(Environment.GetEnvironmentVariable)) return;
        if (Toolchains.UpdateNotifier.Notice(Toolchains.ToolchainHome.Current(), Version) is { } n)
            AnsiConsole.MarkupLine($"\n[yellow]↑[/] [grey]{Markup.Escape(n)}[/]");
    }

    static int Dispatch(List<string> args)
    {
        if (args.Count == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected) return Repl.ReplCommand.Run([]);
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help") { Help(); return args.Count == 0 ? 2 : 0; }
        if (args[0] is "-V" or "--version") args[0] = "version";
        var rest = args.Skip(1).ToList();
        try
        {
            return args[0] switch
            {
                "lsp" => RunLsp(rest),
                "new" => ProjectCommands.New(rest),
                "build" => CompileCommands.Build(rest),
                "run" => CompileCommands.RunCmd(rest),
                "check" => CompileCommands.Check(rest),
                "emit" => CompileCommands.Emit(rest),
                "gen" => CompileCommands.Gen(rest),
                "test" => TestCommand.Run(rest),
                "fmt" => FormatCommand.Run(rest),
                "add" => ProjectCommands.Add(rest),
                "remove" => PackageCommands.Remove(rest),
                "search" => PackageCommands.Search(rest),
                "list" => PackageCommands.List(rest),
                "install" => PackageCommands.Install(rest),
                "repl" => Repl.ReplCommand.Run(rest),
                "cache" => PackageCommands.Cache(rest),
                "clean" => ProjectCommands.Clean(rest),
                "update" => ProjectCommands.Update(rest),
                "version" => VersionCommand.Run(),
                "toolchain" => ToolchainCommands.Run(rest),
                "self" => SelfCommands.Run(rest),
                "upgrade" => SelfCommands.Run(["update", .. rest]),
                "doctor" => DoctorCommand.Run(),
                "completions" => CompletionsCommand.Run(rest),
                _ when args[0].EndsWith(".al") => CompileCommands.RunCmd(args),
                _ => Unknown(args[0]),
            };
        }
        catch (UsageException e)
        {
            Ui.Ui.Error(e.Message, e.Hint);
            return e.ExitCode;
        }
        catch (Toolchains.ToolchainException e)
        {
            Ui.Ui.Error(e.Message, e.Hint);
            return 1;
        }
        catch (Alice.Compiler.CompileException e)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics);
            return 1;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // last resort: a bug or environment failure is still one readable line, the trace only on request
            Ui.Ui.Error(T("internal", e.Message), Verbose ? e.ToString() : T("hint.verbose"));
            return 70;
        }
    }

    static int RunLsp(List<string> args)
    {
        if (args.Any(a => a != "--stdio")) throw UsageException.Usage("alice lsp [--stdio]");
        using var server = new Lsp.LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
        return server.Run();
    }

    static int Unknown(string cmd)
    {
        var near = UsageException.Closest(cmd, Commands.Select(c => c.Name).Append("repl"));
        throw new UsageException(T("unknown.command", cmd), near != null ? T("did.you.mean", near) : "alice --help");
    }

    static void Help()
    {
        AnsiConsole.MarkupLine($"[bold magenta]{Markup.Escape(T("tagline", Version))}[/]\n");
        AnsiConsole.MarkupLine($"[bold]{T("usage")}:[/] alice [green]<command>[/] [grey][[options]][/] [blue][[files...]][/]\n");
        var table = new Table().NoBorder().HideHeaders().AddColumn("").AddColumn("");
        foreach (var (name, key) in Commands) table.AddRow($"  [green]{name}[/]", Markup.Escape(T(key)));
        AnsiConsole.MarkupLine($"[bold]{T("commands")}:[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[grey]--lang en|pt|es   -q/--quiet   --no-color   alice <file.al> = alice run <file.al>   alice +VERSION <command>[/]");
    }
}
