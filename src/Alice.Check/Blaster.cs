using System.Text;

namespace Alice.Check;

/// <summary>
/// The meaning of the proof IR, as a Tseitin encoding: <see cref="Cnf"/> of a <see cref="Goal"/> is satisfiable exactly
/// when some input in the domain makes the rule fail or return false. Literal 1 is the constant true. Deterministic:
/// the same goal always gives the same clauses in the same order, so a checker can re-derive and compare them.
/// </summary>
public sealed class Blaster
{
    readonly List<int[]> clauses = [[1]];
    int vars = 1;
    const int T = 1, F = -1;
    readonly Dictionary<Term, int[]> memo = new(ReferenceEqualityComparer.Instance);

    /// <summary>The goal's negation as DIMACS: domain, and (fails or not result).</summary>
    public static string Cnf(Goal g) => Cnf(g, out _);

    /// <summary>The same, and the literals of each named variable (bit 0 first), for reading a model.</summary>
    public static string Cnf(Goal g, out Dictionary<string, int[]> inputs)
    {
        var b = new Blaster();
        foreach (var d in g.Domain) b.clauses.Add([b.Bit(d)]);
        b.clauses.Add([b.Bit(g.Fails), -b.Bit(g.Result)]);
        inputs = b.memo.Where(m => m.Key.Op == Op.Var).ToDictionary(m => m.Key.Name!, m => m.Value);
        var sb = new StringBuilder($"p cnf {b.vars} {b.clauses.Count}\n");
        foreach (var c in b.clauses) sb.AppendJoin(' ', c).Append(" 0\n");
        return sb.ToString();
    }

    int Bit(Term t) => Bits(t)[0];

    /// <summary>A term's literals, bit 0 first (one literal for a Bool), operands encoded before their users.</summary>
    int[] Bits(Term t)
    {
        if (memo.TryGetValue(t, out var r)) return r;
        foreach (var a in t.A) Bits(a);
        memo[t] = r = Encode(t);
        return r;
    }

    int[] Encode(Term t)
    {
        int[] A(int i) => memo[t.A[i]];
        int L(int i) => memo[t.A[i]][0];
        bool sg = t.K != 0;
        int w = (int)t.W;
        switch (t.Op)
        {
            case Op.Bool: return [t.K != 0 ? T : F];
            case Op.Bv: return Enumerable.Range(0, w).Select(i => i < 64 && ((t.K >> i) & 1) != 0 ? T : F).ToArray();
            case Op.Var: return Enumerable.Range(0, Math.Max(w, 1)).Select(_ => ++vars).ToArray();
            case Op.Not: return [-L(0)];
            case Op.And: return [t.A.Select(a => memo[a][0]).Aggregate(T, And)];
            case Op.Or: return [-t.A.Select(a => -memo[a][0]).Aggregate(T, And)];
            case Op.Eq: return [Equal(A(0), A(1))];
            case Op.Ite: return A(1).Zip(A(2), (x, y) => Mux(L(0), x, y)).ToArray();
            case Op.Ult: return [Less(A(0), A(1))];
            case Op.Ule: return [-Less(A(1), A(0))];
            case Op.Slt: return [Less(Flip(A(0)), Flip(A(1)))];
            case Op.Sle: return [-Less(Flip(A(1)), Flip(A(0)))];
            case Op.Add: return Add(A(0), A(1), F).S;
            case Op.Sub: return Add(A(0), Not(A(1)), T).S;
            case Op.Neg: return Add(Const(w, 0), Not(A(0)), T).S;
            case Op.Mul: return Mul(A(0), A(1));
            case Op.BvNot: return Not(A(0));
            case Op.BAnd: return A(0).Zip(A(1), And).ToArray();
            case Op.BOr: return A(0).Zip(A(1), (x, y) => -And(-x, -y)).ToArray();
            case Op.BXor: return A(0).Zip(A(1), Xor).ToArray();
            case Op.UDiv: return DivRem(A(0), A(1)).Q;
            case Op.URem: return DivRem(A(0), A(1)).R;
            case Op.FDiv: return Floored(A(0), A(1)).Q;
            case Op.FMod: return Floored(A(0), A(1)).R;
            case Op.SExt: return A(0).Concat(Enumerable.Repeat(A(0)[^1], w - A(0).Length)).ToArray();
            case Op.ZExt: return A(0).Concat(Enumerable.Repeat(F, w - A(0).Length)).ToArray();
            case Op.Extract: return A(0)[..w];
            case Op.AddOvf:
                {
                    var (s, c) = Add(A(0), A(1), F);
                    return [sg ? And(-Xor(A(0)[^1], A(1)[^1]), Xor(s[^1], A(0)[^1])) : c];   // same signs in, other sign out
                }
            case Op.SubOvf:
                {
                    var (d, c) = Add(A(0), Not(A(1)), T);
                    return [sg ? And(Xor(A(0)[^1], A(1)[^1]), Xor(d[^1], A(0)[^1])) : -c];   // a borrow
                }
            case Op.MulOvf:
                {
                    // the exact product at twice the width; it overflows when the high half is not the low half's extension
                    int n = A(0).Length;
                    int[] X(int[] v) => v.Concat(Enumerable.Repeat(sg ? v[^1] : F, n)).ToArray();
                    var p = Mul(X(A(0)), X(A(1)));
                    int top = sg ? p[n - 1] : F;
                    return [-p[n..].Select(b => -Xor(b, top)).Aggregate(T, And)];
                }
        }
        throw new InvalidOperationException(t.Op.ToString());
    }

