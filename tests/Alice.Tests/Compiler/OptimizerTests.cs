using Alice.Compiler;
using Alice.Compiler.Backend;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;
using Alice.Compiler.Optimizer.Passes;

namespace Alice.Tests.Compiler;

/// <summary>MIR pass pipeline (ADR 0014): each primitive pass on MIR, then end to end through the compiler.</summary>
[Collection("console")]
public class OptimizerTests
{
    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    static MCall Prelude(string name, MExpr a, MExpr b) => new() { Target = new MCode("Prelude"), Name = name, Args = { a, b } };

    static string After(IMirPass pass, MExpr e)
    {
        var body = new List<MStmt> { new MReturn { Value = e } };
        pass.Run(body);
        return Printer.E(((MReturn)body[0]).Value!);
    }

    [Theory]
    [InlineData("+", "2", "3", "5")]
    [InlineData("*", "60", "60", "3600")]
    [InlineData("<<", "1", "4", "16")]
    [InlineData("-", "5L", "7L", "-2L")]
    [InlineData("*", "2147483647", "2", "2147483647 * 2")]   // would overflow Int32: left for Roslyn to report
    [InlineData("+", "x", "1", "x + 1")]
    public void Constant_folding(string op, string l, string r, string expected)
        => Assert.Equal(expected, After(new ConstantFolding(), new MBinary { Op = op, L = new MCode(l), R = new MCode(r) }));

    [Theory]
    [InlineData("floor_div", "-7", "2", "-4")]
    [InlineData("floor_mod", "-7", "3", "2")]
    [InlineData("floor_mod", "7", "-3", "-2")]
    public void Floored_helpers_fold_on_literals(string fn, string a, string b, string expected)
        => Assert.Equal(expected, After(new ConstantFolding(), Prelude(fn, new MCode(a), new MCode(b))));

    [Theory]
    [InlineData("floor_div", "8", "x >> 3")]
    [InlineData("floor_mod", "16", "x & 15")]
    [InlineData("floor_mod", "16L", "x & 15L")]
    [InlineData("floor_div", "1", "x >> 0")]
    [InlineData("floor_div", "6", "Prelude.floor_div_pos(x, 6)")]
    [InlineData("floor_mod", "-4", "Prelude.floor_mod(x, -4)")]
    public void Strength_reduction_of_power_of_two_divisors(string fn, string d, string expected)
        => Assert.Equal(expected, After(new StrengthReduction(), Prelude(fn, new MCode("x"), new MCode(d))));

    [Fact]
    public void Literal_min_becomes_a_math_min_chain()
    {
        var e = new MCall { Name = "min", Target = new MNewArray { ElemCs = "int", Items = [new MCode("a"), new MCode("b"), new MCode("7")] } };
        Assert.Equal("global::System.Math.Min(global::System.Math.Min(((int)a), ((int)b)), 7)", After(new LiteralMinMax(), e));
    }

    [Theory]
    [InlineData("int", "<", false, " = unchecked(i + 1)")]
    [InlineData("long", "<", false, " = unchecked(i + 1)")]
    [InlineData("int", "<=", false, "++")]    // i <= MAX: i + 1 can overflow
    [InlineData("int", "<", true, "++")]      // the body writes i
    [InlineData(null, "<", false, "++")]      // `var` counter: its type may be narrower than the bound
    public void Induction_step_is_unchecked_only_when_provable(string? type, string cmp, bool bodyWrites, string step)
    {
        var f = new MFor { Var = "i", VarType = type, Start = new MCode("0"), Cmp = cmp, End = new MCode("n") };
        if (bodyWrites) f.Body.Add(new MExprStmt { E = new MUnary { Op = "++", E = new MCode("i"), Postfix = true } });
        new InductionVariables().Run([f]);
        Assert.Equal(step, f.Step);
    }

    [Theory]
    [InlineData("x = -7\nputs x / 2\nputs x % 4\nputs x / 1\nputs x % 1", "-4\n1\n-7\n0")]
    [InlineData("y = -9_i64\nputs y / 4_i64\nputs y % 8_i64", "-3\n7")]
    [InlineData("puts(-17 / 4)\nputs(-17 % 4)\nputs((1 << 4) / 3)", "-5\n3\n5")]
    [InlineData("x = 2.5\nputs x % 2", "0.5")]   // float modulo is floor_fmod: never masked
    public void Rewrites_keep_floored_semantics_end_to_end(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Fact]
    public void Power_of_two_division_compiles_to_a_shift()
    {
        var cs = AliceCompiler.Transpile("def f(x : Int32) : Int32\n  x / 8 + x % 16\nend\n");
        Assert.Contains("x >> 3", cs);
        Assert.Contains("x & 15", cs);
        Assert.DoesNotContain("floor_", cs);
    }

