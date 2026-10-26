using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Analysis;

/// <summary>
/// A perfect nest of 2-3 counted loops `for (int v = lo; v &lt; hi; v++)` with rectangular, loop-invariant bounds,
/// whose innermost body is only element assignments `x[affine] = pure`.
/// </summary>
public sealed class LoopNest
{
    public required List<MFor> Loops;
    public required List<MAssign> Body;
    public required List<Access> Accesses;
    public IReadOnlySet<string> Vars => Loops.Select(l => l.Var).ToHashSet();
}

/// <summary>One array element access. `Dims` is the delinearized subscript (row, column) or the flat one.</summary>
public sealed record Access(string Array, Affine Index, bool Write, List<Affine>? Dims, string? Sym);

/// <summary>
/// Affine dependence analysis (ADR 0025). For every pair of accesses to the same array, at least one a write,
/// each delinearized dimension that is `v + a` in both gives the exact distance a - b on loop v; constant
/// dimensions that differ prove independence; anything else leaves the distance unknown (`*`, any sign).
/// The result is the set of direction vectors (signs, in original loop order) made lexicographically positive.
/// Delinearizing `i * n + j` into (i, j) assumes `0 &lt;= j &lt; n`; the transformation's runtime guard checks it.
/// </summary>
public static class Dependence
{
    public static LoopNest? Match(MFor outer)
    {
        var loops = new List<MFor>();
        MFor f = outer;
        while (true)
        {
            if (!Counted(f) || loops.Any(l => l.Var == f.Var)) return null;
            loops.Add(f);
            if (f.Body is [MFor inner]) { f = inner; continue; }
            break;
        }
        if (loops.Count is < 2 or > 3) return null;
        var vars = loops.Select(l => l.Var).ToHashSet();
        foreach (var l in loops)
            if (Bound(l.Start, vars) == null || Bound(l.End, vars) == null) return null;

        var body = new List<MAssign>();
        var accesses = new List<Access>();
        foreach (var s in f.Body)
        {
            if (s is not MAssign { Target: MIndex t } a || !Element(t, vars, write: true, accesses)) return null;
            if (a.Op != "=" && (a.Op is not ("+=" or "-=" or "*=") || !Float(a.Value, accesses))) return null;
            if (a.Op != "=") Element(t, vars, write: false, accesses);   // a compound assignment also reads
            if (!Pure(a.Value, vars, accesses)) return null;
            body.Add(a);
        }
        if (body.Count == 0) return null;
        // a scalar the nest reads must not be an array it writes (`n` would then be unstable); arrays are not
        // reassigned by element assignments, and the body has no calls that could write anything else
        var written = accesses.Where(x => x.Write).Select(x => x.Array).ToHashSet();
        foreach (var x in accesses)
            if (x.Index.Syms.Any(s => written.Contains(s))) return null;
        return new LoopNest { Loops = loops, Body = body, Accesses = accesses };
    }

    static bool Counted(MFor f) =>
        f.VarType is "int" or null && f.Cmp == "<" && (f.Step == "++" || f.Step == $" = unchecked({f.Var} + 1)");

    /// <summary>A loop bound: affine, free of the nest's counters.</summary>
    public static Affine? Bound(MExpr e, IReadOnlySet<string> vars) =>
        Affine.Parse(e, vars) is { } a && !a.Vars.Any() ? a : null;

    static bool Element(MIndex ix, IReadOnlySet<string> vars, bool write, List<Access> into)
    {
        if (ix.Target is not MCode { Text: var arr } || !Affine.Identifier(arr) || vars.Contains(arr) || ix.Args.Count != 1) return false;
        if (Affine.Parse(ix.Args[0], vars) is not { } idx) return false;
        var (dims, sym) = Delinearize(idx);
        into.Add(new Access(arr, idx, write, dims, sym));
        return true;
    }

    /// <summary>Row-major `high * N + low` with one symbol N; a form without symbols is one dimension.</summary>
    static (List<Affine>?, string?) Delinearize(Affine idx)
    {
        var syms = idx.Syms.ToList();
        if (syms.Count == 0) return ([idx], null);
        if (syms.Count == 1 && idx.Split(syms[0]) is var (hi, lo)) return ([hi, lo], syms[0]);
        return (null, null);
    }

