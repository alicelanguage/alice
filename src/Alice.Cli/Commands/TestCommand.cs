using System.Diagnostics;
using System.Reflection;
using Alice.Compiler;
using Alice.Runtime;
using Spectre.Console;
using static Alice.Cli.Localization.Strings;
using UiH = Alice.Cli.Ui.Ui;

namespace Alice.Cli.Commands;

/// <summary>
/// `alice test [files|file.al:LINE] [-e TEXT] [--seed N] [--fail-fast] [--format doc|progress|junit] [--profile]`:
/// compiles src/ + each test/*.al (also *_spec.al, *_test.al) and runs specs, @[Test]/@[Cases] and test_* via TestRunner.
/// </summary>
public static class TestCommand
{
    public static int Run(List<string> a)
    {
        var opts = new RunOptions();
        string format = "doc";
        bool profile = false;
        var tests = new List<string>();
        for (int i = 0; i < a.Count; i++)
        {
            string x = a[i];
            if (x is "-e" or "--example" && i + 1 < a.Count) opts.Filters.Add(a[++i]);
            else if (x == "--seed" && i + 1 < a.Count) opts.Seed = int.Parse(a[++i]);
            else if (x == "--order" && i + 1 < a.Count && a[++i] == "random") opts.Seed ??= Random.Shared.Next(1, 100000);
            else if (x == "--fail-fast") opts.FailFast = true;
            else if (x == "--format" && i + 1 < a.Count) format = a[++i];
            else if (x == "--profile") profile = true;
            else if (x.Contains(".al:") && int.TryParse(x[(x.LastIndexOf(':') + 1)..], out int ln)) { tests.Add(x[..x.LastIndexOf(':')]); opts.Lines.Add(ln); }
            else if (x.EndsWith(".al")) tests.Add(x);
        }
        foreach (var dir in new[] { "test", "spec" })
            if (tests.Count == 0 && Directory.Exists(dir)) tests = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories).OrderBy(x => x).ToList();
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
        var all = new List<TestResult>();
        var sw = Stopwatch.StartNew();
        var reporter = new Ui.TestReporter(format);
        if (opts.Seed is int seed0) AnsiConsole.MarkupLine($"[grey]randomized with seed {seed0}[/]");
        foreach (var t in tests)
        {
            var files = AliceCompiler.LoadWithRequires(lib.Where(l => Path.GetFullPath(l) != Path.GetFullPath(t)).Append(t))
                .Select(f => Path.GetFullPath(f.Path) == Path.GetFullPath(t) ? f : f with { LibraryOnly = true }).ToList();
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
            reporter.File(t);
            all.AddRange(TestRunner.Run(AliceCompiler.Load(image), opts, reporter.Result));
            if (opts.FailFast && all.Any(r => r.Status == TestStatus.Failed)) break;
        }
        reporter.Finish(all, sw.Elapsed, profile, opts.Seed);
        return all.Any(r => r.Status == TestStatus.Failed) ? 1 : 0;
        static bool IsUnder(string f, string dir) => f.Replace('\\', '/').Contains("/" + dir + "/");

    }
}
