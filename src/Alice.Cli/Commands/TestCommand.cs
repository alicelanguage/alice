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
        var refs = new List<string>();
        if (File.Exists(Alice.Cli.Projects.Manifest.FileName))
        {
            // a project: build it, then compile the tests against its resolved references (NuGet, project refs)
            var proj = Alice.Cli.Projects.Manifest.Sync(".");
            if (CompileCommands.BuildProject(proj, false) != 0) return 1;
            var refsFile = Directory.GetFiles("obj", "refs.txt", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            // package/project assemblies only: the framework itself comes from the running runtime
            if (refsFile != null)
                refs = File.ReadAllLines(refsFile).Where(l => l.Length > 0 && !l.Replace('\\', '/').Contains("/packs/") && !Path.GetFileName(l).StartsWith("Alice.Runtime")).ToList();
            lib = Directory.GetFiles(".", "*.al", SearchOption.AllDirectories)
                .Where(f => !IsUnder(f, "test") && !IsUnder(f, "bin") && !IsUnder(f, "obj") && !IsUnder(f, ".alice")).ToList();
        }
        int passed = 0, failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var t in tests)
        {
            var files = AliceCompiler.LoadWithRequires(lib.Append(t));
            byte[] image;
            try
            {
                image = UiH.Spin($"{T("compiling")} {t}…", () => AliceCompiler.Emit(AliceCompiler.TranspileAll(files, refs), "tests", exe: false, refs));
            }
            catch (CompileException e)
            {
                Ui.DiagnosticRenderer.Render(e.Diagnostics);
                return 1;
            }
            foreach (var r in refs)
                System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
                    Path.GetFileNameWithoutExtension(r) == name.Name ? ctx.LoadFromAssemblyPath(Path.GetFullPath(r)) : null;
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
        static bool IsUnder(string f, string dir) => f.Replace('\\', '/').Contains("/" + dir + "/");
        string verdict = failed == 0 ? $"[green]{T("test.ok")}[/]" : $"[red]{T("test.failed")}[/]";
        AnsiConsole.MarkupLine("\n" + T("test.result", verdict, passed, failed, UiH.Elapsed(sw)));
        return failed == 0 ? 0 : 1;
    }
}