    /// <summary>
    /// The right-hand side cannot throw or have effects: array reads with affine subscripts, invariant names,
    /// literals, and floating-point arithmetic (`Prelude.fma`, a float literal operand). Integer `+ - *` is checked
    /// (ADR 0013) and could throw at a different iteration once reordered, so it is rejected.
    /// </summary>
    static bool Pure(MExpr e, IReadOnlySet<string> vars, List<Access> into) => e switch
    {
        MIndex ix => Element(ix, vars, write: false, into),
        MCode c => MirFacts.IntLiteral(c, out _) || FloatLiteral(c.Text) || Affine.Identifier(c.Text),
        MCall { Target: MCode { Text: "Prelude" }, Name: "fma" } c => c.Args.All(x => Pure(x, vars, into)),
        MUnary { Op: "-", Postfix: false } u => Float(u.E, into) && Pure(u.E, vars, into),
        MBinary { Op: "+" or "-" or "*" } b => (Float(b.L, into) || Float(b.R, into)) && Pure(b.L, vars, into) && Pure(b.R, vars, into),
        _ => false,
    };

    /// <summary>Provably a Float64/Float32 value (so C# arithmetic on it is unchecked and cannot throw).</summary>
    static bool Float(MExpr e, List<Access> seen) => e switch
    {
        MCode c => FloatLiteral(c.Text),
        MCall { Target: MCode { Text: "Prelude" }, Name: "fma" } => true,
        MCast { TypeCs: "double" or "float" } => true,
        MUnary { Op: "-" } u => Float(u.E, seen),
        MBinary { Op: "+" or "-" or "*" or "/" } b => Float(b.L, seen) || Float(b.R, seen),
        _ => false,
    };

    static bool FloatLiteral(string t)
    {
        t = t.TrimStart('-');
        if (t.EndsWith('f') || t.EndsWith('F') || t.EndsWith('d') || t.EndsWith('D')) t = t[..^1];
        return t.Contains('.') && t.Replace(".", "").Replace("e", "").Replace("E", "").Replace("-", "").Replace("+", "")
            .All(char.IsAsciiDigit) && char.IsAsciiDigit(t[0]);
    }

    /// <summary>Direction vectors (−1/0/+1 per loop, original order), each lexicographically positive.</summary>
    public static HashSet<string> Directions(LoopNest nest)
    {
        var loops = nest.Loops.Select(l => l.Var).ToList();
        var result = new HashSet<string>();
        var acc = nest.Accesses;
        for (int p = 0; p < acc.Count; p++)
            for (int q = p; q < acc.Count; q++)
            {
                Access a = acc[p], b = acc[q];
                if (a.Array != b.Array || !a.Write && !b.Write) continue;
                if (Distances(a, b, loops) is not { } dist) continue;   // proven independent
                foreach (var v in Expand(dist, 0))
                {
                    int first = Array.FindIndex(v, x => x != 0);
                    if (first < 0) continue;   // same iteration: statement order is kept
                    if (v[first] < 0) for (int k = 0; k < v.Length; k++) v[k] = -v[k];
                    result.Add(string.Join(",", v));
                }
            }
        return result;
    }

    /// <summary>Per-loop distance (null = unknown); null result when the accesses can never touch the same element.</summary>
    static long?[]? Distances(Access a, Access b, List<string> loops)
    {
        var dist = new long?[loops.Count];
        if (a.Dims == null || b.Dims == null || a.Sym != b.Sym || a.Dims.Count != b.Dims.Count) return dist;
        for (int d = 0; d < a.Dims.Count; d++)
        {
            if (a.Dims[d].Uniform() is not var (va, oa) || b.Dims[d].Uniform() is not var (vb, ob)) continue;
            if (va == null && vb == null) { if (oa != ob) return null; continue; }
            if (va == null || va != vb) continue;
            int k = loops.IndexOf(va);
            long dk = oa - ob;
            if (dist[k] is { } prev && prev != dk) return null;
            dist[k] = dk;
        }
        return dist;
    }

    static IEnumerable<int[]> Expand(long?[] dist, int k)
    {
        if (k == dist.Length) { yield return new int[dist.Length]; yield break; }
        int[] signs = dist[k] is { } d ? [Math.Sign(d)] : [-1, 0, 1];
        foreach (var rest in Expand(dist, k + 1))
            foreach (var s in signs)
            {
                var v = (int[])rest.Clone();
                v[k] = s;
                yield return v;
            }
    }

    static IEnumerable<int[]> Parse(HashSet<string> dirs) => dirs.Select(d => d.Split(',').Select(int.Parse).ToArray());

    /// <summary>Loop order `perm` (perm[new position] = original index) keeps every dependence lexicographically positive.</summary>
    public static bool Legal(HashSet<string> dirs, int[] perm) =>
        Parse(dirs).All(v => perm.Select(p => v[p]).FirstOrDefault(x => x != 0) > 0);

    /// <summary>Every dependence is non-negative in every loop: the band is fully permutable, so tiling is legal.</summary>
    public static bool Permutable(HashSet<string> dirs) => Parse(dirs).All(v => v.All(x => x >= 0));
}
