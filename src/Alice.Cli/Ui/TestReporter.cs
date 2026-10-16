using System.Xml.Linq;
using Alice.Runtime;
using Spectre.Console;

namespace Alice.Cli.Ui;

/// <summary>`alice test` output: documentation tree (default), progress dots, or JUnit XML for CI.</summary>
public sealed class TestReporter(string format)
{
    List<string> lastPath = new();
    string file = "";

    public void File(string path)
    {
        file = path;
        lastPath = new();
        if (format == "doc") AnsiConsole.MarkupLine($"\n[bold]{Markup.Escape(path)}[/]");
    }

    public void Result(TestResult r)
    {
        if (format == "progress")
        {
            AnsiConsole.Markup(r.Status switch { TestStatus.Passed => "[green].[/]", TestStatus.Failed => "[red]F[/]", TestStatus.Pending => "[yellow]*[/]", _ => "[grey]s[/]" });
            return;
        }
        if (format != "doc") return;
        int common = 0;
        while (common < lastPath.Count && common < r.Path.Count && lastPath[common] == r.Path[common]) common++;
        for (int i = common; i < r.Path.Count; i++) AnsiConsole.MarkupLine($"{new string(' ', 2 * i + 2)}[bold]{Markup.Escape(r.Path[i])}[/]");
        lastPath = r.Path.ToList();
        string pad = new(' ', 2 * r.Path.Count + 2);
        string ms = r.Duration.TotalMilliseconds >= 1 ? $" [grey]({r.Duration.TotalMilliseconds:0.#} ms)[/]" : "";
        AnsiConsole.MarkupLine(r.Status switch
        {
            TestStatus.Passed => $"{pad}[green]✓[/] {Markup.Escape(r.Name)}{ms}",
            TestStatus.Failed => $"{pad}[red]✗ {Markup.Escape(r.Name)}[/]{ms}",
            TestStatus.Pending => $"{pad}[yellow]… {Markup.Escape(r.Name)}[/] [grey]({Markup.Escape(r.Message ?? "")})[/]",
            _ => $"{pad}[grey]- {Markup.Escape(r.Name)} ({Markup.Escape(r.Message ?? "skipped")})[/]",
        });
    }

    public void Finish(List<TestResult> all, TimeSpan elapsed, bool profile, int? seed)
    {
        if (format == "junit") { Junit(all, elapsed); return; }
        if (format == "progress") AnsiConsole.WriteLine();
        var failed = all.Where(r => r.Status == TestStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Failures:[/]");
            int n = 1;
            foreach (var f in failed)
            {
                AnsiConsole.MarkupLine($"\n  {n++}) [bold]{Markup.Escape(f.FullName)}[/]");
                AnsiConsole.MarkupLine($"     [red]{Markup.Escape(f.Message ?? "")}[/]");
                if (f.Expected != null && f.Actual != null)
                {
                    AnsiConsole.MarkupLine($"     [green]expected:[/] {Markup.Escape(f.Expected)}");
                    AnsiConsole.MarkupLine($"     [red]     got:[/] {Markup.Escape(f.Actual)}");
                }
                string where = f.Stack ?? (f.Line > 0 ? $"{f.File}:{f.Line}" : "");
                if (where.Length > 0) AnsiConsole.MarkupLine($"     [grey]# {Markup.Escape(Rel(where))}[/]");
            }
        }
        if (profile && all.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Slowest examples:[/]");
            foreach (var r in all.OrderByDescending(r => r.Duration).Take(10))
                AnsiConsole.MarkupLine($"  {r.Duration.TotalMilliseconds,9:0.00} ms  {Markup.Escape(r.FullName)}");
        }
        int pass = all.Count(r => r.Status == TestStatus.Passed), pend = all.Count(r => r.Status == TestStatus.Pending), skip = all.Count(r => r.Status == TestStatus.Skipped);
        string color = failed.Count > 0 ? "red" : pend > 0 ? "yellow" : "green";
        AnsiConsole.MarkupLine($"\n[{color}]{all.Count} examples, {failed.Count} failures{(pend > 0 ? $", {pend} pending" : "")}{(skip > 0 ? $", {skip} skipped" : "")}[/] [grey]in {elapsed.TotalSeconds:0.00}s[/]");
        if (failed.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Rerun failed:[/]");
            foreach (var f in failed.Where(f => f.Line > 0))
                AnsiConsole.MarkupLine($"  [red]alice test {Markup.Escape(Rel(f.File))}:{f.Line}[/] [grey]# {Markup.Escape(f.FullName)}[/]");
        }
        if (seed != null) AnsiConsole.MarkupLine($"[grey]randomized with seed {seed}[/]");
        _ = pass;
    }

    static string Rel(string p) => Path.IsPathRooted(p) ? Path.GetRelativePath(Environment.CurrentDirectory, p) : p;

    static void Junit(List<TestResult> all, TimeSpan elapsed)
    {
        var suite = new XElement("testsuite",
            new XAttribute("name", "alice"), new XAttribute("tests", all.Count),
            new XAttribute("failures", all.Count(r => r.Status == TestStatus.Failed)),
            new XAttribute("skipped", all.Count(r => r.Status is TestStatus.Pending or TestStatus.Skipped)),
            new XAttribute("time", elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)),
            all.Select(r => new XElement("testcase",
                new XAttribute("classname", string.Join(".", r.Path)), new XAttribute("name", r.Name),
                new XAttribute("file", r.File), new XAttribute("line", r.Line),
                new XAttribute("time", r.Duration.TotalSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)),
                r.Status == TestStatus.Failed ? new XElement("failure", new XAttribute("message", r.Message ?? ""), r.Stack ?? "") : null,
                r.Status is TestStatus.Pending or TestStatus.Skipped ? new XElement("skipped", new XAttribute("message", r.Message ?? "")) : null)));
        Console.WriteLine(new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("testsuites", suite)).ToString());
    }
}
