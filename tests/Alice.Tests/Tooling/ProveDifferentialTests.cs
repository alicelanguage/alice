using System.Text;
using System.Text.RegularExpressions;
using Alice.Cli.Commands;
using Alice.Cli.Proof;
using Alice.Compiler;
using Xunit.Abstractions;

namespace Alice.Tests.Tooling;

/// <summary>
/// Differential fuzz of the two provers: random straight-line integer rules (locals, early `return true if`, checked and
/// wrapping arithmetic, floored `/` `%`, `abs`, `Math.Min/Max`, ternaries, `to_X` / `to_X!` conversions, domains at the
/// Int32 limits). Enumeration and --smt must agree on PROVEN / FAILS for every rule, and every counterexample either
/// engine reports is replayed through the compiled rule, which must return false or raise on it.
/// ALICE_PROVE_FUZZ_SEED=n runs another batch of rules locally.
/// </summary>
[Collection("console")]
public class ProveDifferentialTests(ITestOutputHelper output)
{
    const int Rules = 30;

    /// <summary>Generates rules whose every subexpression is Int32 (conversions come back to Int32).</summary>
    sealed class Gen(int seed)
    {
        readonly Random r = new(seed);
        List<string> ints = [], bools = [];

        T Pick<T>(params T[] xs) => xs[r.Next(xs.Length)];

        string Leaf() => r.Next(10) switch
        {
            < 6 => Pick(ints.ToArray()),
            6 => Pick("2147483647", "(-2147483648)"),   // literals: `Int32::MAX` in a rule is AL0210 today
            _ => Pick("0", "1", "2", "3", "7", "100", "65536", "46341"),
        };

        /// <summary>An Int32 expression. <paramref name="wrap"/> is kept for fuzzing wrap-free operands on purpose;
        /// the generator no longer needs it (see <see cref="Wrapping_ops_keep_their_operands_checked"/>).</summary>
        public string Int(int depth, bool wrap = false)
        {
            if (depth <= 0 || r.Next(4) == 0) return Leaf();
            int d = depth - 1;
            string op = wrap ? Pick("/", "%", "&+", "&-", "&*", "&", "|", "^") : Pick("+", "-", "*", "/", "%", "&+", "&-", "&*", "&", "|", "^");
            bool inner = wrap;
            string e = r.Next(12) switch
            {
                < 5 => $"({Int(d, inner)} {op} {Int(d, inner)})",
                5 when !wrap => $"(-{Int(d)})",
                6 => $"{Int(d, wrap)}.abs",
                7 => $"Math.{Pick("Min", "Max")}({Int(d, wrap)}, {Int(d, wrap)})",
                8 => $"({Cond(d, wrap)} ? {Int(d, wrap)} : {Int(d, wrap)})",
                9 => $"{Paren(Int(d, wrap))}.{Pick("to_i8", "to_i16", "to_u8", "to_u16")}.to_i32",
                10 => $"{Paren(Int(d, wrap))}.{Pick("to_i8!", "to_i16!", "to_u8!", "to_u16!")}.to_i32",
                _ when wrap => $"({Paren(Int(d, true))}.to_i64 &* {Paren(Int(d, true))}.to_i64).{Pick("to_i32", "to_i32!")}",
                _ => $"({Paren(Int(d))}.to_i64 {Pick("+", "*", "-", "&*")} {Paren(Int(d))}.to_i64).{Pick("to_i32", "to_i32!")}",
            };
            // literals only would be a C# constant, and an overflowing constant is a compile error (AL0300)
            return Regex.IsMatch(e, @"\b([a-c]|t[0-9])\b") ? e : Pick(ints.ToArray());
        }

        static string Paren(string e) => e.StartsWith('(') ? e : $"({e})";

        public string Cond(int depth, bool wrap = false)
        {
            if (bools.Count > 0 && r.Next(5) == 0) return Pick(bools.ToArray());
            if (depth > 0 && r.Next(6) == 0) return $"({Cond(depth - 1, wrap)} {Pick("&&", "||")} {Cond(depth - 1, wrap)})";
            return $"({Int(depth, wrap)} {Pick("<", "<=", ">", ">=", "==", "!=")} {Int(depth, wrap)})";
        }

