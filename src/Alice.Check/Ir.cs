using System.Text;

namespace Alice.Check;

/// <summary>The operators of the proof IR (ADR 0042). Bit vectors wrap; the failures of Alice's checked arithmetic
/// are the *Ovf predicates; FDiv/FMod are Alice's floored `/` and `%` (ADR 0013). Their meaning is
/// <see cref="Blaster"/>: the definitions a certificate is trusted against.</summary>
public enum Op
{
    Bool, Bv, Var,
    Not, And, Or, Eq, Ite,
    Slt, Sle, Ult, Ule,
    Add, Sub, Mul, Neg, BvNot, BAnd, BOr, BXor,
    FDiv, FMod, UDiv, URem,
    SExt, ZExt, Extract,
    AddOvf, SubOvf, MulOvf,
}

/// <summary>A node of the IR DAG. <see cref="W"/>: 0 for Bool, else the width. <see cref="K"/>: a literal's bits (or a
/// Bool's 0/1), 1 for the signed form of an overflow predicate or division.</summary>
public sealed class Term(Op op, uint w, Term[] args, long k = 0, string? name = null)
{
    public readonly Op Op = op;
    public readonly uint W = w;
    public readonly Term[] A = args;
    public readonly long K = k;
    public readonly string? Name = name;
    public bool IsBool => W == 0;
}

/// <summary>A claim: for every assignment of the variables with every <see cref="Domain"/> term true,
/// <see cref="Fails"/> is false and <see cref="Result"/> is true.</summary>
public sealed record Goal(List<Term> Domain, Term Fails, Term Result);

/// <summary>The IR as text, one node per line (`add 32 0 n3 n4`), arguments before their users; the last line is
/// `goal fails result domain...`. Writing is deterministic; reading checks arities and widths.</summary>
public static class Ir
{
    public static string Write(Goal g)
    {
        var ids = new Dictionary<Term, int>(ReferenceEqualityComparer.Instance);
        var sb = new StringBuilder();
        int Id(Term root)
        {
            var stack = new Stack<(Term T, bool Done)>();
            stack.Push((root, false));
            while (stack.Count > 0)
            {
                var (t, done) = stack.Pop();
                if (ids.ContainsKey(t)) continue;
                if (!done)
                {
                    stack.Push((t, true));
                    for (int i = t.A.Length - 1; i >= 0; i--) stack.Push((t.A[i], false));
                    continue;
                }
                sb.Append(t.Op.ToString().ToLowerInvariant()).Append(' ').Append(t.W).Append(' ').Append(t.K);
                foreach (var a in t.A) sb.Append(' ').Append(ids[a]);
                if (t.Name != null) sb.Append(' ').Append(t.Name);
                sb.Append('\n');
                ids[t] = ids.Count;
            }
            return ids[root];
        }
        int f = Id(g.Fails), r = Id(g.Result);
        var d = g.Domain.Select(Id).ToList();
        sb.Append("goal ").Append(f).Append(' ').Append(r);
        foreach (var x in d) sb.Append(' ').Append(x);
        return sb.Append('\n').ToString();
    }

    public static Goal Read(IReadOnlyList<string> lines)
    {
        var nodes = new List<Term>();
        Term Arg(string s) => int.TryParse(s, out var i) && i >= 0 && i < nodes.Count ? nodes[i] : throw new FormatException($"bad node reference `{s}`");
        for (int n = 0; n < lines.Count; n++)
        {
            var p = lines[n].Split(' ');
            if (p[0] == "goal" && n == lines.Count - 1 && p.Length >= 3)
            {
                var g = new Goal(p.Skip(3).Select(Arg).ToList(), Arg(p[1]), Arg(p[2]));
                if (!g.Fails.IsBool || !g.Result.IsBool || g.Domain.Any(x => !x.IsBool)) throw new FormatException("goal terms must be Bool");
                return g;
            }
            if (p.Length < 3 || !Enum.TryParse<Op>(p[0], true, out var op) || p[0] != p[0].ToLowerInvariant() || !uint.TryParse(p[1], out var w) || w > 128 || !long.TryParse(p[2], out var k))
                throw new FormatException($"line {n + 1}: `{lines[n]}` is not an IR node");
            bool named = op == Op.Var;
            if (named && p.Length != 4) throw new FormatException($"line {n + 1}: a var has one name");
            var args = p.Skip(3).Take(p.Length - 3 - (named ? 1 : 0)).Select(Arg).ToArray();
            var t = new Term(op, w, args, k, named ? p[3] : null);
            if (Typed(t) is { } why) throw new FormatException($"line {n + 1}: {why}");
            nodes.Add(t);
        }
        throw new FormatException("the IR has no goal line");
    }

    /// <summary>Null when the node is well formed (arity, widths), else why not.</summary>
    static string? Typed(Term t)
    {
        var a = t.A;
        bool Ok(int n) => a.Length == n;
        bool Same() => a.All(x => x.W == t.W && !x.IsBool);
        bool Pair() => Ok(2) && a[0].W == a[1].W && !a[0].IsBool;
        bool good = t.Op switch
        {
            Op.Bool => Ok(0) && t.W == 0 && t.K is 0 or 1,
            Op.Bv => Ok(0) && t.W > 0 && (t.W >= 64 || (t.K >> (int)t.W) == 0),
            Op.Var => Ok(0),
            Op.Not => Ok(1) && t.IsBool && a[0].IsBool,
            Op.And or Op.Or => a.Length >= 2 && t.IsBool && a.All(x => x.IsBool),
            Op.Eq => Ok(2) && t.IsBool && a[0].W == a[1].W,
            Op.Ite => Ok(3) && a[0].IsBool && a[1].W == t.W && a[2].W == t.W,
            Op.Slt or Op.Sle or Op.Ult or Op.Ule or Op.AddOvf or Op.SubOvf or Op.MulOvf => t.IsBool && Pair(),
            Op.Neg or Op.BvNot => Ok(1) && Same(),
            Op.SExt or Op.ZExt => Ok(1) && !a[0].IsBool && t.W > a[0].W,
            Op.Extract => Ok(1) && !a[0].IsBool && t.W > 0 && t.W < a[0].W,
            _ => Ok(2) && Same() && t.W > 0,
        };
        return good ? null : $"`{t.Op}` node with these operands is ill-typed";
    }
}
