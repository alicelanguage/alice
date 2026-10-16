using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Alice.Runtime;

public enum TestStatus { Passed, Failed, Pending, Skipped }

public sealed record TestResult(
    IReadOnlyList<string> Path, string Name, string File, int Line, TestStatus Status, TimeSpan Duration,
    string? Message = null, string? Expected = null, string? Actual = null, string? Stack = null)
{
    public string FullName => string.Join(" ", Path.Append(Name));
}

public sealed class RunOptions
{
    /// <summary>Run tests whose full name contains any of these (case-insensitive).</summary>
    public List<string> Filters = new();
    /// <summary>Run only examples declared at these source lines (`file.al:12`), or groups containing them.</summary>
    public List<int> Lines = new();
    /// <summary>Shuffle order with this seed (null = declaration order).</summary>
    public int? Seed;
    public bool FailFast;
}

/// <summary>
/// Discovers and runs RSpec-style examples (registered by running the file's top-level code), xUnit-style
/// `@[Test]`/`@[Cases]` methods (fresh instance per test, setup/teardown) and legacy `test_*` functions.
/// Reporting is the caller's job (CLI renders, tests assert on results).
/// </summary>
public static class TestRunner
{
    sealed record Case(IReadOnlyList<string> Path, string Name, string File, int Line, bool Focus, string? Skip, Action Run, SpecExample? Ex);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test discovery is reflection-based by design.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test discovery is reflection-based by design.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test discovery is reflection-based by design.")]
    public static List<TestResult> Run(Assembly asm, RunOptions opts, Action<TestResult>? onResult = null)
    {
        Specs.Reset();
        var main = asm.GetType("Program")?.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        main?.Invoke(null, [Array.Empty<string>()]);    // top-level code registers describe/it

        var cases = new List<Case>();
        Collect(Specs.Root, new List<string>(), cases, false);
        foreach (var t in asm.GetTypes()) Discover(t, cases);

        if (cases.Any(c => c.Focus)) cases = cases.Where(c => c.Focus).ToList();
        if (opts.Filters.Count > 0) cases = cases.Where(c => opts.Filters.Any(f => string.Join(" ", c.Path.Append(c.Name)).Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (opts.Lines.Count > 0) cases = cases.Where(c => opts.Lines.Contains(c.Line) || (c.Ex != null && opts.Lines.Any(l => InGroupAt(c.Ex.Group, l)))).ToList();
        if (opts.Seed is int seed) cases = cases.OrderBy(_ => 0).ToList().Shuffled(seed);

        var results = new List<TestResult>();
        var ranAll = new HashSet<SpecGroup>();
        foreach (var c in cases)
        {
            var r = Execute(c, ranAll);
            results.Add(r);
            onResult?.Invoke(r);
            if (opts.FailFast && r.Status == TestStatus.Failed) break;
        }
        foreach (var g in ranAll.Reverse())
            foreach (var h in g.AfterAll) try { h(); } catch { /* reported by the examples */ }
        return results;
    }

    /// <summary>`file.al:N` where N is a describe/context line selects everything inside it.</summary>
    static bool InGroupAt(SpecGroup g, int line)
    {
        for (var x = g; x != null; x = x.Parent) if (x.Line == line) return true;
        return false;
    }

    static List<Case> Shuffled(this List<Case> xs, int seed)
    {
        var rng = new Random(seed);
        for (int i = xs.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (xs[i], xs[j]) = (xs[j], xs[i]); }
        return xs;
    }

    static void Collect(SpecGroup g, List<string> path, List<Case> into, bool focus)
    {
        foreach (var ch in g.Children)
        {
            if (ch is SpecGroup sg)
            {
                path.Add(sg.Name);
                Collect(sg, path, into, focus || sg.Focus);
                path.RemoveAt(path.Count - 1);
            }
            else if (ch is SpecExample ex)
            {
                string? skip = ex.Body == null ? "pending: " + ex.Reason : ex.Skip ? ex.Reason : SkippedGroup(ex.Group);
                into.Add(new Case(path.ToList(), ex.Name, ex.File, ex.Line, focus || ex.Focus, skip, ex.Body ?? (() => { }), ex));
            }
        }
    }

    static string? SkippedGroup(SpecGroup g)
    {
        for (var x = g; x != null; x = x.Parent) if (x.Skip) return "skipped with xdescribe";
        return null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Test discovery is reflection-based by design.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Test discovery is reflection-based by design.")]
    static void Discover(Type t, List<Case> into)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var m in t.GetMethods(all).OrderBy(m => m.MetadataToken))
        {
            var test = m.GetCustomAttribute<TestAttribute>();
            var rows = m.GetCustomAttributes<CasesAttribute>().ToList();
            bool legacy = t.Name == "Program" && m.IsStatic && m.Name.StartsWith("test_") && m.GetParameters().Length == 0;
            if (test == null && rows.Count == 0 && !legacy) continue;
            string name = test?.Name ?? m.Name.Replace('_', ' ');
            var path = t.Name == "Program" ? new List<string>() : new List<string> { t.Name };
            if (rows.Count == 0) into.Add(new Case(path, name, "", 0, false, test?.Skip, () => Invoke(t, m, []), null));
            foreach (var row in rows)
                into.Add(new Case(path, $"{name}({string.Join(", ", row.Args.Select(Prelude.inspect))})", "", 0, false, test?.Skip, () => Invoke(t, m, row.Args), null));
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Test discovery is reflection-based by design.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Test discovery is reflection-based by design.")]
    static void Invoke(Type t, MethodInfo m, object?[] args)
    {
        var ps = m.GetParameters();
        if (ps.Length != args.Length) throw new ArgumentException($"{m.Name} takes {ps.Length} argument(s), the case has {args.Length}");
        var conv = args.Select((a, i) => a == null || ps[i].ParameterType.IsInstanceOfType(a) ? a
            : Convert.ChangeType(a, ps[i].ParameterType, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        object? self = m.IsStatic ? null : Activator.CreateInstance(t);   // fresh instance per test (isolation)
        var setup = m.IsStatic ? null : t.GetMethod("setup", Type.EmptyTypes);
        var teardown = m.IsStatic ? null : t.GetMethod("teardown", Type.EmptyTypes);
        try
        {
            setup?.Invoke(self, null);
            var r = m.Invoke(self, conv);
            if (r is Task task) task.GetAwaiter().GetResult();
        }
        finally { teardown?.Invoke(self, null); }
    }

    static TestResult Execute(Case c, HashSet<SpecGroup> ranAll)
    {
        if (c.Skip != null)
            return new TestResult(c.Path, c.Name, c.File, c.Line, c.Skip.StartsWith("pending") ? TestStatus.Pending : TestStatus.Skipped, TimeSpan.Zero, c.Skip);
        var sw = Stopwatch.StartNew();
        var chain = new List<SpecGroup>();
        for (var g = c.Ex?.Group; g != null; g = g.Parent) chain.Insert(0, g);
        try
        {
            foreach (var g in chain)
                if (ranAll.Add(g)) foreach (var h in g.BeforeAll) h();
            try
            {
                foreach (var g in chain) foreach (var h in g.Before) h();
                c.Run();
            }
            finally
            {
                for (int i = chain.Count - 1; i >= 0; i--) foreach (var h in chain[i].After) h();
            }
            return new TestResult(c.Path, c.Name, c.File, c.Line, TestStatus.Passed, sw.Elapsed);
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException { InnerException: { } ie }) e = ie;
            return e switch
            {
                PendingException p => new TestResult(c.Path, c.Name, c.File, c.Line, TestStatus.Pending, sw.Elapsed, "pending: " + p.Message),
                SkipException s => new TestResult(c.Path, c.Name, c.File, c.Line, TestStatus.Skipped, sw.Elapsed, s.Message),
                ExpectationFailed f => new TestResult(c.Path, c.Name, c.File, c.Line, TestStatus.Failed, sw.Elapsed, f.Message, f.Expected, f.Actual, Where(f)),
                _ => new TestResult(c.Path, c.Name, c.File, c.Line, TestStatus.Failed, sw.Elapsed, $"{e.GetType().Name}: {e.Message}", Stack: Where(e)),
            };
        }
    }

    /// <summary>First Alice-source frame (.al) of the stack, for "at file.al:12".</summary>
    static string? Where(Exception e)
    {
        var frames = new StackTrace(e, true).GetFrames();
        var f = frames.FirstOrDefault(x => x.GetFileName()?.EndsWith(".al") == true);
        return f == null ? null : $"{f.GetFileName()}:{f.GetFileLineNumber()}";
    }
}
