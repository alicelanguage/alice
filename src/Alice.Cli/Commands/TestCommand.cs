using System.Diagnostics;
using System.Reflection;
using Alice.Compiler;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>`alice test`: compiles src/ + each test/*.al and runs every `def test_*`, cargo-test style.</summary>
public static class TestCommand
{
    public static int Run(List<string> a)
    {
        var tests = a.Where(x => x.EndsWith(".al")).ToList();
        if (tests.Count == 0 && Directory.Exists("test")) tests = Directory.GetFiles("test", "*.al").OrderBy(x => x).ToList();
        if (tests.Count == 0) throw new CompileException(T("no.files"));
        var lib = Directory.Exists("src") ? Directory.GetFiles("src", "*.al", SearchOption.AllDirectories).ToList() : new List<string>();
        int passed = 0, failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var t in tests)
        {
            var files = lib.Append(t).Select(p => new SourceFile(Path.GetFullPath(p), File.ReadAllText(p))).ToList();
            byte[] image;
            try
            {
                image = UiH.Spin($"{T("compiling")} {t}…", () => AliceCompiler.Emit(AliceCompiler.TranspileAll(files), "tests", exe: false));
            }
            catch (CompileException e)
            {
                Ui.DiagnosticRenderer.Render(e.Diagnostics);
                return 1;
            }
            var prog = AliceCompiler.Load(image).GetType("Program")!;
            var cases = prog.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.StartsWith("test_") && m.GetParameters().Length == 0).ToList();
            AnsiConsole.MarkupLine($"\n[bold]{Markup.Escape(t)}[/]: {T("test.running", cases.Count)}");
            foreach (var m in cases)
            {
                try
                {
                    m.Invoke(null, null);
                    passed++;
                    AnsiConsole.MarkupLine($"  test {Markup.Escape(m.Name)} ... [green]{T("test.ok")}[/]");
                }
                catch (TargetInvocationException e)
                {
                    failed++;
                    var inner = e.InnerException ?? e;
                    AnsiConsole.MarkupLine($"  test {Markup.Escape(m.Name)} ... [bold red]{T("test.failed")}[/]");
                    AnsiConsole.MarkupLine($"    [red]{Markup.Escape(inner.Message)}[/]");
                }
            }
        }
        string verdict = failed == 0 ? $"[green]{T("test.ok")}[/]" : $"[red]{T("test.failed")}[/]";
        AnsiConsole.MarkupLine("\n" + T("test.result", verdict, passed, failed, UiH.Elapsed(sw)));
        return failed == 0 ? 0 : 1;
    }
}
