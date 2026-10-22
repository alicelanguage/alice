using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// `[a, b, c].min` lowers to `new T[] { a, b, c }.min()`: an allocation per call in what is often an inner loop
/// (edit distance, clamps). Rewritten to `Math.Min(Math.Min(a, b), c)`; elements are still evaluated left to right.
/// </summary>
public sealed class LiteralMinMax : MirRewriter, IMirPass
{
    public string Name => "literal-minmax";

    static readonly HashSet<string> Numeric = ["int", "long", "double", "float", "short", "sbyte", "byte", "ushort", "uint", "ulong"];

    protected override MExpr Rewrite(MExpr e)
    {
        if (e is not MCall { Name: "min" or "max", Args.Count: 0, Target: MNewArray { Items: { Count: > 0 } items, ElemCs: var t } } c || !Numeric.Contains(t))
            return e;
        string fn = c.Name == "min" ? "Min" : "Max";
        MExpr acc = Typed(items[0], t);
        foreach (var x in items.Skip(1))
            acc = new MCall { Target = new MCode("global::System.Math"), Name = fn, Args = { acc, Typed(x, t) } };
        return acc;
    }

    // the array literal converted each item to T: keep that conversion so the Min/Max overload and result type match
    static MExpr Typed(MExpr x, string t) => x is MCode && MirFacts.IntLiteral(x, out _) && t is "int" ? x : new MCast { TypeCs = t, E = x };
}
