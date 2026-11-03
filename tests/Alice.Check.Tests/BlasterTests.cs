using System.Numerics;
using Alice.Check;

namespace Alice.Check.Tests;

/// <summary>
/// The bit-blaster against Alice's semantics computed independently (exact integers in C#), at 8 bits exhaustively in
/// the first operand and 16 bits on samples. Each operator's CNF is evaluated by unit propagation with its inputs
/// fixed: `op(x, y) == z` must be refuted for the wrong z and hold for the right one, so the clauses themselves are
/// tested, not just the constant folding.
/// </summary>
public class BlasterTests
{
    static Term Var(uint w, string n) => new(Op.Var, w, [], 0, n);
    static Term Bin(Op op, uint w, Term a, Term b, bool signed = false) => new(op, w, [a, b], signed ? 1 : 0);

    static BigInteger S(ulong v, int w) => (v >> (w - 1)) != 0 ? (BigInteger)v - (BigInteger.One << w) : v;
    static ulong U(BigInteger v, int w) => (ulong)(v & ((BigInteger.One << w) - 1));
    static bool Fits(BigInteger v, int w, bool signed) => signed ? v >= -(BigInteger.One << (w - 1)) && v < BigInteger.One << (w - 1) : v >= 0 && v < BigInteger.One << w;

    static BigInteger FloorDiv(BigInteger a, BigInteger b) { var q = BigInteger.DivRem(a, b, out var r); return r != 0 && (r < 0) != (b < 0) ? q - 1 : q; }

