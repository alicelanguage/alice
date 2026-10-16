using System.Diagnostics;
using Alice.Cli.Commands;
using Spectre.Console;

namespace Alice.Cli.Repl;

/// <summary>
/// `alice` / `alice repl`: Julia-style REPL. Modes (typed on an empty line, Backspace returns):
/// `?` help, `;` shell, `]` packages. Commands: :help :reset :load FILE :save FILE :emit EXPR :time EXPR :quit.
/// </summary>
public static class ReplCommand
{
    enum Mode { Alice, Help, Shell, Pkg }

    static readonly string HistoryFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alice_history");

    public static int Run(List<string> a)
    {
        var refs = ProjectRefs();
        var session = new ReplSession(refs);
        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (!interactive) return Piped(session);

        Banner();
        var ed = new LineEditor(session.Complete);
        try { ed.History.AddRange(File.ReadAllLines(HistoryFile).TakeLast(1000)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var mode = Mode.Alice;
        while (true)
        {
            var (prompt, width) = Prompt(mode);
            ed.Highlight = mode is Mode.Alice or Mode.Help;
            var (sig, line) = ed.Read(prompt, width, mode == Mode.Alice ? "?;]" : "");
            if (sig == LineEditor.Signal.Eof) break;
            if (sig == LineEditor.Signal.ModeKey)
            {
                mode = line switch { "?" => Mode.Help, ";" => Mode.Shell, _ => Mode.Pkg };
                Console.Write("\r\x1b[K");
                continue;
            }
            if (sig == LineEditor.Signal.LeaveMode) { if (mode != Mode.Alice) { mode = Mode.Alice; Console.Write("\r\x1b[K"); } continue; }
            if (line.Trim().Length == 0) continue;
            var input = line;
            if (mode == Mode.Alice && !line.TrimStart().StartsWith(':'))
                while (!ReplSession.IsComplete(input))
                {
                    var (s2, more) = ed.Read("\u001b[90m     ⋮ \u001b[0m", 7, "", ReplSession.Indent(input));
                    if (s2 == LineEditor.Signal.Eof) break;
                    if (s2 == LineEditor.Signal.LeaveMode) { input += "\n"; continue; }
                    input += "\n" + more;
                }
            ed.History.Add(input.Replace("\n", "\n"));
            try { File.AppendAllText(HistoryFile, input.Replace('\n', ' ') + "\n"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            switch (mode)
            {
                case Mode.Help: AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ReplHelp.Lookup(input))}[/]"); break;
                case Mode.Shell: Shell(input); break;
                case Mode.Pkg: Pkg(input); break;
                default:
                    if (input.TrimStart().StartsWith(':')) { if (!Command(session, input.Trim())) return 0; }
                    else Show(session.Eval(input, Console.Out));
                    break;
            }
            if (mode != Mode.Alice && sig == LineEditor.Signal.Line) { }
        }
        return 0;
    }

    /// <summary>Non-interactive (stdin piped): evaluate complete inputs in order, print values like the REPL.</summary>
    static int Piped(ReplSession session)
    {
        string buf = "";
        int failures = 0;
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            buf = buf.Length == 0 ? line : buf + "\n" + line;
            if (buf.Trim().Length == 0) { buf = ""; continue; }
            if (!ReplSession.IsComplete(buf)) continue;
            if (buf.TrimStart().StartsWith(':')) { if (!Command(session, buf.Trim())) break; }
            else
            {
                var r = session.Eval(buf, Console.Out);
                if (r.Error != null) { failures++; Console.Error.WriteLine(r.Error); }
                else if (r.Value != null) Console.WriteLine(r.Value);
            }
            buf = "";
        }
        return failures == 0 ? 0 : 1;
    }

    static (string, int) Prompt(Mode m) => m switch
    {
        Mode.Help => ("\u001b[1;33mhelp?> \u001b[0m", 7),
        Mode.Shell => ("\u001b[1;31mshell> \u001b[0m", 7),
        Mode.Pkg => PkgPrompt(),
        _ => ("\u001b[1;35malice> \u001b[0m", 7),
    };

    static (string, int) PkgPrompt()
    {
        string name = "(" + (File.Exists(Projects.Manifest.FileName) ? Path.GetFileName(Environment.CurrentDirectory) : "global") + ") pkg> ";
        return ("\u001b[1;34m" + name + "\u001b[0m", name.Length);
    }

    static void Banner()
    {
        string channel = Toolchains.ToolchainVersion.TryParse(App.Version)?.Channel ?? "stable";
        AnsiConsole.MarkupLine("[bold magenta]    ╱|    [/]  [bold]Alice[/] [grey]" + Markup.Escape(App.Version) + " (" + channel + ") · .NET " + Environment.Version + "[/]");
        AnsiConsole.MarkupLine("[bold magenta]   ( ?)   [/]  [grey]curiouser and faster — Ruby to write, C to run, all of .NET[/]");
        AnsiConsole.MarkupLine("[bold magenta]   ╱ ◇    [/]  [grey]Documentation: [/][link]https://github.com/alicelanguage/alice[/]");
        AnsiConsole.MarkupLine("[bold magenta]  ◇  ◇    [/]  [grey]? help · ; shell · ]] packages · :help · Tab completes · Ctrl-R searches · Ctrl-D exits[/]\n");
    }

    static void Show(ReplResult r)
    {
        if (r.Error != null) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(r.Error)}[/]"); return; }
        if (r.Value != null) AnsiConsole.MarkupLine($"[bold]{Markup.Escape(r.Value)}[/] [grey]:: {Markup.Escape(Compiler.Diagnostics.AliceNames.Type(r.ValueType ?? ""))}[/]");
    }

