using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// Folds integer arithmetic on literals (`1 << 4`, `60 * 60`, floored `/` and `%`) so later passes see constants.
/// An operation that would overflow is left alone: Roslyn's checked constant folding then reports it, as Alice wants.
/// </summary>
public sealed class ConstantFolding : MirRewriter, IMirPass
{
    public string Name => "const-fold";

    protected override MExpr Rewrite(MExpr e)
    {
        switch (e)
        {
            case MBinary b when MirFacts.IntLiteral(b.L, out long l) && MirFacts.IntLiteral(b.R, out long r):
                {
                    bool isLong = IsLong(b.L) || IsLong(b.R);
                    long? v = b.Op switch
                    {
                        "+" => Checked(() => l + r),
                        "-" => Checked(() => l - r),
                        "*" => Checked(() => l * r),
                        "&" => l & r,
                        "|" => l | r,
                        "^" => l ^ r,
                        "<<" when r is >= 0 and < 31 && !isLong => Checked(() => l << (int)r),
                        ">>" when r >= 0 => l >> (int)Math.Min(r, isLong ? 63 : 31),
                        _ => null,
                    };
                    return v is long x && Fits(x, isLong) ? MirFacts.Int(x, isLong) : e;
                }
            case MCall { Target: MCode { Text: "Prelude" }, Name: "floor_div" or "floor_mod", Args: [var a, var d] } c
                when MirFacts.IntLiteral(a, out long x) && MirFacts.IntLiteral(d, out long y) && y != 0 && !(x == long.MinValue && y == -1):
                {
                    bool isLong = IsLong(a) || IsLong(d);
                    long q = x / y, m = x % y;
                    if (m != 0 && ((m ^ y) < 0)) { q--; m += y; }
                    long v = c.Name == "floor_div" ? q : m;
                    return Fits(v, isLong) ? MirFacts.Int(v, isLong) : e;
                }
        }
        return e;
    }

    static bool IsLong(MExpr e) => e is MCode c && c.Text.EndsWith('L');

    static bool Fits(long v, bool isLong) => isLong || v is >= int.MinValue and <= int.MaxValue;

    static long? Checked(Func<long> f)
    {
        try { return checked(f()); }
        catch (OverflowException) { return null; }
    }
}