    // ---- gates (with constant folding, so constants cost no clauses) ----

    int New(params int[][] cs)
    {
        int v = ++vars;
        foreach (var c in cs) clauses.Add(c.Select(l => l == 0 ? v : l == int.MinValue ? -v : l).ToArray());
        return v;
    }

    const int V = 0, NV = int.MinValue;   // placeholders for the new gate's literal in New's clauses

    int And(int a, int b) =>
        a == F || b == F || a == -b ? F : a == T ? b : b == T || a == b ? a : New([NV, a], [NV, b], [V, -a, -b]);

    int Xor(int a, int b) =>
        a == F ? b : b == F ? a : a == T ? -b : b == T ? -a : a == b ? F : a == -b ? T
        : New([NV, a, b], [NV, -a, -b], [V, -a, b], [V, a, -b]);

    int Mux(int c, int x, int y) =>
        c == T ? x : c == F ? y : x == y ? x : New([NV, -c, x], [NV, c, y], [V, -c, -x], [V, c, -y]);

    int Equal(int[] a, int[] b) => a.Zip(b, (x, y) => -Xor(x, y)).Aggregate(T, And);

    static int[] Not(int[] a) => a.Select(x => -x).ToArray();
    static int[] Const(int w, long v) => Enumerable.Range(0, w).Select(i => i < 64 && ((v >> i) & 1) != 0 ? T : F).ToArray();
    static int[] Flip(int[] a) => [.. a[..^1], -a[^1]];   // signed order = unsigned order with the sign bit flipped

    /// <summary>Ripple-carry a + b + cin: the sum bits and the carry out.</summary>
    (int[] S, int C) Add(int[] a, int[] b, int c)
    {
        var s = new int[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int x = Xor(a[i], b[i]);
            s[i] = Xor(x, c);
            c = -And(-And(a[i], b[i]), -And(x, c));
        }
        return (s, c);
    }

    /// <summary>a &lt; b unsigned: no carry out of a + ~b + 1.</summary>
    int Less(int[] a, int[] b) => -Add(a, Not(b), T).C;

    /// <summary>a * b modulo 2^width: shift-and-add.</summary>
    int[] Mul(int[] a, int[] b)
    {
        int n = a.Length;
        var acc = Const(n, 0);
        for (int i = 0; i < n; i++)
        {
            if (b[i] == F) continue;
            var row = Const(n, 0);
            for (int j = i; j < n; j++) row[j] = And(a[j - i], b[i]);
            acc = Add(acc, row, F).S;
        }
        return acc;
    }

    /// <summary>Unsigned long division (restoring). A zero divisor gives all-ones and a (unused: it fails the rule).</summary>
    (int[] Q, int[] R) DivRem(int[] a, int[] b)
    {
        int n = a.Length;
        var q = new int[n];
        var r = Const(n, 0);
        var bb = b.Append(F).ToArray();
        for (int i = n - 1; i >= 0; i--)
        {
            var rr = new[] { a[i] }.Concat(r).ToArray();   // r * 2 + a[i], n + 1 bits
            var (d, c) = Add(rr, Not(bb), T);
            q[i] = c;                                        // rr >= b
            r = d[..n].Zip(rr[..n], (x, y) => Mux(c, x, y)).ToArray();
        }
        return (q, r);
    }

    /// <summary>Alice's signed `/` and `%`: truncated division on magnitudes with signs restored, then rounded toward
    /// negative infinity when the remainder is non-zero and its sign differs from the divisor's (ADR 0013).</summary>
    (int[] Q, int[] R) Floored(int[] a, int[] b)
    {
        int n = a.Length, sa = a[^1], sb = b[^1];
        int[] Neg(int[] v) => Add(Const(n, 0), Not(v), T).S;
        int[] Abs(int[] v) => v.Zip(Neg(v), (x, y) => Mux(v[^1], y, x)).ToArray();
        int[] Sel(int c, int[] x, int[] y) => x.Zip(y, (p, s) => Mux(c, p, s)).ToArray();
        var (uq, ur) = DivRem(Abs(a), Abs(b));
        var q = Sel(Xor(sa, sb), Neg(uq), uq);
        var r = Sel(sa, Neg(ur), ur);
        int adjust = And(-Equal(r, Const(n, 0)), Xor(r[^1], sb));
        return (Sel(adjust, Add(q, Const(n, -1), F).S, q), Sel(adjust, Add(r, b, F).S, r));
    }
}
