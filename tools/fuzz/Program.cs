using System.Diagnostics;
using System.Text;
using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using Alice.Fuzz;

// alice-fuzz: long-running fuzzers over the shared engine (tests/Alice.Tests/Fuzz/Engine).
//
//   alice-fuzz parse [--seconds N] [--seed S] [--out DIR] [--root REPO]   parser + formatter
//   alice-fuzz typed [...]                                                binder/lowering, Roslyn, check, strict
//   alice-fuzz diff  [...] [--batch B]                                    C# vs IL, ALICE_DISABLE_PASSES, ALICE_PAR=0
//   alice-fuzz exec --dir DIR                                             (child) run DIR/*.al under both backends
//   alice-fuzz minimize FILE --mode parse|typed|diff                      shrink a saved program again
//
// Findings go to DIR (default tools/fuzz/findings) as <category>-<hash>.al, minimized, one per category per run.
// Exit code 3: the run stopped early after hung calls (leaked threads); the driver restarts with the next seed.

var a = args.ToList();
string Opt(string name, string def) { int i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : def; }
string mode = a.Count > 0 ? a[0] : "help";
string root = Opt("--root", FindRoot());
string outDir = Opt("--out", Path.Combine(root, "tools", "fuzz", "findings"));
int seconds = int.Parse(Opt("--seconds", "60"));
int seed = int.Parse(Opt("--seed", "1"));
int batch = int.Parse(Opt("--batch", "12"));

return mode switch
{
    "parse" => new Driver(root, outDir, seconds, seed).Parse(),
    "typed" => new Driver(root, outDir, seconds, seed).Typed(),
    "diff" => new Driver(root, outDir, seconds, seed).Diff(batch),
    "exec" => Driver.Exec(Opt("--dir", ".")),
    "syntax" => Driver.SyntaxLines(a[1]),
    "minimize" => new Driver(root, outDir, seconds, seed).MinimizeFile(a[1], Opt("--mode", "typed")),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("alice-fuzz parse|typed|diff [--seconds N] [--seed S] [--out DIR] [--root REPO] [--batch B] | exec --dir DIR | minimize FILE --mode M");
    return 2;
}

static string FindRoot()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Alice.sln"))) return d.FullName;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Alice.sln"))) return d.FullName;
    return Directory.GetCurrentDirectory();
}

