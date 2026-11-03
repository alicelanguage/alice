using Alice.Compiler.Syntax;
using Alice.Fuzz;
using Xunit.Abstractions;

namespace Alice.Tests.Fuzz;

/// <summary>
/// Parser fuzzer, short deterministic seed set (the long runs are tools/fuzz, docs/tooling.md "Fuzzing"): generated
/// programs (<see cref="SyntaxGen"/>) and mutated corpus files (<see cref="Mutator"/>) must either parse or be rejected
/// with an AL diagnostic; `alice fmt` never throws, is idempotent, and re-parses to the same AST. Findings that are
/// triaged (<see cref="Known.Issues"/>) are counted, not failed; anything else fails with its minimized program.
/// </summary>
public class ParserFuzzTests(ITestOutputHelper output)
{
    internal static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    /// <summary>Fails on the first unknown finding, after shrinking it.</summary>
    internal static void Check(Finding? f, Func<string, Finding?> again, Dictionary<string, int> known)
    {
        if (f == null) return;
        if (Known.Match(f) is { } id) { known[id] = known.GetValueOrDefault(id) + 1; return; }
        var min = Minimizer.Minimize(f.Source, c => again(c)?.Key == f.Key, 300);
        Assert.Fail("new fuzz finding (add it to tools/fuzz/findings and Known.Issues once triaged):\n" + (f with { Source = min }).ToFile());
    }

    [Fact]
    public void Generated_programs_parse_or_are_rejected_and_format_round_trips()
    {
        var known = new Dictionary<string, int>();
        int ok = 0, rejected = 0;
        for (int seed = 1; seed <= 400; seed++)
        {
            var (res, f) = Oracles.CheckSyntax(SyntaxGen.Generate(seed));
            if (res == Oracles.ParseResult.Ok) ok++;
            if (res == Oracles.ParseResult.Rejected) rejected++;
            Check(f, c => Oracles.CheckSyntax(c).Finding, known);
        }
        output.WriteLine($"ok {ok}, rejected {rejected}, known {string.Join(", ", known.Select(kv => kv.Key + " x" + kv.Value))}");
        Assert.True(ok >= 40, $"the generator should produce parseable programs (ok {ok} of 400)");
    }

    [Fact]
    public void Mutated_corpus_files_never_crash_the_parser()
    {
        var corpus = Mutator.Corpus(Root(), "examples", "benchmarks");
        Assert.NotEmpty(corpus);
        var r = new Rng(7);
        var known = new Dictionary<string, int>();
        for (int i = 0; i < 400; i++)
        {
            var src = Mutator.Mutate(corpus[r.Next(corpus.Count)].Text, r);
            Check(Oracles.CheckSyntax(src).Finding, c => Oracles.CheckSyntax(c).Finding, known);
        }
        output.WriteLine("known: " + string.Join(", ", known.Select(kv => kv.Key + " x" + kv.Value)));
    }

    [Fact]
    public void Corpus_files_format_idempotently_to_the_same_ast()
    {
        var known = new Dictionary<string, int>();
        foreach (var (path, text) in Mutator.Corpus(Root(), "examples", "benchmarks", "samples"))
        {
            var (res, f) = Oracles.CheckSyntax(text);
            if (f != null && Known.Match(f) == null) Assert.Fail(Path.GetFileName(path) + ": " + f.Category + "\n" + f.Detail);
            Check(f, c => Oracles.CheckSyntax(c).Finding, known);
            _ = res;
        }
    }

    [Fact]
    public void Ast_dump_ignores_layout_but_not_content()
    {
        string Dump(string s) => AstDump.Of(Parser.ParseFile(s, "fuzz.al"));
        Assert.Equal(Dump("x = 1\nif x > 0\n  puts x\nend\n"), Dump("\n\nx  =  1\nif x > 0\n      puts x   # c\nend\n"));
        Assert.NotEqual(Dump("x = 1\n"), Dump("x = 2\n"));
        Assert.NotEqual(Dump("puts \"a b\"\n"), Dump("puts \"a  b\"\n"));
    }

    [Fact]
    public void Generators_are_deterministic()
    {
        Assert.Equal(SyntaxGen.Generate(42), SyntaxGen.Generate(42));
        Assert.Equal(TypedGen.Generate(42), TypedGen.Generate(42));
        Assert.NotEqual(TypedGen.Generate(42), TypedGen.Generate(43));
    }
}
