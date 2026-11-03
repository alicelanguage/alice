using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alice.Compiler;
using Alice.Compiler.Syntax;
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
    /// <summary>
    /// One rule's verdict. <paramref name="Counterexample"/> is the failing input (`x = 7, lo = -3`) and
    /// <paramref name="Outcome"/> what the compiled rule did on it (`false` or `raised OverflowException`).
    /// <paramref name="NotProven"/> is set when there is no verdict either way (time limit, or --smt cannot translate
    /// a rule too large to enumerate): not a failure of the code, but not a proof.
    /// </summary>
    public sealed record RuleResult(string Rule, long Cases, string? Counterexample, double Ms, bool Smt = false, string? Outcome = null, string? NotProven = null)
    {
        public bool Proven => Counterexample == null && NotProven == null;
        public string Status => NotProven != null ? "unknown" : Counterexample == null ? "proven" : "fails";
        /// <summary>`rule X FAILS: x = 7, lo = -3 -> false`.</summary>
        public string FailureLine => $"rule {Rule} FAILS: {Counterexample} -> {Outcome ?? "false"}";
    }

    public const string ProofFile = "PROOF.al";

    /// <summary>`--smt`: Z3 proves each rule it can translate for every input of its domain; the rest are enumerated
    /// (when finite enough) or reported as not proven. A Z3 counterexample is replayed through the compiled rule
    /// before it is reported, so a counterexample on screen is an input that really fails.</summary>
    public static List<RuleResult> CheckSmt(List<SourceFile> files, List<string> refs, TimeSpan? timeout = null)
    {
        if (Proof.SmtProver.Unavailable() is { } why) throw new UsageException(why);
        var results = new List<RuleResult>();
        var fallback = new HashSet<string>();
        var proved = new Dictionary<string, IReadOnlyList<RuleDomain>>();
        uint ms = timeout is { } t ? (uint)Math.Clamp(t.TotalMilliseconds, 1, uint.MaxValue) : Proof.SmtProver.DefaultTimeoutMs;
        foreach (var o in Proof.SmtProver.Prove(files, refs, ms))
        {
            if (o.Untranslatable == null)
            {
                results.Add(new RuleResult(o.Rule, -1, o.Counterexample, o.Ms, Smt: true));
                if (o.Proved && o.Domains != null) proved[o.Rule] = o.Domains;
            }
            else
            {
                fallback.Add(o.Rule);
                results.Add(new RuleResult(o.Rule, 0, null, o.Ms, NotProven: $"--smt: {o.Untranslatable}; the domain is too large to enumerate"));
            }
        }
        // one compiled image replays the counterexamples and enumerates the rules Z3 could not decide
        Type? program;
        Alice.Compiler.Backend.Emitter.SkipOversizedRules = true;   // a full-range rule has no enumerator: Z3 decided it
        try { program = Compile(files, refs); }
        finally { Alice.Compiler.Backend.Emitter.SkipOversizedRules = false; }
        for (int i = 0; i < results.Count; i++)
            if (results[i] is { Smt: true, Counterexample: { } cex } r) results[i] = Replay(program, r, cex);
            else if (results[i] is { Smt: true, Proven: true } p && proved.TryGetValue(p.Rule, out var doms)) results[i] = SampleCheck(program, p, doms);
        if (fallback.Count > 0)
            foreach (var r in Enumerate(program, timeout, fallback))
                results[results.FindIndex(x => x.Rule == r.Rule)] = r;   // enumerated after all
        return Sorted(results);
    }

    /// <summary>Compiles the sources and runs every rule's generated `__provect_` method (`__prove_` without a time limit).</summary>
    public static List<RuleResult> Check(List<SourceFile> files, List<string> refs, TimeSpan? timeout = null) =>
        Sorted(Enumerate(Compile(files, refs), timeout, null));

    /// <summary>Rules in ordinal name order: the table, the JSON and PROOF.al list them the same way on every run.</summary>
    static List<RuleResult> Sorted(List<RuleResult> r) => r.OrderBy(x => x.Rule, StringComparer.Ordinal).ToList();

    static Type? Compile(List<SourceFile> files, List<string> refs)
    {
        var cs = AliceCompiler.TranspileAll(files, refs);
        return AliceCompiler.Load(AliceCompiler.Emit(cs, "alice_prove", false, refs)).GetType("Program");
    }

    static List<RuleResult> Enumerate(Type? program, TimeSpan? timeout, ISet<string>? only)
    {
        var results = new List<RuleResult>();
        if (program == null) return results;
        foreach (var m in program.GetMethods().Where(m => m.Name.StartsWith("__provect_")).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            string rule = m.Name["__provect_".Length..];
            if (only != null && !only.Contains(rule)) continue;
            // the limit runs on a dedicated thread, not CancellationTokenSource(timeout): that timer's callback needs a
            // thread-pool thread and Parallel.For holds them all, so the cancel came seconds late (0.3s limit, 11.8s stop).
            // Neither object is disposed: the thread may still touch them after the rule returns.
            var cts = new CancellationTokenSource();
            var done = new ManualResetEventSlim();
            if (timeout is { } t) new Thread(() => { if (!done.Wait(t)) cts.Cancel(); }) { IsBackground = true }.Start();
            var sw = Stopwatch.StartNew();
            System.Runtime.CompilerServices.ITuple r;
            try { r = (System.Runtime.CompilerServices.ITuple)m.Invoke(null, [cts.Token])!; }
            catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException)
            {
                results.Add(new RuleResult(rule, 0, null, sw.Elapsed.TotalMilliseconds, NotProven: $"time limit of {timeout!.Value.TotalSeconds:0.###}s reached before every case ran"));
                continue;
            }
            finally { done.Set(); }
            var (cex, outcome) = Split((string?)r[1]);
            results.Add(new RuleResult(rule, (long)r[0]!, cex, sw.Elapsed.TotalMilliseconds, Outcome: outcome));
        }
        return results;
    }

    /// <summary>The enumerator's `x = 7, b = True: raised OverflowException` as (`x = 7, b = true`, `raised OverflowException`).</summary>
    static (string?, string?) Split(string? raw)
    {
        if (raw == null) return (null, null);
        string cex = raw, outcome = "false";
        int at = raw.IndexOf("raised ", StringComparison.Ordinal);
        if (at >= 0)
        {
            cex = raw[..at].TrimEnd(' ', ':');
            outcome = raw[at..];
        }
        if (cex.Length == 0) cex = "(no variables)";
        return (cex.Replace(" = True", " = true").Replace(" = False", " = false"), outcome);
    }

    /// <summary>Runs the compiled rule on Z3's counterexample: it must return false or raise. If it holds there, Z3 and
    /// the compiler disagree about the rule's semantics: an internal error, reported as a failure at that input (as
    /// <see cref="SampleCheck"/> does for a contradicted proof), never as a proof.</summary>
    static RuleResult Replay(Type? program, RuleResult r, string cex)
    {
        var m = program?.GetMethod("rule_" + r.Rule);
        if (m == null) return r;
        var values = Assignments(cex);
        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i >= values.Count) return r;
            var v = values[i].Value;
            args[i] = ps[i].ParameterType == typeof(bool) ? v is bool b ? b : bool.Parse(v.ToString()!) : System.Convert.ChangeType(v, ps[i].ParameterType);
        }
        try
        {
            if ((bool)m.Invoke(null, args)! == false) return r with { Outcome = "false" };
        }
        catch (TargetInvocationException e) { return r with { Outcome = "raised " + (e.InnerException ?? e).GetType().Name }; }
        return r with { Outcome = "true " + Contradicted };
    }

    /// <summary>Points of a rule's domain a Z3 proof is cross-checked on: every bound, its neighbours and 0 when inside,
    /// then seeded random points (the same on every run, so verdicts are reproducible).</summary>
    public const int SamplePoints = 64;

    /// <summary>The outcome suffix of a rule whose Z3 verdict (proof or counterexample) the compiled rule contradicts.</summary>
    public const string Contradicted = "(internal error: solver verdict contradicted by compiled rule; please report it)";

    /// <summary>
    /// A Z3 proof (UNSAT) is cross-checked on <see cref="SamplePoints"/> inputs through the compiled rule (review-r1 F8:
    /// Z3 once modelled Int8 at 8 bits where Alice computes at 32). An input where the compiled rule returns false or
    /// raises is an internal error of `--smt`: the rule is reported as failing there, never as proven.
    /// </summary>
    internal static RuleResult SampleCheck(Type? program, RuleResult r, IReadOnlyList<RuleDomain> domains)
    {
        var m = program?.GetMethod("rule_" + r.Rule);
        if (m == null) return r;
        var ps = m.GetParameters();
        if (ps.Length != domains.Count) return r;
        var rng = new Random(r.Rule.Aggregate(17, (h, c) => unchecked(h * 31 + c)));
        var picks = domains.Select(d => Candidates(d, rng)).ToList();
        if (picks.Any(c => c.Count == 0)) return r;   // a domain the enumerator cannot express either
        for (int k = 0; k < SamplePoints; k++)
        {
            var vals = picks.Select(c => c[k % c.Count]).ToList();
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                args[i] = vals[i] is bool b ? b : vals[i] is Int128 n ? ToParam(n, ps[i].ParameterType) : null;
            string at = string.Join(", ", domains.Select((d, i) => $"{d.Name} = {(vals[i] is bool b ? b ? "true" : "false" : vals[i])}"));
            string? outcome = null;
            try { if ((bool)m.Invoke(null, args)! == false) outcome = "false"; }
            catch (TargetInvocationException e) { outcome = "raised " + (e.InnerException ?? e).GetType().Name; }
            if (outcome != null)
                return r with { Counterexample = at, Outcome = outcome + " " + Contradicted };
        }
        return r;
    }

    /// <summary>The sample values of one domain variable: <see cref="SamplePoints"/> entries, bounds first.</summary>
    static List<object> Candidates(RuleDomain d, Random rng)
    {
        if (d.IsBool) return [false, true];
        Int128 lo = d.Lo, hi = d.Hi;
        var c = new List<object>();
        if (lo > hi) return c;
        foreach (var v in new Int128[] { lo, hi, lo + 1, hi - 1, 0, -1, 1 })
            if (v >= lo && v <= hi && !c.Contains(v)) c.Add(v);
        while (c.Count < SamplePoints) c.Add(lo + (Int128)(rng.NextDouble() * (double)(hi - lo + 1)) is var x && x > hi ? hi : x);
        return c;
    }

    static object ToParam(Int128 v, Type t) => t == typeof(ulong) ? (ulong)(long)v : t == typeof(uint) ? (uint)v
        : t == typeof(ushort) ? (ushort)v : t == typeof(byte) ? (byte)v : t == typeof(sbyte) ? (sbyte)v
        : t == typeof(short) ? (short)v : t == typeof(int) ? (int)v : (object)(long)v;

    /// <summary>`x = 7, b = true` as ordered (name, long or bool) pairs.</summary>
    public static List<(string Name, object Value)> Assignments(string cex)
    {
        var r = new List<(string, object)>();
        if (cex == "(no variables)") return r;
        foreach (var part in cex.Split(", "))
        {
            int eq = part.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;
            string name = part[..eq], v = part[(eq + 3)..];
            r.Add((name, long.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : bool.TryParse(v, out var b) ? b : v));
        }
        return r;
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
            sb.Append(r.NotProven != null ? $"# rule {r.Rule}: not proven ({r.NotProven})\n"
                : r.Counterexample == null
                ? r.Smt ? $"# rule {r.Rule}: proven for every input of its domain (smt)\n" : $"# rule {r.Rule}: proven over {r.Cases} cases\n"
                : $"# rule {r.Rule}: FAILS at {r.Counterexample}{(r.Outcome is { } o && o != "false" ? ": " + o : "")}\n");
        return sb.ToString();
    }

    /// <summary>`--format json`: the whole run as one JSON object on stdout, rules in name order.</summary>
    public static string Json(List<RuleResult> results, bool ok, string proof)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", ok);
            w.WriteString("proof", proof);
            w.WriteStartArray("rules");
            foreach (var r in results)
            {
                w.WriteStartObject();
                w.WriteString("rule", r.Rule);
                w.WriteString("status", r.Status);
                w.WriteString("engine", r.Smt ? "smt" : "enumeration");
                if (r.Smt || r.NotProven != null) w.WriteNull("cases"); else w.WriteNumber("cases", r.Cases);
                w.WriteNumber("ms", Math.Round(r.Ms, 1));
                if (r.Counterexample != null)
                {
                    w.WriteStartObject("counterexample");
                    foreach (var (name, v) in Assignments(r.Counterexample))
                        if (v is long n) w.WriteNumber(name, n); else if (v is bool b) w.WriteBoolean(name, b); else w.WriteString(name, v.ToString());
                    w.WriteEndObject();
                    w.WriteString("result", r.Outcome ?? "false");
                    w.WriteString("message", r.FailureLine);
                }
                if (r.NotProven != null) w.WriteString("reason", r.NotProven);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>
    /// Exit code 0 when every rule is proven (and, with --check, PROOF.al is current); 1 otherwise. `json` prints
    /// only the JSON summary on stdout, so a CI step or an agent can parse it.
    /// </summary>
    public static int Run(List<SourceFile> files, List<string> refs, bool checkOnly, string dir, bool smt = false, TimeSpan? timeout = null, bool json = false)
    {
        if (!files.Any(f => f.Text.Contains("rule "))) throw new UsageException("no rules found", "declare them in RULES.al: `rule name(x in 0..100)` ... `end`");
        var results = smt ? CheckSmt(files, refs, timeout) : Check(files, refs, timeout);
        bool ok = results.All(r => r.Proven);
        string cert = Certificate(files, results), path = Path.Combine(dir, ProofFile);
        bool stale = false;
        if (checkOnly) stale = (File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "") != cert;
        else File.WriteAllText(path, cert);
        if (json)
        {
            Console.Out.Write(Json(results, ok && !stale, checkOnly ? stale ? "stale" : "current" : "written"));
            return ok && !stale ? 0 : 1;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumn("rule").AddColumn(new TableColumn("cases").RightAligned()).AddColumn("result").AddColumn(new TableColumn("ms").RightAligned());
        foreach (var r in results)
            table.AddRow(Markup.Escape(r.Rule), r.Smt ? "all" : r.NotProven != null ? "-" : r.Cases.ToString("N0"),
                r.NotProven != null ? $"[yellow]not proven: {Markup.Escape(r.NotProven)}[/]" : r.Counterexample == null ? "[green]proven[/]" : "[red]FAILS[/]", r.Ms.ToString("0"));
        AnsiConsole.Write(table);
        foreach (var r in results.Where(r => r.Counterexample != null))
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(r.FailureLine)}[/]");
        if (stale)
            Ui.Ui.Error($"{ProofFile} is missing or stale: the code or RULES.al changed since it was written; run `alice prove` and commit {ProofFile}");
        if (results.Any(r => r.Counterexample != null))
            Ui.Ui.Error("some rules fail: fix the code (or the rule, if the rule is wrong) and run `alice prove` again");
        if (results.Any(r => r.NotProven != null))
            Ui.Ui.Error("some rules are not proven: the table says why (a time limit: raise --timeout, narrow the domain, or try --smt)");
        return ok && !stale ? 0 : 1;
    }
}
