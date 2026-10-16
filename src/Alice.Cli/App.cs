using Alice.Cli.Commands;
using Alice.Cli.Localization;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;

namespace Alice.Cli;

/// <summary>Composition root: global flags, command dispatch, help.</summary>
public static class App
{
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.10.0";

    static readonly (string Name, string Key)[] Commands =
    [
        ("new", "cmd.new"), ("build", "cmd.build"), ("run", "cmd.run"), ("check", "cmd.check"), ("test", "cmd.test"),
        ("add", "cmd.add"), ("clean", "cmd.clean"), ("emit", "cmd.emit"), ("fmt", "cmd.fmt"), ("sln", "cmd.sln"), ("version", "cmd.version"),
    ];

    public static int Run(string[] argv)
    {
        var args = new List<string>(argv);
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--lang" && i + 1 < args.Count) { Strings.Use(args[i + 1]); args.RemoveRange(i, 2); i--; }
            else if (args[i] is "-q" or "--quiet") { Ui.Ui.Quiet = true; args.RemoveAt(i); i--; }
            else if (args[i] == "--no-color") { AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors; args.RemoveAt(i); i--; }
            else if (args[i] == "--") break;
        }
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help") { Help(); return args.Count == 0 ? 2 : 0; }
        if (args[0] is "-V" or "--version") args[0] = "version";
        var rest = args.Skip(1).ToList();
        try
        {
            return args[0] switch
            {
                "new" => ProjectCommands.New(rest),
                "build" => CompileCommands.Build(rest),
                "run" => CompileCommands.RunCmd(rest),
                "check" => CompileCommands.Check(rest),
                "emit" => CompileCommands.Emit(rest),
                "gen" => CompileCommands.Gen(rest),
                "test" => TestCommand.Run(rest),
                "fmt" => FormatCommand.Run(rest),
                "add" => ProjectCommands.Add(rest),
                "clean" => ProjectCommands.Clean(rest),
                "sln" => ProjectCommands.Sln(rest),
                "version" => VersionCommand.Run(),
                _ when args[0].EndsWith(".al") => CompileCommands.RunCmd(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Alice.Compiler.CompileException e)
        {
            Ui.DiagnosticRenderer.Render(e.Diagnostics);
            return 1;
        }
    }

    static int Unknown(string cmd)
    {
        Ui.Ui.Error(T("unknown.command", cmd));
        Help();
        return 2;
    }

    static void Help()
    {
        AnsiConsole.MarkupLine($"[bold magenta]{Markup.Escape(T("tagline", Version))}[/]\n");
        AnsiConsole.MarkupLine($"[bold]{T("usage")}:[/] alice [green]<command>[/] [grey][[options]][/] [blue][[files...]][/]\n");
        var table = new Table().NoBorder().HideHeaders().AddColumn("").AddColumn("");
        foreach (var (name, key) in Commands) table.AddRow($"  [green]{name}[/]", Markup.Escape(T(key)));
        AnsiConsole.MarkupLine($"[bold]{T("commands")}:[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[grey]--lang en|pt|es   -q/--quiet   --no-color   alice <file.al> = alice run <file.al>[/]");
    }
}
