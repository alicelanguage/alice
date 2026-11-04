using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Syntax;
using ILVerify;

namespace Alice.Fuzz;

/// <summary>
/// The fuzz oracles. Each check returns null (the program behaved) or a <see cref="Finding"/> whose category is what
/// the minimizer keeps reproducing.
/// <list type="bullet">
/// <item>Syntax: the lexer/parser only ever fail with a <see cref="CompileException"/> (an AL diagnostic), never an
/// internal exception or a hang; `alice fmt` (<see cref="Formatter.Format"/>) never throws, is idempotent, and its
/// output of a parseable program parses to the same position-free AST (<see cref="AstDump"/>).</item>
/// <item>Typed: the program compiles (no internal compiler error, no AL diagnostic for a generated well-typed
/// program), the emitted C# compiles under Roslyn, compiling twice gives the same C# and `alice check` gives the same
/// diagnostics twice, strict mode (AL0220) accepts it and runs it the same.</item>
/// <item>Differential: the C# backend and the IL backend (ADR 0033) print the same stdout and raise the same
/// exception; the IL image passes ILVerify.</item>
/// </list>
/// </summary>
public static class Oracles
{
    public const int ParseMs = 10_000, CompileMs = 60_000, RunMs = 20_000;

    /// <summary>Exception signature: type plus the compiler frame that threw (stable across minimization).</summary>
    public static string Sig(Exception e)
    {
        var frame = new System.Diagnostics.StackTrace(e).GetFrames()?.Select(f => f.GetMethod())
            .FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("Alice") == true);
        string where = frame == null ? "" : "@" + (frame.DeclaringType!.Name.Split('`')[0]) + "." + frame.Name;
        return e.GetType().Name + where;
    }

    /// <summary>Exception and the top of its stack, source paths relative to the repository.</summary>
    static string Brief(Exception e) => e.GetType().Name + ": " + e.Message + "\n" + string.Join("\n", (e.StackTrace ?? "").Split('\n').Take(8)
        .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @" in \S*?[/\\](?=(src|tests|tools)[/\\])", " in ")));

    // ------------------------------------------------------------------ syntax

    /// <summary>Outcome of one parse (for stats): ok, rejected with a diagnostic, or a finding.</summary>
    public enum ParseResult { Ok, Rejected, Finding }

    /// <summary>The diagnostic of the last rejected parse (stats: what the generators get wrong).</summary>
    public static string? LastRejection;

    public static (ParseResult Result, Finding? Finding) CheckSyntax(string src)
    {
        var (fDone, formatted, fErr) = Watchdog.Run(() => Formatter.Format(src), ParseMs);
        if (!fDone) return (ParseResult.Finding, new Finding("fmt-hang", "Formatter.Format did not return within " + ParseMs + " ms", src));
        if (fErr != null) return (ParseResult.Finding, new Finding("fmt-crash:" + Sig(fErr), Brief(fErr), src));
        var (gDone, twice, gErr) = Watchdog.Run(() => Formatter.Format(formatted!), ParseMs);
        if (gDone && gErr == null && twice != formatted)
            return (ParseResult.Finding, new Finding("fmt-not-idempotent", FirstLineDiff(formatted!, twice!), src));

        var (pDone, ast, pErr) = Watchdog.Run(() => AstDump.Of(Parser.ParseFile(src, "fuzz.al")), ParseMs);
        if (!pDone) return (ParseResult.Finding, new Finding("parse-hang", "Parser.ParseFile did not return within " + ParseMs + " ms", src));
        if (pErr is CompileException pce) { LastRejection = System.Text.RegularExpressions.Regex.Replace(pce.Message, @"^.*?:\d+:\d+: ", ""); return (ParseResult.Rejected, null); }
        if (pErr != null) return (ParseResult.Finding, new Finding("parse-crash:" + Sig(pErr), Brief(pErr), src));

        var (qDone, ast2, qErr) = Watchdog.Run(() => AstDump.Of(Parser.ParseFile(formatted!, "fuzz.al")), ParseMs);
        if (!qDone) return (ParseResult.Finding, new Finding("parse-hang", "parsing the formatted program did not return", src));
        if (qErr != null) return (ParseResult.Finding, new Finding("fmt-breaks-parse", "the program parses but `alice fmt` output does not:\n" + Brief(qErr) + "\n--- formatted:\n" + formatted, src));
        if (ast != ast2) return (ParseResult.Finding, new Finding("fmt-changes-ast", AstDump.FirstDifference(ast!, ast2!), src));
        return (ParseResult.Ok, null);
    }

    static string FirstLineDiff(string a, string b)
    {
        var x = a.Split('\n'); var y = b.Split('\n');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            string l = i < x.Length ? x[i] : "<none>", m = i < y.Length ? y[i] : "<none>";
            if (l != m) return $"line {i + 1}: fmt once: `{l}`\nfmt twice: `{m}`";
        }
        return "";
    }

    // ------------------------------------------------------------------ typed (binder / lowering)

    public sealed record Compiled(List<string> CSharp, byte[] Image);

    /// <summary>Compiles through the C# backend with every typed-program oracle. <paramref name="compiled"/> is set
    /// when the program compiled (even when a later oracle found something).</summary>
    public static Finding? CheckTyped(string src, out Compiled? compiled, bool strict = true)
    {
        compiled = null;
        var files = new[] { new SourceFile("main.al", src) };
        var (done, cs, err) = Watchdog.Run(() => AliceCompiler.TranspileAll(files), CompileMs);
        if (!done) return new Finding("compile-hang", "TranspileAll did not return within " + CompileMs + " ms", src);
        if (err is CompileException ce) { var cat = "rejected:" + string.Join(",", ce.Diagnostics.Select(d => d.Code).Distinct().Order()); return new Finding(cat, ce.Message, src) { Key = Finding.KeyOf(cat, ce) }; }
        if (err != null) return new Finding("compile-crash:" + Sig(err), Brief(err), src);

        var (eDone, image, eErr) = Watchdog.Run(() => AliceCompiler.Emit(cs!, "fuzz", exe: true), CompileMs);
        if (!eDone) return new Finding("emit-hang", "Roslyn emit did not return", src);
        if (eErr is CompileException re) return new Finding("roslyn-reject", re.Message + "\n--- C#:\n" + cs![0], src) { Key = Finding.KeyOf("roslyn-reject", re) };
        if (eErr != null) return new Finding("emit-crash:" + Sig(eErr), Brief(eErr), src);
        compiled = new Compiled(cs!, image!);

        // determinism: `alice check` / `alice build` twice
        var (aDone, cs2, aErr) = Watchdog.Run(() => AliceCompiler.TranspileAll(files), CompileMs);
        if (aDone && aErr == null && !cs!.SequenceEqual(cs2!))
            return new Finding("nondeterministic-codegen", FirstLineDiff(cs![0], cs2![0]), src);
        var d1 = AliceCompiler.Check(cs!).Select(d => d.ToString()).ToList();
        var d2 = AliceCompiler.Check(cs!).Select(d => d.ToString()).ToList();
        if (d1.Count > 0) return new Finding("check-disagrees-with-emit", string.Join("\n", d1), src);
        if (!d1.SequenceEqual(d2)) return new Finding("check-not-idempotent", string.Join("\n", d1) + "\n---\n" + string.Join("\n", d2), src);

        if (strict)
        {
            var (sDone, scs, sErr) = Watchdog.Run(() => AliceCompiler.TranspileStrict(files), CompileMs);
            if (!sDone) return new Finding("strict-hang", "TranspileStrict did not return", src);
            if (sErr is CompileException se) { var cat = "strict-rejects:" + string.Join(",", se.Diagnostics.Select(d => d.Code).Distinct().Order()); return new Finding(cat, se.Message, src) { Key = Finding.KeyOf(cat, se) }; }
            if (sErr != null) return new Finding("strict-crash:" + Sig(sErr), Brief(sErr), src);
            if (!scs!.SequenceEqual(cs!))
            {
                // strict typing may only reject programs, never change what an accepted one does
                var img = compiled.Image;
                var a = Execute(() => img);
                var b = Execute(() => AliceCompiler.Emit(scs!, "fuzzstrict", exe: true));
                if (a.Hung || b.Hung) return null;
                if (a.Output != b.Output) return new Finding("strict-diverges", "default:\n" + a.Output + "\nstrict:\n" + b.Output, src);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ execution

    /// <summary>What a run printed, plus the escaping exception (as `&lt;raised Type: message&gt;`).</summary>
    public sealed record RunResult(string Output, bool Hung);

    static readonly object ConsoleLock = new();

    /// <summary>Loads and runs an image in-process (Console.Out redirected), with the run watchdog.</summary>
    public static RunResult Execute(Func<byte[]> image)
    {
        lock (ConsoleLock)
        {
            var (done, outp, err) = Watchdog.Run(() =>
            {
                var asm = AliceCompiler.Load(image());
                var entry = asm.EntryPoint!;
                var old = Console.Out;
                var sw = new StringWriter();
                Console.SetOut(sw);
                string tail = "";
                try { entry.Invoke(null, [Array.Empty<string>()]); }
                catch (TargetInvocationException e) when (e.InnerException != null) { tail = "<raised " + e.InnerException.GetType().FullName + ": " + e.InnerException.Message + ">"; }
                finally { Console.SetOut(old); }
                return sw + tail;
            }, RunMs);
            if (!done) return new RunResult("<hang>", true);
            return new RunResult(err == null ? outp! : "<harness " + err.GetType().Name + ": " + err.Message + ">", false);
        }
    }

    public sealed class DiffStats
    {
        public int Programs, IlUnsupported, Compared, Hangs;
        /// <summary>Outputs of the last compared program (C# backend, IL backend).</summary>
        public string? LastCs, LastIl;
        /// <summary>Unsupported node kinds of the last program the IL backend could not compile (names and lines dropped).</summary>
        public List<string> LastUnsupported = [];
        public readonly SortedDictionary<string, int> Unsupported = new(StringComparer.Ordinal);
    }

    /// <summary>C# backend vs IL backend, in-process. Unsupported IL nodes are counted (not a finding).</summary>
    public static Finding? CheckDifferential(string src, Compiled cs, DiffStats stats, bool verify = true)
    {
        stats.Programs++;
        var (done, res, err) = Watchdog.Run(() => AirToIl.Compile([new SourceFile("main.al", src)], "fuzzil", exe: true), CompileMs);
        if (!done) return new Finding("il-compile-hang", "AirToIl.Compile did not return", src);
        if (err is CompileException ce) return new Finding("il-rejects", "the C# backend compiled the program, the IL backend rejected it:\n" + ce.Message, src) { Key = Finding.KeyOf("il-rejects", ce) };
        if (err != null) return new Finding("il-crash:" + Sig(err), Brief(err), src);
        if (!res!.Ok)
        {
            stats.IlUnsupported++;
            stats.LastUnsupported = res.Errors.Select(e => UnsupportedKind(e.Message)).Distinct().ToList();
            foreach (var e in res.Errors) { var k = UnsupportedKind(e.Message); stats.Unsupported[k] = stats.Unsupported.GetValueOrDefault(k) + 1; }
            return null;
        }
        if (verify)
        {
            var v = IlVerify(res.Pe!);
            if (v.Count > 0) return new Finding("il-verify", string.Join("\n", v.Take(10)), src);
        }
        var a = Execute(() => cs.Image);
        var b = Execute(() => res.Pe!);
        if (a.Hung || b.Hung)
        {
            stats.Hangs++;
            if (a.Hung != b.Hung) return new Finding(a.Hung ? "hang-cs-only" : "hang-il-only", "cs: " + Short(a.Output) + "\nil: " + Short(b.Output), src);
            return null;
        }
        stats.Compared++;
        stats.LastCs = a.Output;
        stats.LastIl = b.Output;
        if (a.Output != b.Output) return new Finding(Category("diff-il", a.Output, b.Output), "C# backend:\n" + Short(a.Output) + "\nIL backend:\n" + Short(b.Output), src);
        return null;
    }

    /// <summary>`Decl:iterator (D1.8): app0 (:1)` -> `Decl:iterator (D1.8)`: the node kind, without names and lines.</summary>
    public static string UnsupportedKind(string message)
    {
        var first = message.Split((char)10)[0];
        var parts = first.Split(": ");
        var k = parts[0];
        k = System.Text.RegularExpressions.Regex.Replace(k, "[(]main[.]al:[0-9]+[)]|[(]:[0-9]+[)]", "").Trim();
        return k.Length > 80 ? k[..80] : k;
    }

    /// <summary>`diff-X:stdout` when the printed text differs before any raise, `diff-X:raise` when only the escaping
    /// exception differs (the minimizer keeps the kind).</summary>
    public static string Category(string kind, string a, string b)
    {
        static string Body(string s) { int i = s.IndexOf("<raised ", StringComparison.Ordinal); return i < 0 ? s : s[..i]; }
        return kind + (Body(a) == Body(b) ? ":raise" : ":stdout");
    }

    public static string Short(string s) => s.Length > 1500 ? s[..1500] + "..." : s;

    // ------------------------------------------------------------------ ILVerify (as tests/Alice.Tests/Backends/AirToIlTests.cs)

    sealed class Resolver : IResolver
    {
        static readonly string Shared = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        readonly Dictionary<string, PEReader?> cache = [];

        PEReader? Load(string name)
        {
            if (cache.TryGetValue(name, out var r)) return r;
            var path = name == "Alice.Runtime" ? typeof(Alice.Runtime.Prelude).Assembly.Location : Path.Combine(Shared, name + ".dll");
            return cache[name] = File.Exists(path) ? new PEReader(File.OpenRead(path)) : null;
        }

        public PEReader ResolveAssembly(AssemblyNameInfo name) => Load(name.Name)!;
        public PEReader ResolveModule(AssemblyNameInfo name, string module) => null!;
    }

    static Resolver? resolver;

    public static List<string> IlVerify(byte[] pe)
    {
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var verifier = new Verifier(resolver ??= new Resolver(), new VerifierOptions { SanityChecks = true });
        verifier.SetSystemModuleName(new AssemblyNameInfo("System.Private.CoreLib"));
        var errors = new List<string>();
        foreach (var r in verifier.Verify(reader))
        {
            if (r.Code == VerifierError.InitLocals) continue;
            if (r.Code == VerifierError.Unverifiable && !r.Method.IsNil && UsesLocalloc(reader, md.GetMethodDefinition(r.Method))) continue;
            string where = r.Method.IsNil ? "" : md.GetString(md.GetTypeDefinition(md.GetMethodDefinition(r.Method).GetDeclaringType()).Name)
                + "::" + md.GetString(md.GetMethodDefinition(r.Method).Name);
            errors.Add(where + ": " + r.Code + " " + r.Message);
        }
        return errors;
    }

    static bool UsesLocalloc(PEReader pe, MethodDefinition m)
    {
        if (m.RelativeVirtualAddress == 0) return false;
        var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
        for (int i = 0; i + 1 < il.Length; i++) if (il[i] == 0xFE && il[i + 1] == 0x0F) return true;
        return false;
    }

    // ------------------------------------------------------------------ variants (other processes)

    /// <summary>Pass names of the MIR pipeline (ALICE_DISABLE_PASSES, Optimizer/Pipeline.cs).</summary>
    public static readonly string[] Passes =
        ["const-fold", "strength-reduce", "literal-minmax", "loop-tile", "induction", "shift-count", "owned-bounds", "unroll", "branchless-select"];

    /// <summary>Writes a deployable copy of an image (dll + Alice.Runtime + runtimeconfig) for `dotnet x.dll` runs.</summary>
    public static string Deploy(string dir, string name, byte[] image)
    {
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, name + ".dll");
        File.WriteAllBytes(dll, image);
        var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
        var dst = Path.Combine(dir, Path.GetFileName(rt));
        if (!File.Exists(dst)) File.Copy(rt, dst, true);
        File.WriteAllText(Path.Combine(dir, name + ".runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + Environment.Version.Major + ".0.0\"}}}");
        return dll;
    }

    /// <summary>Runs a process; stdout plus `&lt;exit N&gt;` (or `&lt;timeout&gt;` or `&lt;signal N&gt;`).</summary>
    public static string RunProcess(System.Diagnostics.ProcessStartInfo psi, int timeoutMs)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } return "<timeout>"; }
        p.WaitForExit();
        // Exit codes > 128 on Unix indicate termination by signal (128 + signal number)
        if (p.ExitCode > 128) return o.Result + "<signal " + (p.ExitCode - 128) + ">" + FirstLine(e.Result);
        return o.Result + (p.ExitCode != 0 ? "<exit " + p.ExitCode + ">" + FirstLine(e.Result) : "");
    }

    static string FirstLine(string s) { var l = s.Split('\n').FirstOrDefault(x => x.Trim().Length > 0) ?? ""; return l.Length > 200 ? l[..200] : l; }

    /// <summary>An in-process run result in the shape a deployed `dotnet x.dll` run reports (unhandled exception:
    /// exit code + stderr first line differ in form, so both sides are normalized to the stdout part and a flag).</summary>
    public static string NormalizeProcess(string s)
    {
        int i = s.IndexOf("<exit ", StringComparison.Ordinal);
        return i < 0 ? s : s[..i] + "<failed>";
    }

    public static string NormalizeInProcess(string s)
    {
        int i = s.IndexOf("<raised ", StringComparison.Ordinal);
        return i < 0 ? s : s[..i] + "<failed>";
    }

    public static string Describe(IEnumerable<string> lines) => new StringBuilder().AppendJoin('\n', lines).ToString();
}
