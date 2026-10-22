using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Alice.Compiler;
using Spectre.Console;

namespace Alice.Cli.Commands;

/// <summary>
/// `alice prove` (0.12.2): Alice's take on Bend's `LAWS.bend` / `PROOF.bend`.
/// RULES.al declares `rule` properties over finite, explicit domains. Rules are pure (AL0210), so running one on
/// every case of its domain is a proof by exhaustion. `alice prove` checks them all and writes PROOF.al, a
/// certificate holding a hash of every source. `alice prove --check` (CI, before committing) fails when a rule
/// fails or when PROOF.al no longer matches the sources. What is proven is the program as Alice compiles it; the
/// compiler itself is trusted, not proven.
/// </summary>
public static class Prove
{
    public sealed record RuleResult(string Rule, long Cases, string? Counterexample, double Ms);

    public const string ProofFile = "PROOF.al";

    /// <summary>Compiles the sources and runs every rule's generated `__prove_` method.</summary>
    public static List<RuleResult> Check(List<SourceFile> files, List<string> refs)
    {
        var cs = AliceCompiler.TranspileAll(files, refs);
        var asm = AliceCompiler.Load(AliceCompiler.Emit(cs, "alice_prove", false, refs));
        var results = new List<RuleResult>();
        var program = asm.GetType("Program");
        if (program == null) return results;
        foreach (var m in program.GetMethods().Where(m => m.Name.StartsWith("__prove_")).OrderBy(m => m.Name))
        {
            var sw = Stopwatch.StartNew();
            var r = (System.Runtime.CompilerServices.ITuple)m.Invoke(null, null)!;
            results.Add(new RuleResult(m.Name["__prove_".Length..], (long)r[0]!, (string?)r[1], sw.Elapsed.TotalMilliseconds));
        }
        return results;
    }

    /// <summary>SHA-256 over every source but PROOF.al, by path (relative to the working directory, so the certificate
    /// does not depend on where the checkout lives) then text.</summary>
    public static string Hash(List<SourceFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.Where(f => Path.GetFileName(f.Path) != ProofFile).OrderBy(f => f.Path, StringComparer.Ordinal))
            sb.Append(Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(f.Path)).Replace('\\', '/')).Append('\0').Append(f.Text.Replace("\r\n", "\n")).Append('\0');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>PROOF.al: valid Alice (comments only), deterministic, no timestamps.</summary>
    public static string Certificate(List<SourceFile> files, List<RuleResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("# PROOF.al: written by `alice prove`. Do not edit; run `alice prove` after changing the code or RULES.al.\n");
        sb.Append("# Every rule marked proven returned true on every case of its domain. Rules are pure, so this is a proof by\n");
        sb.Append("# exhaustion of the program as Alice compiles it. `alice prove --check` fails when the sources change.\n#\n");
        sb.Append($"# alice {App.Version}\n# sources sha256 {Hash(files)}\n");
        foreach (var r in results.OrderBy(r => r.Rule, StringComparer.Ordinal))
            sb.Append(r.Counterexample == null ? $"# rule {r.Rule}: proven over {r.Cases} cases\n" : $"# rule {r.Rule}: FAILS at {r.Counterexample}\n");
        return sb.ToString();
    }

    public static int Run(List<SourceFile> files, List<string> refs, bool checkOnly, string dir)
    {
        if (!files.Any(f => f.Text.Contains("rule "))) throw new UsageException("no rules found", "declare them in RULES.al: `rule name(x in 0..100)` ... `end`");
        var results = Check(files, refs);
        var table = new Table().Border(TableBorder.Rounded).AddColumn("rule").AddColumn(new TableColumn("cases").RightAligned()).AddColumn("result").AddColumn(new TableColumn("ms").RightAligned());
        foreach (var r in results)
            table.AddRow(Markup.Escape(r.Rule), r.Cases.ToString("N0"), r.Counterexample == null ? "[green]proven[/]" : $"[red]fails at {Markup.Escape(r.Counterexample)}[/]", r.Ms.ToString("0"));
        AnsiConsole.Write(table);
        bool ok = results.All(r => r.Counterexample == null);
        string cert = Certificate(files, results), path = Path.Combine(dir, ProofFile);
        if (checkOnly)
        {
            string old = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
            if (old != cert)
            {
                Ui.Ui.Error($"{ProofFile} is missing or stale: the code or RULES.al changed since it was written; run `alice prove` and commit {ProofFile}");
                return 1;
            }
        }
        else File.WriteAllText(path, cert);
        if (!ok) Ui.Ui.Error("some rules fail: fix the code (or the rule) and run `alice prove` again");
        return ok ? 0 : 1;
    }
}
