using Alice.Compiler;
using Alice.Compiler.Syntax;
using Alice.Cli.Proof.Z3;
using Expr = Alice.Cli.Proof.Z3.Expr;
using Term = Alice.Check.Term;
using Op = Alice.Check.Op;

namespace Alice.Cli.Proof;

/// <summary>
/// `alice prove --smt` (ADR 0022): a rule holds for every input of its domain, the full Int32/Int64 range included,
/// when Z3 finds no input that makes it fail. Failing means returning false or raising: integer overflow (Alice traps,
/// ADR 0013), division by zero, `Int::MIN / -1`. The rule is first translated to the proof IR (<see cref="Symbolic"/>);
/// this class lowers that IR to Z3 terms. A construct outside the IR makes the rule untranslatable, never half-proven.
/// </summary>
public static class SmtProver
{
    /// <summary><paramref name="Domains"/>: the rule's quantified variables, so a proof can be sample-checked against
    /// the compiled rule (review-r1 F8: an UNSAT verdict is never trusted blindly). <paramref name="T"/>: the rule's
    /// translation, which a certificate (ADR 0042) is built from. <paramref name="Unknown"/>: Z3 gave up (time limit).</summary>
    public sealed record Outcome(string Rule, bool Proved, string? Counterexample, string? Untranslatable, double Ms = 0, IReadOnlyList<RuleDomain>? Domains = null, Translation? T = null, bool Unknown = false);

    /// <summary>Z3's limit per rule when `alice prove --timeout` is not given: a nonlinear 64-bit query can run for hours.</summary>
    public const uint DefaultTimeoutMs = 20000;

    /// <summary>Z3's native library: the system one (`pacman -S z3`, `apt install libz3-4`, `brew install z3`) or
    /// ALICE_Z3=/path/to/libz3. Any architecture: the C API is called directly.</summary>
    public static string? Unavailable()
    {
        try
        {
            if (Z3Native.Load()) { using var ctx = new Context(); return null; }
        }
        catch (EntryPointNotFoundException) { }   // a library named libz3 that is not Z3's
        return "Z3 is not installed: install it (pacman -S z3, apt install libz3-4, brew install z3) or set ALICE_Z3=/path/to/libz3";
    }