    /// <summary>Returns false to quit.</summary>
    static bool Command(ReplSession s, string cmd)
    {
        string arg = cmd.Contains(' ') ? cmd[(cmd.IndexOf(' ') + 1)..].Trim() : "";
        switch (cmd.Split(' ')[0])
        {
            case ":q" or ":quit" or ":exit": return false;
            case ":reset": s.Reset(); AnsiConsole.MarkupLine("[grey]session cleared[/]"); break;
            case ":load":
                if (!File.Exists(arg)) { AnsiConsole.MarkupLine($"[red]file not found: {Markup.Escape(arg)}[/]"); break; }
                Show(s.Eval(File.ReadAllText(arg), Console.Out));
                break;
            case ":save":
                File.WriteAllText(arg.Length > 0 ? arg : "session.al", string.Join("\n", s.Inputs.Select(Clean)) + "\n");
                AnsiConsole.MarkupLine($"[grey]saved {s.Inputs.Count} inputs to {Markup.Escape(arg.Length > 0 ? arg : "session.al")}[/]");
                break;
            case ":emit":
                try { Console.WriteLine(Compiler.AliceCompiler.Transpile(arg)); }
                catch (Compiler.CompileException e) { Ui.DiagnosticRenderer.Render(e.Diagnostics); }
                break;
            case ":time":
                {
                    var r = s.Eval(arg, Console.Out);
                    Show(r);
                    AnsiConsole.MarkupLine($"[grey]{r.Elapsed.TotalMilliseconds:0.0} ms (compile + run)[/]");
                    break;
                }
            case ":type":
                {
                    var r = s.Eval(arg, null);
                    if (r.Error != null) AnsiConsole.MarkupLine($"[red]{Markup.Escape(r.Error)}[/]");
                    else AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(Compiler.Diagnostics.AliceNames.Type(r.ValueType ?? "Nil"))}[/]");
                    break;
                }
            case ":history":
                for (int i = 0; i < s.Inputs.Count; i++)
                    AnsiConsole.MarkupLine($"[grey]{i + 1,4}[/]  " + Markup.Escape(Clean(s.Inputs[i]).Replace("\n", "\n      ")));
                break;
            case ":clear": Console.Write("\u001b[2J\u001b[H"); break;
            case ":help" or ":h":
                AnsiConsole.MarkupLine("""
                    [bold]modes[/] (type on an empty line; Backspace returns)
                      [yellow]?[/]  help: ?puts  ?Math.Sqrt  ?String  ?def
                      [red];[/]  shell: ;ls -la
                      [blue]]][/]  packages: ]]add Newtonsoft.Json  ]]remove X  ]]list  ]]search json
                    [bold]commands[/]
                      :reset  :load FILE  :save FILE  :emit CODE (show generated C#)  :time EXPR
                      :type EXPR (static type)  :history  :clear  :quit
                    [bold]values[/]
                      the last value is [green]ans[/]; definitions persist; earlier inputs re-run silently
                    [bold]keys[/]  ↑↓ history (prefix search)  → accept hint  Ctrl-R search  Tab complete
                          Alt/Ctrl ←→ words  Ctrl-W/K/U kill  Ctrl-C clear  Ctrl-L clear screen  Ctrl-D exit
                    """);
                break;
            default: AnsiConsole.MarkupLine($"[red]unknown command {Markup.Escape(cmd)}[/] (:help)"); break;
        }
        return true;
    }

    static string Clean(string chunk) =>
        System.Text.RegularExpressions.Regex.Replace(chunk, @"\n?ReplHost\.show\([^)]*\)", "").Replace("__v", "v");

    static void Shell(string cmd)
    {
        bool win = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo(win ? "cmd.exe" : "/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add(win ? "/c" : "-c");
        psi.ArgumentList.Add(cmd);
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }

    static void Pkg(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return;
        try
        {
            int rc = parts[0] switch
            {
                "add" => ProjectCommands.Add(parts.Skip(1).ToList()),
                "rm" or "remove" => PackageCommands.Remove(parts.Skip(1).ToList()),
                "st" or "status" or "list" => PackageCommands.List([]),
                "search" => PackageCommands.Search(parts.Skip(1).ToList()),
                "install" or "instantiate" => PackageCommands.Install(parts.Skip(1).ToList()),
                _ => throw new UsageException($"unknown pkg command '{parts[0]}'", "add, remove, list, search, install"),
            };
            if (rc == 0 && parts[0] is "add" or "rm" or "remove") AnsiConsole.MarkupLine("[grey]restart the REPL (or :reset) to use new packages[/]");
        }
        catch (UsageException e) { Ui.Ui.Error(e.Message, e.Hint); }
    }

    /// <summary>In a project, compile REPL inputs against its resolved package references.</summary>
    static List<string> ProjectRefs()
    {
        if (!File.Exists(Projects.Manifest.FileName) || !Directory.Exists("obj")) return new();
        var refsFile = Directory.GetFiles("obj", "refs.txt", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return refsFile == null ? new() : File.ReadAllLines(refsFile).Where(l => l.Length > 0 && !l.Replace('\\', '/').Contains("/packs/") && !Path.GetFileName(l).StartsWith("Alice.Runtime")).ToList();
    }
}