sealed class Driver(string root, string outDir, int seconds, int seed)
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, int> categories = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> written = new(StringComparer.Ordinal);
    readonly HashSet<string> seenKeys = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, int> stats = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, int> features = new(StringComparer.Ordinal);
    DateTime lastStats = DateTime.UtcNow;

    bool TimeLeft => clock.Elapsed.TotalSeconds < seconds && Watchdog.Leaked < 3;

    void Count(string k, int n = 1) => stats[k] = stats.GetValueOrDefault(k) + n;

    void Report(bool final = false)
    {
        if (!final && (DateTime.UtcNow - lastStats).TotalSeconds < 60) return;
        lastStats = DateTime.UtcNow;
        var sb = new StringBuilder(final ? "FINAL " : "STATS ");
        sb.Append($"t={clock.Elapsed.TotalSeconds:F0}s seed={seed} ");
        sb.AppendJoin(' ', stats.Select(kv => kv.Key + "=" + kv.Value));
        if (categories.Count > 0) sb.Append(" | findings: ").AppendJoin(", ", categories.Select(kv => kv.Key + " x" + kv.Value));
        if (final && features.Count > 0) sb.Append("\nFEATURES ").AppendJoin(' ', features.Select(kv => kv.Key + "=" + kv.Value));
        Console.WriteLine(sb);
    }

    /// <summary>Records a finding: a triaged one (<see cref="Known"/>) is only counted; otherwise the first per key
    /// (rejections) or the first six per category are minimized and written.</summary>
    void Found(Finding f, Func<string, Finding?> check, int maxTests = 600)
    {
        if (Known.Match(f) is { } known) { Count("known:" + known); return; }
        Count("findings");
        categories[f.Category] = categories.GetValueOrDefault(f.Category) + 1;
        // keyed findings (diagnostics): one per distinct key; unkeyed: up to 6 minimized samples (equal minimal
        // programs land in the same file)
        if (f.Key != f.Category ? !seenKeys.Add(f.Key) || written.GetValueOrDefault(f.Category) >= 4 : written.GetValueOrDefault(f.Category) >= 6) return;
        written[f.Category] = written.GetValueOrDefault(f.Category) + 1;
        var sw = Stopwatch.StartNew();
        string min = f.Source;
        try { min = Minimizer.Minimize(f.Source, c => check(c)?.Key == f.Key, maxTests); }
        catch (Exception e) { Console.WriteLine("minimizer failed: " + e.Message); }
        // the detail of the minimized case (it still reproduces: the minimizer only keeps reproducing candidates)
        Finding mf;
        try { mf = (min != f.Source ? check(min) : null) is { } again && again.Key == f.Key ? again with { Source = min } : f with { Source = min }; }
        catch (Exception) { mf = f with { Source = min }; }
        var path = mf.Write(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "orig"));
        File.WriteAllText(Path.Combine(outDir, "orig", mf.Id + ".al"), f.ToFile());
        Console.WriteLine($"FINDING {f.Category} -> {path} ({f.Source.Split('\n').Length} -> {min.Split('\n').Length} lines, {sw.Elapsed.TotalSeconds:F0}s)");
    }

    // ------------------------------------------------------------------ parse

    public int Parse()
    {
        var corpus = Mutator.Corpus(root, "examples", "benchmarks", "samples", "guide");
        Console.WriteLine($"parse fuzzer: corpus {corpus.Count} files, {seconds}s, seed {seed}");
        var r = new Rng(seed);
        for (int i = 0; TimeLeft; i++)
        {
            string src;
            string kind;
            switch (i % 4)
            {
                case 0 or 1: src = SyntaxGen.Generate(seed * 1_000_003 + i); kind = "gen"; break;
                case 2: src = Mutator.Mutate(corpus[r.Next(corpus.Count)].Text, r); kind = "mut-corpus"; break;
                default: src = Mutator.Mutate(SyntaxGen.Generate(seed * 7919 + i), r); kind = "mut-gen"; break;
            }
            var (res, f) = Oracles.CheckSyntax(src);
            Count("programs");
            Count(kind + "-" + res.ToString().ToLowerInvariant());
            if (res == Oracles.ParseResult.Rejected && kind == "gen" && Oracles.LastRejection is { } why)
            {
                why = why.Length > 70 ? why[..70] : why;
                rejections[why] = rejections.GetValueOrDefault(why) + 1;
            }
            if (f != null) Found(f, c => Oracles.CheckSyntax(c).Finding);
            Report();
        }
        Report(final: true);
        Console.WriteLine("GEN-REJECTIONS " + string.Join(" | ", rejections.OrderByDescending(kv => kv.Value).Take(12).Select(kv => kv.Key + " x" + kv.Value)));
        return Watchdog.Leaked >= 3 ? 3 : 0;
    }

    readonly Dictionary<string, int> rejections = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ typed

    public int Typed()
    {
        Console.WriteLine($"typed fuzzer: {seconds}s, seed {seed}");
        for (int i = 0; TimeLeft; i++)
        {
            var gen = new TypedGen(new Rng(seed * 1_000_003 + i), avoidKnown: i % 2 == 1);
            var src = gen.Program();
            foreach (var feat in gen.Features) features[feat] = features.GetValueOrDefault(feat) + 1;
            var f = Oracles.CheckTyped(src, out var compiled);
            Count("programs");
            Count(compiled != null ? "compiled" : "not-compiled");
            if (f != null) Found(f, TypedCheck, 300);
            Report();
        }
        Report(final: true);
        return Watchdog.Leaked >= 3 ? 3 : 0;
    }

    // ------------------------------------------------------------------ diff

    public int Diff(int batchSize)
    {
        Console.WriteLine($"differential fuzzer: {seconds}s, seed {seed}, variant batch {batchSize}");
        var ds = new Oracles.DiffStats();
        var pending = new List<(string Name, string Src, string Cs, string Il)>();
        var r = new Rng(seed);
        for (int i = 0; TimeLeft; i++)
        {
            var gen = new TypedGen(new Rng(seed * 1_000_003 + i), avoidKnown: true);
            var src = gen.Program();
            foreach (var feat in gen.Features) features[feat] = features.GetValueOrDefault(feat) + 1;
            Count("programs");
            var files = new[] { new SourceFile("main.al", src) };
            Oracles.Compiled? compiled = null;
            try { var cs = AliceCompiler.TranspileAll(files); compiled = new(cs, AliceCompiler.Emit(cs, "fuzz", exe: true)); }
            catch (Exception) { Count("not-compiled"); continue; }   // the typed fuzzer owns compile findings
            int before = ds.Compared, unsupBefore = ds.IlUnsupported;
            var f = Oracles.CheckDifferential(src, compiled, ds);
            bool unsupported = ds.IlUnsupported > unsupBefore;
            foreach (var feat in gen.Features)
            {
                var (n, u) = ilCoverage.GetValueOrDefault(feat);
                ilCoverage[feat] = (n + 1, u + (unsupported ? 1 : 0));
            }
            if (unsupported) foreach (var k in ds.LastUnsupported) unsupportedPrograms[k] = unsupportedPrograms.GetValueOrDefault(k) + 1;
            if ((DateTime.UtcNow - lastCoverage).TotalSeconds >= 600) CoverageTable(final: false);
            if (f != null) Found(f, DiffCheck, 300);
            // variants compare each backend with itself: a known C#-vs-IL difference does not keep a program out
            if (ds.Compared > before && (f == null || Known.Match(f) != null)) pending.Add(("p" + i, src, ds.LastCs!, ds.LastIl!));
            if (pending.Count >= batchSize) { Variants(pending, r); pending.Clear(); }
            Report();
        }
        if (pending.Count > 0) Variants(pending, r);
        Count("il-compared", ds.Compared);
        Count("il-unsupported", ds.IlUnsupported);
        Report(final: true);
        CoverageTable(final: true);
        if (ds.Unsupported.Count > 0) Console.WriteLine("IL-UNSUPPORTED " + string.Join(" | ", ds.Unsupported.OrderByDescending(kv => kv.Value).Take(15).Select(kv => kv.Key + " x" + kv.Value)));
        return Watchdog.Leaked >= 3 ? 3 : 0;
    }

    static readonly string[] EffectCodes = ["AL0209", "AL0210", "AL0211", "AL0212"];

    /// <summary>Typed oracles for a shrinking candidate: it must still terminate by construction, and an effect
    /// rejection must go away without the declared effects (so the rest of the program stays valid).</summary>
    static Finding? TypedCheck(string src)
    {
        if (!Shrink.Terminates(src) || !ShrinkTyping.WellTyped(src)) return null;
        var f = Oracles.CheckTyped(src, out _);
        if (f != null && f.Category.StartsWith("rejected:") && EffectCodes.Any(f.Category.Contains)
            && Oracles.CheckTyped(Shrink.WithoutEffects(src), out _, strict: false) is { } g && g.Category.StartsWith("rejected:")) return null;
        return f;
    }

    // IL coverage per generator feature: programs with the feature, and how many of them the IL backend could not
    // compile (an unsupported node anywhere in the program); per unsupported node kind, programs that hit it
    readonly SortedDictionary<string, (int Programs, int Unsupported)> ilCoverage = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, int> unsupportedPrograms = new(StringComparer.Ordinal);
    DateTime lastCoverage = DateTime.UtcNow;

    void CoverageTable(bool final)
    {
        lastCoverage = DateTime.UtcNow;
        string tag = final ? "IL-COVERAGE-FINAL" : "IL-COVERAGE";
        Console.WriteLine($"{tag} t={clock.Elapsed.TotalSeconds:F0}s | feature | programs | il-unsupported | il-ok %");
        foreach (var (feat, (n, u)) in ilCoverage.OrderByDescending(kv => kv.Value.Unsupported * 1.0 / Math.Max(1, kv.Value.Programs)))
            Console.WriteLine($"{tag} | {feat} | {n} | {u} | {(n == 0 ? 0 : 100.0 * (n - u) / n):F1}");
        foreach (var (k, n) in unsupportedPrograms.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"{tag} | node: {k} | {n} programs");
    }

    static Finding? DiffCheck(string src)
    {
        if (!Shrink.Terminates(src) || !ShrinkTyping.WellTyped(src)) return null;
        try
        {
            var cs = AliceCompiler.TranspileAll([new SourceFile("main.al", src)]);
            var c = new Oracles.Compiled(cs, AliceCompiler.Emit(cs, "fuzz", exe: true));
            return Oracles.CheckDifferential(src, c, new Oracles.DiffStats());
        }
        catch (Exception) { return null; }
    }

    static string? IlRun(string src)
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], "fuzzil", exe: true);
        return res.Ok ? Oracles.Execute(() => res.Pe!).Output : null;
    }

    /// <summary>The batch again in child processes: every pass off, one random pass off, ALICE_PAR=0.</summary>
    void Variants(List<(string Name, string Src, string Cs, string Il)> progs, Rng r)
    {
        var dir = Path.Combine(Path.GetTempPath(), "alice-fuzz-" + Environment.ProcessId, "batch");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        foreach (var p in progs) File.WriteAllText(Path.Combine(dir, p.Name + ".al"), p.Src);
        var variants = new List<(string Name, string Var, string Value)>
        {
            ("passes-off:all", "ALICE_DISABLE_PASSES", string.Join(",", Oracles.Passes)),
            ("passes-off:" + r.Pick(Oracles.Passes), "ALICE_DISABLE_PASSES", ""),
            ("par0", "ALICE_PAR", "0"),
        };
        variants[1] = (variants[1].Name, variants[1].Var, variants[1].Name["passes-off:".Length..]);
        foreach (var (vname, var, value) in variants)
        {
            var outs = RunChild(dir, var, value);
            Count("variant-runs");
            foreach (var p in progs)
            {
                if (!outs.TryGetValue(p.Name, out var o)) { Count("variant-missing"); continue; }
                Count("variant-compared");
                string kind = vname.StartsWith("par0") ? "diff-par0" : "diff-" + vname.Replace(':', '-');
                string? which = o.Cs != p.Cs ? "cs" : o.Il != null && o.Il != p.Il ? "il" : null;
                if (which == null) continue;
                string baseOut = which == "cs" ? p.Cs : p.Il, varOut = which == "cs" ? o.Cs : o.Il!;
                var cat = Oracles.Category(kind + "-" + which, baseOut, varOut);
                var f = new Finding(cat, $"{var}={value} ({which} backend)\ndefault:\n{Oracles.Short(baseOut)}\nwith {var}={value}:\n{Oracles.Short(varOut)}", p.Src);
                Found(f, c => VariantCheck(c, kind, which, var, value), 120);
            }
        }
    }

    Finding? VariantCheck(string src, string kind, string which, string var, string value)
    {
        if (!Shrink.Terminates(src) || !ShrinkTyping.WellTyped(src)) return null;
        try
        {
            var cs = AliceCompiler.TranspileAll([new SourceFile("main.al", src)]);
            var baseCs = Oracles.Execute(() => AliceCompiler.Emit(cs, "fuzz", exe: true)).Output;
            var baseIl = IlRun(src);
            var dir = Path.Combine(Path.GetTempPath(), "alice-fuzz-" + Environment.ProcessId, "min");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "m.al"), src);
            if (!RunChild(dir, var, value).TryGetValue("m", out var o)) return null;
            string b = which == "cs" ? baseCs : baseIl ?? "", v = which == "cs" ? o.Cs : o.Il ?? b;
            return b == v ? null : new Finding(Oracles.Category(kind + "-" + which, b, v), $"{var}={value} ({which} backend)\ndefault:\n{Oracles.Short(b)}\nwith {var}={value}:\n{Oracles.Short(v)}", src);
        }
        catch (Exception) { return null; }
    }

    static Dictionary<string, (string Cs, string? Il)> RunChild(string dir, string var, string value)
    {
        var self = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(self);
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") psi.ArgumentList.Add(typeof(Driver).Assembly.Location);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--dir");
        psi.ArgumentList.Add(dir);
        psi.Environment.Remove("ALICE_PAR");
        psi.Environment.Remove("ALICE_DISABLE_PASSES");
        psi.Environment[var] = value;
        var childOut = Oracles.RunProcess(psi, 600_000);
        // If child crashed with signal, write marker files for all programs so parent can detect it
        if (childOut.Contains("<signal "))
        {
            var signalMarker = childOut[childOut.IndexOf("<signal")..];
            foreach (var f in Directory.GetFiles(dir, "*.al"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var crashFile = Path.Combine(dir, name + ".cs.out");
                if (!File.Exists(crashFile)) File.WriteAllText(crashFile, signalMarker);
            }
        }
        var res = new Dictionary<string, (string, string?)>();
        foreach (var f in Directory.GetFiles(dir, "*.cs.out"))
        {
            var name = Path.GetFileName(f)[..^".cs.out".Length];
            var il = Path.Combine(dir, name + ".il.out");
            res[name] = (File.ReadAllText(f), File.Exists(il) ? File.ReadAllText(il) : null);
            File.Delete(f);
            if (File.Exists(il)) File.Delete(il);
        }
        return res;
    }

    /// <summary>Child: compile and run every DIR/*.al under both backends with this process's environment.</summary>
    public static int Exec(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*.al").Order(StringComparer.Ordinal))
        {
            var src = File.ReadAllText(f);
            var name = Path.Combine(dir, Path.GetFileNameWithoutExtension(f));
            try
            {
                var cs = AliceCompiler.TranspileAll([new SourceFile("main.al", src)]);
                var img = AliceCompiler.Emit(cs, "fuzz", exe: true);
                var r = Oracles.Execute(() => img);
                File.WriteAllText(name + ".cs.out", r.Output);
                if (r.Hung) return 3;
                try
                {
                    var il = IlRun(src);
                    if (il != null) File.WriteAllText(name + ".il.out", il);
                    if (il == "<hang>") return 3;
                }
                catch (Exception ie) { File.WriteAllText(name + ".il.out", "<il-crash " + ie.GetType().Name + ": " + ie.Message.Split('\n')[0] + ">"); }
            }
            catch (Exception e) { File.WriteAllText(name + ".cs.out", "<compile " + e.GetType().Name + ": " + e.Message.Split('\n')[0] + ">"); }
        }
        return 0;
    }

    /// <summary>Debug aid: parses every line of FILE as its own program and prints the parse result.</summary>
    public static int SyntaxLines(string file)
    {
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.Trim().Length == 0) continue;
            // ` ;; ` in the line stands for a newline (multi-line probes)
            try { Alice.Compiler.Syntax.Parser.ParseFile(line.Replace(" ;; ", "\n") + "\n", "x.al"); Console.WriteLine("ok    " + line); }
            catch (Exception e) { Console.WriteLine("FAIL  " + line + "   -> " + e.Message.Split('\n')[0]); }
        }
        return 0;
    }

    // ------------------------------------------------------------------ minimize a saved file

    public int MinimizeFile(string file, string m)
    {
        // a saved finding: the program follows the comment header and one blank line; any other file is taken as is
        // (byte for byte: a lexer bug may need the file to end without a newline)
        var src = File.ReadAllText(file).Replace("\r\n", "\n");
        if (src.StartsWith("# fuzz finding ", StringComparison.Ordinal) && src.IndexOf("\n\n", StringComparison.Ordinal) is var cut and >= 0)
            src = src[(cut + 2)..];
        Func<string, Finding?> check = m switch
        {
            "parse" => c => Oracles.CheckSyntax(c).Finding,
            "diff" => DiffCheck,
            _ => TypedCheck,
        };
        var f0 = check(src);
        if (f0 == null) { Console.WriteLine("does not reproduce"); return 1; }
        var min = Minimizer.Minimize(src, c => check(c)?.Key == f0.Key);
        var f = (check(min) ?? f0) with { Source = min };
        Console.WriteLine(f.Category + " -> " + f.Write(outDir));
        return 0;
    }
}