    public static List<Outcome> Prove(List<SourceFile> files, IEnumerable<string> refs, uint timeoutMs = DefaultTimeoutMs)
    {
        var r = new List<Outcome>();
        foreach (var (rule, t, why) in Symbolic.Translate(files, refs))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (t == null) { r.Add(new Outcome(rule.RuleName!, false, null, why, sw.Elapsed.TotalMilliseconds)); continue; }
            using var ctx = new Context();
            r.Add(Check(ctx, t, timeoutMs) with { Ms = sw.Elapsed.TotalMilliseconds, Domains = rule.Domains, T = t });
        }
        return r;
    }

    static Outcome Check(Context z, Translation t, uint timeoutMs)
    {
        var memo = new Dictionary<Term, Expr>(ReferenceEqualityComparer.Instance);
        Expr L(Term x) => Lower(z, x, memo);
        using var s = z.MkSolver();
        s.TimeoutMs = timeoutMs;   // a nonlinear 64-bit query can run for hours: give up, enumerate instead
        s.Assert(t.Goal.Domain.Select(d => (BoolExpr)L(d)).ToArray());
        s.Assert(z.MkOr((BoolExpr)L(t.Goal.Fails), z.MkNot((BoolExpr)L(t.Goal.Result))));   // an input that raises or returns false
        string rule = t.Rule.RuleName!;
        var status = s.Check();
        if (status == Status.UNSATISFIABLE) return new Outcome(rule, true, null, null);
        if (status == Status.UNKNOWN) return new Outcome(rule, false, null, "Z3 could not decide the rule: " + s.ReasonUnknown, Unknown: true);
        string ce = string.Join(", ", t.Inputs.Select(x => $"{x.Domain.Name} = {s.Value(L(x.Var))}"));
        return new Outcome(rule, false, ce, null);
    }

    /// <summary>One IR node as a Z3 term (operands memoized: the IR is a DAG).</summary>
    static Expr Lower(Context z, Term t, Dictionary<Term, Expr> memo)
    {
        if (memo.TryGetValue(t, out var done)) return done;
        var a = t.A.Select(x => Lower(z, x, memo)).ToArray();
        BoolExpr B(int i) => (BoolExpr)a[i];
        BitVecExpr V(int i) => (BitVecExpr)a[i];
        bool sg = t.K != 0;
        Expr r = t.Op switch
        {
            Op.Bool => z.MkBool(t.K != 0),
            Op.Bv => z.MkBV(t.K, t.W),
            Op.Var => t.IsBool ? z.MkBoolConst(t.Name!) : z.MkBVConst(t.Name!, t.W),
            Op.Not => z.MkNot(B(0)),
            Op.And => z.MkAnd(a.Cast<BoolExpr>().ToArray()),
            Op.Or => z.MkOr(a.Cast<BoolExpr>().ToArray()),
            Op.Eq => z.MkEq(a[0], a[1]),
            Op.Ite => z.MkITE(B(0), a[1], a[2]),
            Op.Slt => z.MkBVSLT(V(0), V(1)),
            Op.Sle => z.MkBVSLE(V(0), V(1)),
            Op.Ult => z.MkBVULT(V(0), V(1)),
            Op.Ule => z.MkBVULE(V(0), V(1)),
            Op.Add => z.MkBVAdd(V(0), V(1)),
            Op.Sub => z.MkBVSub(V(0), V(1)),
            Op.Mul => z.MkBVMul(V(0), V(1)),
            Op.Neg => z.MkBVNeg(V(0)),
            Op.BvNot => z.MkBVNot(V(0)),
            Op.BAnd => z.MkBVAND(V(0), V(1)),
            Op.BOr => z.MkBVOR(V(0), V(1)),
            Op.BXor => z.MkBVXOR(V(0), V(1)),
            Op.UDiv => z.MkBVUDiv(V(0), V(1)),
            Op.URem => z.MkBVURem(V(0), V(1)),
            Op.FDiv => Floored(z, V(0), V(1)).Q,
            Op.FMod => Floored(z, V(0), V(1)).R,
            Op.SExt => z.MkSignExt(t.W - V(0).SortSize, V(0)),
            Op.ZExt => z.MkZeroExt(t.W - V(0).SortSize, V(0)),
            Op.Extract => z.MkExtract(t.W - 1, 0, V(0)),
            Op.AddOvf => z.MkNot(z.MkAnd(z.MkBVAddNoOverflow(V(0), V(1), sg), sg ? z.MkBVAddNoUnderflow(V(0), V(1)) : z.MkTrue())),
            Op.SubOvf => z.MkNot(z.MkAnd(sg ? z.MkBVSubNoOverflow(V(0), V(1)) : z.MkTrue(), z.MkBVSubNoUnderflow(V(0), V(1), sg))),
            Op.MulOvf => z.MkNot(z.MkAnd(z.MkBVMulNoOverflow(V(0), V(1), sg), sg ? z.MkBVMulNoUnderflow(V(0), V(1)) : z.MkTrue())),
            _ => throw new InvalidOperationException(t.Op.ToString()),
        };
        return memo[t] = r;
    }

    /// <summary>Alice's `/` and `%` round toward negative infinity (ADR 0013): truncated division, then one step down
    /// when the remainder is non-zero and its sign differs from the divisor's.</summary>
    static (BitVecExpr Q, BitVecExpr R) Floored(Context z, BitVecExpr x, BitVecExpr y)
    {
        var zero = z.MkBV(0, x.SortSize);
        var q = z.MkBVSDiv(x, y);
        var r = z.MkBVSRem(x, y);
        var adjust = z.MkAnd(z.MkNot(z.MkEq(r, zero)), z.MkBVSLT(z.MkBVXOR(r, y), zero));
        var one = z.MkBV(1, x.SortSize);
        return ((BitVecExpr)z.MkITE(adjust, z.MkBVSub(q, one), q), (BitVecExpr)z.MkITE(adjust, z.MkBVAdd(r, y), r));
    }
}
