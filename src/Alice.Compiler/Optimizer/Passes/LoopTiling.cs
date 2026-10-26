using System.Globalization;
using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer.Analysis;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// Polyhedral-lite loop optimization (ADR 0025) of perfect 2-3 deep counted nests over arrays
/// (<see cref="Dependence.Match"/>):
/// - interchange: the legal loop order whose innermost loop walks the arrays with stride 1 (or not at all), then
///   whose middle loop does, ties kept in source order;
/// - tiling: 3-deep nests (reuse across the third loop, matrix multiply) and 2-deep nests that still stride
///   across rows innermost (transpose) are tiled when every dependence is non-negative.
/// The new nest runs behind a guard, evaluated once in Int64: every loop is non-empty, every subscript at every
/// corner of the iteration box is inside its array, each delinearized column stays in [0, N), and no written
/// array is the same object as another array of the nest. Then nothing in the nest can throw, so reordering
/// iterations is unobservable; otherwise the original nest runs unchanged. Reductions keep their order
/// (`c[i, j]` still sums k upwards), so results are bit-identical.
/// </summary>
public sealed class LoopTiling : IMirPass
{
    public string Name => "loop-tile";

    /// <summary>Cache model: L1 data and L2 sizes (bytes), 8-byte elements. `ALICE_TILE=n` overrides the tile.</summary>
    const int L1 = 32 * 1024, L2 = 256 * 1024, Elem = 8;

    static readonly int? Override = int.TryParse(Environment.GetEnvironmentVariable("ALICE_TILE"), out int t) && t > 0 ? t : null;

    public void Run(List<MStmt> body) => Walk(body);