        /// <summary>A final Bool that often holds (an identity) or often fails (a random comparison).</summary>
        string Final()
        {
            string a = Int(2), b = Int(2), wa = Int(2), wb = Int(2);
            return r.Next(6) switch
            {
                0 => $"{a} == {a}",
                1 => $"Math.Min({a}, {b}) <= Math.Max({a}, {b})",
                2 => $"(({wa} &+ {wb}) &- {wb}) == {wa}",
                3 => $"({a} ^ {b}) ^ {b} == {a}",
                _ => Cond(2),
            };
        }

        public string Rule(int n)
        {
            ints = [];
            bools = [];
            var domains = new List<string>();
            long cases = 1;
            int vars = 1 + r.Next(3);
            for (int i = 0; i < vars; i++)
            {
                if (i > 0 && r.Next(5) == 0) { string bn = "f" + i; bools.Add(bn); domains.Add($"{bn} : Bool"); cases *= 2; continue; }
                var (lo, hi) = Pick<(long, long)>((-20, 20), (-7, 7), (0, 60), (-300, 300), (2147483600, 2147483647), (-2147483648, -2147483600), (-1, 1));
                if (cases * (hi - lo + 1) > 400_000) (lo, hi) = (-3, 3);
                cases *= hi - lo + 1;
                string name = ((char)('a' + i)).ToString();
                ints.Add(name);
                domains.Add($"{name} in {(lo == int.MinValue ? "-2147483648" : lo.ToString())}..{hi}");
            }
            var sb = new StringBuilder($"rule r{n}({string.Join(", ", domains)})\n");
            if (r.Next(3) == 0) sb.Append($"  return true if {Cond(1)}\n");
            for (int k = 0, locals = r.Next(3); k < locals; k++)
            {
                sb.Append($"  t{k} = {Int(2)}\n");
                ints.Add("t" + k);
            }
            sb.Append($"  {Final()}\nend\n");
            return sb.ToString();
        }
    }

