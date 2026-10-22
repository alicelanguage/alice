using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// Floored division by a power of two is an arithmetic shift, floored modulo is a mask: exact for two's complement
/// (`-7 >> 1 == -4`, `-7 & 3 == 1`), branch-free, and what the JIT cannot derive from the helper's general form.
/// Any other positive literal divisor uses the branch-free `floor_div_pos` / `floor_mod_pos`.
/// </summary>
public sealed class StrengthReduction : MirRewriter, IMirPass
{
    public string Name => "strength-reduce";

    protected override MExpr Rewrite(MExpr e)
    {
        if (e is MCall { Target: MCode { Text: "Prelude" }, Name: "floor_div" or "floor_mod", Args: [var x, var d] } c
            && MirFacts.IntLiteral(d, out long v) && MirFacts.PowerOfTwo(v, out int k))
        {
            bool isLong = d is MCode { Text: var t } && t.EndsWith('L');
            return c.Name == "floor_div"
                ? new MBinary { Op = ">>", L = x, R = new MCode(k.ToString()) }
                : new MBinary { Op = "&", L = x, R = MirFacts.Int(v - 1, isLong) };
        }
        if (e is MCall { Target: MCode { Text: "Prelude" }, Name: "floor_div" or "floor_mod", Args: [_, var d2] } c2 && MirFacts.IntLiteral(d2, out long v2) && v2 > 0)
            c2.Name += "_pos";   // positive divisor: branch-free floored form
        return e;
    }
}
