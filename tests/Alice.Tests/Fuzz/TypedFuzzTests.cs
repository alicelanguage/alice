using Alice.Fuzz;
using Xunit.Abstractions;

namespace Alice.Tests.Fuzz;

/// <summary>
/// Binder/lowering fuzzer, short deterministic seed set: well-typed generated programs (<see cref="TypedGen"/>:
/// numeric kernels, arrays, lists, hashes, records, sum types, affine enums, blocks, `par for`, `par a, b =`, owned
/// arrays, pure/safe defs, classes, strings, rescue) compile without an internal error, the emitted C# compiles under
/// Roslyn, compiling and `alice check` are deterministic, and strict mode accepts them. Triaged findings
/// (<see cref="Known.Issues"/>) are counted; the program set alternates the avoid-known mode, so both stay covered.
/// </summary>
[Collection("console")]
public class TypedFuzzTests(ITestOutputHelper output)
{
    [Fact]
    public void Generated_typed_programs_compile_check_and_strict()
    {
        var known = new Dictionary<string, int>();
        int compiled = 0;
        for (int seed = 1; seed <= 24; seed++)
        {
            var src = new TypedGen(new Rng(seed), avoidKnown: seed % 2 == 0).Program();
            var f = Oracles.CheckTyped(src, out var c);
            if (c != null) compiled++;
            ParserFuzzTests.Check(f, s => ShrinkTyping.WellTyped(s) && Shrink.Terminates(s) ? Oracles.CheckTyped(s, out _) : null, known);
        }
        output.WriteLine($"compiled {compiled} of 24; known {string.Join(", ", known.Select(kv => kv.Key + " x" + kv.Value))}");
        Assert.True(compiled >= 12, $"most generated programs should compile (compiled {compiled} of 24)");
    }

    [Fact]
    public void Avoid_known_mode_compiles_every_program()
    {
        // the differential fuzzer relies on it: a program that does not compile is not compared
        for (int seed = 100; seed < 112; seed++)
        {
            var src = new TypedGen(new Rng(seed), avoidKnown: true).Program();
            var f = Oracles.CheckTyped(src, out _, strict: false);
            Assert.True(f == null || Known.Match(f) != null, f?.ToFile());
        }
    }
}