    static MFor Loop(string start, string cmp, MExpr end, params MStmt[] body)
    {
        var f = new MFor { Var = "j", VarType = "int", Start = new MCode(start), Cmp = cmp, End = end };
        f.Body.AddRange(body);
        return f;
    }

    static MStmt Use(string op, string k) => new MExprStmt { E = new MCall { Name = "use", Args = { new MBinary { Op = op, L = new MCode("j"), R = new MCode(k) } } } };

    static MExpr Length => new MMember { Target = new MCode("b"), Name = "Length" };

    [Fact]
    public void Inclusive_loop_to_a_length_steps_unchecked()
    {
        var f = Loop("1", "<=", Length);
        new InductionVariables().Run([f]);
        Assert.Equal(" = unchecked(j + 1)", f.Step);   // j <= Length <= Array.MaxLength < Int32::MAX
    }

    [Theory]
    [InlineData("1", "<=", "-", "1", true)]     // j >= 1: j - 1 >= 0
    [InlineData("1", "<=", "+", "1", true)]     // j <= Length: j + 1 fits
    [InlineData("0", "<", "-", "1", true)]      // j >= 0: j - 1 >= -1
    [InlineData("n", "<", "-", "1", false)]     // unknown start: j - 1 may underflow
    [InlineData("1", "<=", "+", "100", false)]  // Length + 100 may pass Int32::MAX
    public void Counter_offsets_are_unchecked_only_inside_their_proven_range(string start, string cmp, string op, string k, bool proven)
    {
        var f = Loop(start, cmp, Length, Use(op, k));
        new InductionVariables().Run([f]);
        string text = Printer.E(((MExprStmt)f.Body[0]).E);
        Assert.Equal(proven, text.Contains($"unchecked(j {op} {k})"));
    }

    [Fact]
    public void Offsets_of_a_written_counter_stay_checked()
    {
        var f = Loop("1", "<=", Length, Use("-", "1"), new MExprStmt { E = new MUnary { Op = "++", E = new MCode("j"), Postfix = true } });
        new InductionVariables().Run([f]);
        Assert.DoesNotContain("unchecked", Printer.E(((MExprStmt)f.Body[0]).E));
    }

    [Theory]
    [InlineData("a = \"kitten\"\nb = \"sitting\"\nprev = Array(Int32).new(b.size + 1)\nfor j in 0..b.size\n  prev[j] = j\nend\nfor i in 1..a.size\n  row = Array(Int32).new(b.size + 1)\n  row[0] = i\n  for j in 1..b.size\n    cost = a[i - 1] == b[j - 1] ? 0 : 1\n    row[j] = [row[j - 1] + 1, prev[j] + 1, prev[j - 1] + cost].min\n  end\n  prev = row\nend\nputs prev[b.size]", "3")]
    [InlineData("xs = [1, 2, 3]\ns = 0\nfor i in 1...xs.size\n  s += xs[i] - xs[i - 1]\nend\nputs s", "2")]
    public void Range_proven_loops_compute_the_same_results(string src, string expected)
        => Assert.Equal(expected, Run(src));

    [Fact]
    public void Floored_ops_by_a_positive_literal_are_branch_free()
    {
        Assert.Equal("Prelude.floor_mod_pos(x, 400)", After(new StrengthReduction(), Prelude("floor_mod", new MCode("x"), new MCode("400"))));
        Assert.Equal("Prelude.floor_div_pos(x, 10L)", After(new StrengthReduction(), Prelude("floor_div", new MCode("x"), new MCode("10L"))));
        Assert.Equal("Prelude.floor_mod(x, -3)", After(new StrengthReduction(), Prelude("floor_mod", new MCode("x"), new MCode("-3"))));
    }

    [Theory]
    [InlineData(-7)] [InlineData(-10)] [InlineData(0)] [InlineData(9)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void Positive_divisor_forms_match_floored_semantics(int a)
    {
        foreach (int d in new[] { 1, 3, 10, 400, int.MaxValue })
        {
            Assert.Equal(Alice.Runtime.Prelude.floor_mod(a, d), Alice.Runtime.Prelude.floor_mod_pos(a, d));
            Assert.Equal(Alice.Runtime.Prelude.floor_div(a, d), Alice.Runtime.Prelude.floor_div_pos(a, d));
            Assert.Equal(Alice.Runtime.Prelude.floor_mod((long)a * 3, (long)d), Alice.Runtime.Prelude.floor_mod_pos((long)a * 3, (long)d));
            Assert.Equal(Alice.Runtime.Prelude.floor_div((long)a * 3, (long)d), Alice.Runtime.Prelude.floor_div_pos((long)a * 3, (long)d));
        }
    }
}