    /// <summary>Alice source that calls `rule_rN` on each input and prints whether it holds (`true`) or not.</summary>
    static string Replays(IEnumerable<(string Rule, string Cex)> cases)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var (rule, cex) in cases)
        {
            string args = string.Join(", ", Prove.Assignments(cex).Select(a => a.Value is long n ? n.ToString() : a.Value.ToString()!.ToLowerInvariant()));
            sb.Append($"def __replay_{i} : Bool\n  begin\n    rule_{rule}({args})\n  rescue e\n    false\n  end\nend\nputs \"{rule} #{{__replay_{i}}}\"\n");
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The first divergence the fuzz found (seed 1): `&+ &- &*` emitted C# `unchecked(l op r)`, which also wrapped the
    /// checked arithmetic inside l and r, so enumeration "proved" this rule and Z3 refuted it. ADR 0013: the inner
    /// `*` and `-` trap; operands with such arithmetic are now `checked(...)` (MirFacts.KeepChecked).
    /// </summary>
    [Fact]
    public void Wrapping_ops_keep_their_operands_checked()
    {
        const string Rule = "rule inner_mul_traps(b in 65530..65536, c in 65530..65536)\n  (1 &* (b * c)) == (1 &* (b * c))\nend\n";
        var e = Assert.Single(Prove.Check([new SourceFile("RULES.al", Rule)], []));
        Assert.Equal("b = 65530, c = 65530", e.Counterexample);   // 65530 * 65530 > Int32::MAX
        Assert.Equal("raised OverflowException", e.Outcome);
        if (!ProveZ3.Available()) return;
        Assert.False(Assert.Single(SmtProver.Prove([new SourceFile("RULES.al", Rule)], [])).Proved);
    }

    [Theory]
    [InlineData("def g(b : Int32, c : Int32) : Int32\n  1 &* (b * c)\nend\nputs g(65536, 65536)\n")]
    [InlineData("def h(b : Int32) : Int32\n  1 &+ (-b)\nend\nputs h(-2147483648)\n")]
    [InlineData("def k(a : Int32, b : Int32) : Int64\n  (a.to_i64 * b.to_i64 * 4_i64) &+ 1_i64\nend\nputs k(2147483647, 2147483647)\n")]
    public void Checked_arithmetic_inside_a_wrapping_operand_still_traps(string src)
    {
        var e = Assert.ThrowsAny<Exception>(() => AliceCompiler.Run(src));
        Assert.Contains("Overflow", (e.InnerException ?? e).GetType().Name + e.Message);
    }

    [Fact]
    public void The_wrapping_operator_itself_still_wraps()
    {
        Assert.Equal("-2147483648", AliceCompiler.Run("def w(a : Int32) : Int32\n  (a + 0) &+ (a - a + 1)\nend\nputs w(2147483647)\n").Trim());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Enumeration_and_smt_agree_on_random_rules(int batch)
    {
        if (!ProveZ3.Available()) return;
        int seed = Environment.GetEnvironmentVariable("ALICE_PROVE_FUZZ_SEED") is { } s && int.TryParse(s, out var n) ? n * 1000 + batch : batch;
        var gen = new Gen(seed);
        var rules = Enumerable.Range(0, Rules).Select(gen.Rule).ToList();
        var files = new List<SourceFile> { new("RULES.al", string.Concat(rules)) };
        if (Environment.GetEnvironmentVariable("ALICE_PROVE_FUZZ_DUMP") is { Length: > 0 } dump) File.WriteAllText(Path.Combine(dump, $"RULES-{seed}.al"), files[0].Text);

        var enumerated = Prove.Check(files, []).ToDictionary(x => x.Rule);
        var smt = SmtProver.Prove(files, []).ToDictionary(x => x.Rule);
        var divergences = new List<string>();
        for (int i = 0; i < Rules; i++)
        {
            var e = enumerated["r" + i];
            var z = smt["r" + i];
            if (z.Untranslatable != null) divergences.Add($"r{i}: --smt cannot translate it ({z.Untranslatable})\n{rules[i]}");
            else if (z.Proved != (e.Counterexample == null))
                divergences.Add($"r{i}: enumeration {(e.Counterexample == null ? "PROVEN" : "FAILS at " + e.Counterexample + " -> " + e.Outcome)}, smt {(z.Proved ? "PROVEN" : "FAILS at " + z.Counterexample)}\n{rules[i]}");
        }

        // every counterexample, from either engine, must fail the compiled rule when it is run again
        var cexs = enumerated.Values.Where(x => x.Counterexample != null).Select(x => (x.Rule, x.Counterexample!))
            .Concat(smt.Values.Where(x => x.Counterexample != null).Select(x => (x.Rule, x.Counterexample!))).ToList();
        if (cexs.Count > 0)
        {
            Alice.Compiler.Backend.Emitter.SkipOversizedRules = true;
            string ran;
            try { ran = AliceCompiler.Run(string.Concat(rules) + Replays(cexs)); }
            finally { Alice.Compiler.Backend.Emitter.SkipOversizedRules = false; }
            var lines = ran.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(cexs.Count, lines.Length);
            for (int i = 0; i < cexs.Count; i++)
                if (lines[i].Trim() != cexs[i].Rule + " false")
                    divergences.Add($"{cexs[i].Rule}: counterexample {cexs[i].Item2} does not fail the compiled rule\n{rules[int.Parse(cexs[i].Rule[1..])]}");
        }

        int proven = enumerated.Values.Count(x => x.Counterexample == null);
        output.WriteLine($"seed {seed}: {Rules} rules, {proven} proven, {Rules - proven} fail, {cexs.Count} counterexamples replayed, {divergences.Count} divergences");
        Assert.True(divergences.Count == 0, string.Join("\n", divergences));
        Assert.InRange(proven, 1, Rules - 1);   // the batch exercises both verdicts
    }
}
