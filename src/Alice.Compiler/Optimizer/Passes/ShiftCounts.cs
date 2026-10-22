using Alice.Compiler.Mir;

namespace Alice.Compiler.Optimizer.Passes;

/// <summary>
/// A shift reads only the low 5 (Int32) or 6 (Int64) bits of its count, and wrapping + - * agree with exact
/// arithmetic on every low bit, so the arithmetic computing a count is modular (ADR 0013): `1 &lt;&lt; (row - c + n - 1)`
/// drops its overflow checks. Only the + - * spine of the count is wrapped; calls, indexes and casts inside it keep
/// their own checks.
/// </summary>
public sealed class ShiftCounts : MirRewriter, IMirPass
{
    public string Name => "shift-count";

    protected override MExpr Rewrite(MExpr e)
    {
        if (e is MBinary { Op: "<<" or ">>" or ">>>" } s && Arithmetic(s.R))
            s.R = new MCall { Name = "unchecked", Args = { s.R } };
        return e;
    }

    /// <summary>True when the count is an integer + - * tree over leaves (the only operators that can overflow).</summary>
    static bool Arithmetic(MExpr e) => e is MBinary { Op: "+" or "-" or "*" } b && Leaf(b.L) && Leaf(b.R);

    /// <summary>A name or literal: opaque code could hide an index whose arithmetic must keep trapping.</summary>
    static bool Leaf(MExpr e) => e is MCode { Text: var t } && t.Length > 0 && t.All(c => char.IsLetterOrDigit(c) || c == '_')
        || Arithmetic(e);
}