    void Walk(List<MStmt>? body)
    {
        if (body == null) return;
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i] is MFor f && Transform(f) is { } fast) { body[i] = fast; continue; }
            switch (body[i])
            {
                case MIf s: Walk(s.Then); Walk(s.Else); break;
                case MWhile s: Walk(s.Body); break;
                case MFor s: Walk(s.Body); break;
                case MForeach s: Walk(s.Body); break;
                case MBlock s: Walk(s.Body); break;
                case MUsing s: Walk(s.Body); break;
                case MTry s: Walk(s.Body); foreach (var c in s.Catches) Walk(c.Body); Walk(s.Finally); break;
            }
        }
    }

    static MStmt? Transform(MFor outer)
    {
        if (Dependence.Match(outer) is not { } nest) return null;
        var dirs = Dependence.Directions(nest);
        int depth = nest.Loops.Count;
        var vars = nest.Loops.Select(l => l.Var).ToList();

        int[] best = Enumerable.Range(0, depth).ToArray();
        var bestCost = Cost(nest, best, vars);
        foreach (var perm in Permutations(depth))
        {
            if (!Dependence.Legal(dirs, perm)) continue;
            var c = Cost(nest, perm, vars);
            if (c.CompareTo(bestCost) < 0) (best, bestCost) = (perm, c);
        }
        bool identity = best.Select((p, i) => p == i).All(x => x);
        string inner = vars[best[^1]];
        bool tile = Dependence.Permutable(dirs)
            && (depth == 3 || nest.Accesses.Any(a => Stride(a.Index, inner) == 2));
        if (identity && !tile) return null;

        int arrays = nest.Accesses.Select(a => a.Array).Distinct().Count();
        int size = Override ?? TileSize(depth, arrays);
        if (Guard(nest) is not { } guard) return null;
        return new MIf
        {
            Line = outer.Line,
            Cond = guard,
            Then = [Build(nest, best, tile ? size : 0)],
            Else = [outer],
        };
    }

    /// <summary>
    /// Largest power of two T with the tiles' footprint in half a cache: 2-deep nests touch one T×T tile per
    /// array and should stay in L1; 3-deep nests (A, B, C tiles of a multiply) in L2.
    /// </summary>
    public static int TileSize(int depth, int arrays)
    {
        long budget = (depth == 2 ? L1 : L2) / 2;
        int t = 8;
        while ((long)(2 * t) * (2 * t) * Elem * Math.Max(arrays, 1) <= budget) t *= 2;
        return t;
    }

    /// <summary>0: invariant or unit stride; 1: small literal stride; 2: symbolic (row) stride.</summary>
    static int Stride(Affine idx, string var) =>
        idx.HasSymCoef(var) ? 2 : Math.Abs(idx.Coef(var)) is 0 or 1 ? 0 : 1;

    /// <summary>(innermost stride cost, middle stride cost, inversions vs source order): lower is better.</summary>
    static (int, int, int) Cost(LoopNest nest, int[] perm, List<string> vars)
    {
        int Level(int pos) => nest.Accesses.Sum(a => Stride(a.Index, vars[perm[pos]]));
        int inv = 0;
        for (int i = 0; i < perm.Length; i++)
            for (int j = i + 1; j < perm.Length; j++)
                if (perm[i] > perm[j]) inv++;
        return (Level(perm.Length - 1), perm.Length > 2 ? Level(perm.Length - 2) : 0, inv);
    }

    static IEnumerable<int[]> Permutations(int n)
    {
        if (n == 1) { yield return [0]; yield break; }
        foreach (var p in Permutations(n - 1))
            for (int at = 0; at <= p.Length; at++)
            {
                var l = p.ToList();
                l.Insert(at, n - 1);
                yield return l.ToArray();
            }
    }

    // ---- the guard ----

    /// <summary>The guard, or null when a condition is statically false (the fast nest would never run).</summary>
    static MExpr? Guard(LoopNest nest)
    {
        var conds = new Conds();
        var vars = nest.Vars;
        const long Limit = 1L << 30;   // keeps every Int64 corner product far from overflow
        var syms = nest.Accesses.SelectMany(a => a.Index.Syms)
            .Concat(nest.Loops.SelectMany(l => new[] { l.Start, l.End }).SelectMany(e => Dependence.Bound(e, vars)!.Syms)).Distinct();
        foreach (var s in syms) conds.AddAll(InRange(Affine.Long(new MCode(s)), -Limit, Limit));
        foreach (var l in nest.Loops)
        {
            conds.AddAll(InRange(Affine.Long(Clone(l.Start)), -Limit, Limit));
            conds.AddAll(InRange(Affine.Long(Clone(l.End)), -Limit, Limit));
            conds.Add(Bin("<", Affine.Long(Clone(l.Start)), Affine.Long(Clone(l.End))));
        }
        var seen = new HashSet<string>();
        foreach (var a in nest.Accesses)
        {
            string key = a.Array + ":" + string.Join(";", a.Index.Terms.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"));
            if (!seen.Add(key)) continue;
            foreach (var corner in Corners(nest.Loops))
            {
                var at = a.Index.ToLong(v => Clone(corner[v]));
                conds.Add(Bin("<=", new MCode("0L"), at));
                conds.Add(Bin("<", Clone(at), Affine.Long(new MCode(a.Array + ".Length"))));
                if (a.Sym != null && a.Dims is [_, var low])
                {
                    var col = low.ToLong(v => Clone(corner[v]));
                    conds.Add(Bin("<=", new MCode("0L"), col));
                    conds.Add(Bin("<", Clone(col), Affine.Long(new MCode(a.Sym))));
                }
            }
        }
        var arrays = nest.Accesses.Select(a => a.Array).Distinct().ToList();
        foreach (var w in nest.Accesses.Where(a => a.Write).Select(a => a.Array).Distinct())
            foreach (var x in arrays.Where(x => x != w))
                conds.Add(new MUnary { Op = "!", E = new MCall { Target = new MCode("global::System.Object"), Name = "ReferenceEquals", Args = { new MCode(w), new MCode(x) } } });
        if (conds.False) return null;
        return conds.List.Count == 0 ? new MCode("true") : conds.List.Aggregate((l, r) => Bin("&&", l, r));
    }

    /// <summary>Guard conditions, deduplicated; comparisons of two literals are decided now.</summary>
    sealed class Conds
    {
        public readonly List<MExpr> List = [];
        public bool False;
        readonly HashSet<string> seen = [];

        public void AddAll(IEnumerable<MExpr> cs) { foreach (var c in cs) Add(c); }

        public void Add(MExpr c)
        {
            if (c is MBinary { Op: "<" or "<=" } b && Affine.LongLiteral(b.L, out long l) && Affine.LongLiteral(b.R, out long r))
            {
                if (!(b.Op == "<" ? l < r : l <= r)) False = true;
                return;
            }
            if (seen.Add(Alice.Compiler.Backend.Printer.E(c))) List.Add(c);
        }
    }

    static IEnumerable<MExpr> InRange(MExpr e, long lo, long hi) =>
        [Bin("<", new MCode(lo.ToString(CultureInfo.InvariantCulture) + "L"), e), Bin("<", Clone(e), new MCode(hi.ToString(CultureInfo.InvariantCulture) + "L"))];

    /// <summary>Every corner of the iteration box: each counter at its first or last value.</summary>
    static IEnumerable<Dictionary<string, MExpr>> Corners(List<MFor> loops)
    {
        IEnumerable<Dictionary<string, MExpr>> acc = [new()];
        foreach (var l in loops)
            acc = acc.SelectMany(c => new[]
            {
                new Dictionary<string, MExpr>(c) { [l.Var] = Clone(l.Start) },
                new Dictionary<string, MExpr>(c) { [l.Var] = Bin("-", Clone(l.End), new MCode("1")) },
            });
        return acc.Select(c => c.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    static MBinary Bin(string op, MExpr l, MExpr r) => new() { Op = op, L = l, R = r };

    // ---- the new nest ----

    static MStmt Build(LoopNest nest, int[] order, int tile)
    {
        var loops = order.Select(i => nest.Loops[i]).ToList();
        int line = nest.Loops[0].Line;
        var body = nest.Body.Select(a => (MStmt)new MAssign
        {
            Line = a.Line, Op = a.Op, Target = Subscripts(Clone(a.Target)), Value = Subscripts(Clone(a.Value)),
        }).ToList();

        // point loops, innermost first
        for (int k = loops.Count - 1; k >= 0; k--)
        {
            var l = loops[k];
            body = [new MFor
            {
                Line = line, Var = l.Var, VarType = "int",
                Start = tile > 0 ? new MCode(l.Var + "_t") : Clone(l.Start),
                End = tile > 0
                    ? new MCall { Target = new MCode("global::System.Math"), Name = "Min", Args = { Unchecked(Bin("+", new MCode(l.Var + "_t"), Lit(tile))), Clone(l.End) } }
                    : Clone(l.End),
                Step = $" = unchecked({l.Var} + 1)",
                Body = body,
            }];
        }
        if (tile > 0)
            for (int k = loops.Count - 1; k >= 0; k--)
            {
                var l = loops[k];
                string tv = l.Var + "_t";
                body = [new MFor
                {
                    Line = line, Var = tv, VarType = "int", Start = Clone(l.Start), End = Clone(l.End),
                    Step = $" = unchecked({tv} + {tile.ToString(CultureInfo.InvariantCulture)})", Body = body,
                }];
            }
        return body[0];
    }

    static MCode Lit(int v) => new(v.ToString(CultureInfo.InvariantCulture));

    static MCall Unchecked(MExpr e) => new() { Name = "unchecked", Args = { e } };

    /// <summary>The guard proved every subscript's exact value is a valid index, so its arithmetic cannot overflow.</summary>
    static MExpr Subscripts(MExpr e)
    {
        if (e is MIndex ix) { ix.Args[0] = Unchecked(ix.Args[0]); return ix; }
        if (e is MCall c) { for (int i = 0; i < c.Args.Count; i++) c.Args[i] = Subscripts(c.Args[i]); return c; }
        if (e is MBinary b) { b.L = Subscripts(b.L); b.R = Subscripts(b.R); return b; }
        if (e is MUnary u) { u.E = Subscripts(u.E); return u; }
        return e;
    }

    /// <summary>Deep copy of the expression kinds a matched nest can contain.</summary>
    static MExpr Clone(MExpr e) => e switch
    {
        MCode c => new MCode(c.Text),
        MIndex ix => new MIndex { Target = Clone(ix.Target), Args = ix.Args.Select(Clone).ToList() },
        MBinary b => new MBinary { Op = b.Op, L = Clone(b.L), R = Clone(b.R) },
        MUnary u => new MUnary { Op = u.Op, Postfix = u.Postfix, E = Clone(u.E) },
        MCall c => new MCall { Target = c.Target == null ? null : Clone(c.Target), Name = c.Name, Safe = c.Safe, Args = c.Args.Select(Clone).ToList() },
        MMember m => new MMember { Target = Clone(m.Target), Name = m.Name, Safe = m.Safe },
        MCast c => new MCast { TypeCs = c.TypeCs, E = Clone(c.E) },
        _ => throw new InvalidOperationException("loop-tile: unexpected " + e.GetType().Name),
    };
}