    /// <summary>An operator, its result width (0: Bool) and its meaning on (x, y) as unsigned bit patterns; null where
    /// the value is unspecified (a zero divisor: the rule fails there whatever the value).</summary>
    public static IEnumerable<object[]> Ops()
    {
        object[] B(string name, Op op, bool sg, Func<ulong, ulong, int, ulong?> f) => [name, op, sg, 1, f];
        object[] P(string name, Op op, bool sg, Func<ulong, ulong, int, bool> f) => [name, op, sg, 0, (Func<ulong, ulong, int, ulong?>)((x, y, w) => f(x, y, w) ? 1UL : 0UL)];
        yield return B("add", Op.Add, false, (x, y, w) => U((BigInteger)x + y, w));
        yield return B("sub", Op.Sub, false, (x, y, w) => U((BigInteger)x - y, w));
        yield return B("mul", Op.Mul, false, (x, y, w) => U((BigInteger)x * y, w));
        yield return B("and", Op.BAnd, false, (x, y, w) => x & y);
        yield return B("or", Op.BOr, false, (x, y, w) => x | y);
        yield return B("xor", Op.BXor, false, (x, y, w) => x ^ y);
        yield return B("udiv", Op.UDiv, false, (x, y, w) => y == 0 ? null : x / y);
        yield return B("urem", Op.URem, false, (x, y, w) => y == 0 ? null : x % y);
        yield return B("fdiv", Op.FDiv, true, (x, y, w) => y == 0 ? null : U(FloorDiv(S(x, w), S(y, w)), w));
        yield return B("fmod", Op.FMod, true, (x, y, w) => y == 0 ? null : U(S(x, w) - FloorDiv(S(x, w), S(y, w)) * S(y, w), w));
        yield return P("ult", Op.Ult, false, (x, y, w) => x < y);
        yield return P("ule", Op.Ule, false, (x, y, w) => x <= y);
        yield return P("slt", Op.Slt, false, (x, y, w) => S(x, w) < S(y, w));
        yield return P("sle", Op.Sle, false, (x, y, w) => S(x, w) <= S(y, w));
        yield return P("eq", Op.Eq, false, (x, y, w) => x == y);
        foreach (bool sg in new[] { false, true })
        {
            BigInteger V(ulong v, int w) => sg ? S(v, w) : v;
            yield return P("addovf" + sg, Op.AddOvf, sg, (x, y, w) => !Fits(V(x, w) + V(y, w), w, sg));
            yield return P("subovf" + sg, Op.SubOvf, sg, (x, y, w) => !Fits(V(x, w) - V(y, w), w, sg));
            yield return P("mulovf" + sg, Op.MulOvf, sg, (x, y, w) => !Fits(V(x, w) * V(y, w), w, sg));
        }
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public void Binary_operators_match_alice_semantics(string name, Op op, bool signed, int bv, Func<ulong, ulong, int, ulong?> meaning)
    {
        foreach (int w in new[] { 8, 16 })
        {
            uint rw = bv == 1 ? (uint)w : 0;
            Term x = Var((uint)w, "x"), y = Var((uint)w, "y"), z = Var(rw, "z");
            var r = new Term(op, rw, [x, y], signed ? 1 : 0);
            var eval = new Evaluator(new Goal([], new Term(Op.Bool, 0, [], 0), new Term(Op.Eq, 0, [r, z])));
            foreach (var (a, b) in Inputs(w))
            {
                if (meaning(a, b, w) is not { } want) continue;
                Assert.True(eval.Holds(new() { ["x"] = a, ["y"] = b, ["z"] = want }), $"{name} {w}-bit: {a} {b} -> {want} refuted");
                Assert.False(eval.Holds(new() { ["x"] = a, ["y"] = b, ["z"] = want ^ 1 }), $"{name} {w}-bit: {a} {b} -> {want ^ 1} accepted");
            }
        }
    }

    [Theory]
    [InlineData("neg"), InlineData("not"), InlineData("sext"), InlineData("zext"), InlineData("low"), InlineData("min")]
    public void Unary_operators_and_widths_match_alice_semantics(string name)
    {
        const int w = 8;
        Term x = Var(w, "x"), y = Var(w, "y");
        (Term r, Func<ulong, ulong, ulong> f) = name switch
        {
            "neg" => (new Term(Op.Neg, w, [x]), (a, _) => U(-(BigInteger)a, w)),
            "not" => (new Term(Op.BvNot, w, [x]), (a, _) => ~a & 0xFF),
            "sext" => (new Term(Op.SExt, 16, [x]), (a, _) => U(S(a, w), 16)),
            "zext" => (new Term(Op.ZExt, 16, [x]), (a, _) => a),
            "low" => (new Term(Op.Extract, 3, [x]), (a, _) => a & 7),
            _ => (new Term(Op.Ite, w, [Bin(Op.Slt, 0, x, y), x, y]), (Func<ulong, ulong, ulong>)((a, b) => S(a, w) < S(b, w) ? a : b)),
        };
        var z = Var(r.W, "z");
        var eval = new Evaluator(new Goal([], new Term(Op.Bool, 0, [], 0), new Term(Op.Eq, 0, [r, z])));
        foreach (var (a, b) in Inputs(w))
        {
            ulong want = f(a, b);
            Assert.True(eval.Holds(new() { ["x"] = a, ["y"] = b, ["z"] = want }), $"{name}: {a} {b} -> {want} refuted");
            Assert.False(eval.Holds(new() { ["x"] = a, ["y"] = b, ["z"] = want ^ 1 }), $"{name}: {a} {b} -> {want ^ 1} accepted");
        }
    }

    [Fact]
    public void The_cnf_is_deterministic_and_states_the_negated_goal()
    {
        Term x = Var(8, "x"), one = new(Op.Bv, 8, [], 1);
        var g = new Goal([Bin(Op.Ult, 0, x, one)], Bin(Op.AddOvf, 0, x, one, true), Bin(Op.Eq, 0, x, new Term(Op.Bv, 8, [], 0)));
        Assert.Equal(Blaster.Cnf(g), Blaster.Cnf(Ir.Read(Ir.Write(g).TrimEnd('\n').Split('\n'))));
        Assert.StartsWith("p cnf ", Blaster.Cnf(g));
        // x < 1 means x == 0, so the rule holds: no assignment satisfies the CNF
        var eval = new Evaluator(g);
        for (ulong a = 0; a < 256; a++) Assert.True(eval.Holds(new() { ["x"] = a }));
    }

    /// <summary>Every x (8 bits) or 2048 samples (16 bits) against edge and random y.</summary>
    static IEnumerable<(ulong, ulong)> Inputs(int w)
    {
        var rng = new Random(w);
        ulong mask = (1UL << w) - 1, min = 1UL << (w - 1);
        ulong[] edges = [0, 1, 2, 3, mask, mask - 1, min, min - 1, min + 1, 7, 40];
        var ys = edges.Concat(Enumerable.Range(0, 12).Select(_ => (ulong)rng.NextInt64(0, (long)mask + 1))).ToArray();
        var xs = w == 8 ? Enumerable.Range(0, 256).Select(i => (ulong)i).ToArray()
            : edges.Concat(Enumerable.Range(0, 64).Select(_ => (ulong)rng.NextInt64(0, (long)mask + 1))).ToArray();
        foreach (var a in xs) foreach (var b in ys) yield return (a, b);
    }

    /// <summary>The CNF with its inputs fixed, decided by unit propagation (complete here: Tseitin gates propagate
    /// forward from their inputs). Holds = the clauses conflict, i.e. no counterexample at these inputs.</summary>
    sealed class Evaluator
    {
        readonly int[][] clauses;
        readonly Dictionary<string, int[]> inputs;
        readonly int vars;

        public Evaluator(Goal g)
        {
            var lines = Blaster.Cnf(g, out inputs).TrimEnd('\n').Split('\n');
            vars = int.Parse(lines[0].Split(' ')[2]);
            clauses = lines.Skip(1).Select(l => l.Split(' ').Select(int.Parse).SkipLast(1).ToArray()).ToArray();
        }

        public bool Holds(Dictionary<string, ulong> at)
        {
            var val = new sbyte[vars + 1];
            bool Set(int lit) { int v = Math.Abs(lit); sbyte s = (sbyte)(lit > 0 ? 1 : -1); if (val[v] == -s) return false; val[v] = s; return true; }
            foreach (var (name, bits) in inputs)
                for (int i = 0; i < bits.Length; i++)
                    if (!Set(((at[name] >> i) & 1) != 0 ? bits[i] : -bits[i])) return true;
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var c in clauses)
                {
                    int open = 0, unit = 0;
                    bool sat = false;
                    foreach (var l in c)
                    {
                        int v = val[Math.Abs(l)];
                        if (v == 0) { open++; unit = l; }
                        else if ((v > 0) == (l > 0)) { sat = true; break; }
                    }
                    if (sat) continue;
                    if (open == 0) return true;
                    if (open == 1) { Set(unit); changed = true; }
                }
            }
            Assert.DoesNotContain(val.Skip(1), v => v == 0);   // propagation decided every variable
            return false;
        }
    }
}
