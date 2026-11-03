namespace Alice.Compiler.Mir;

// AIR nodes of the `gpu for` lowering (Lowerer.Gpu, ADR 0021). Printed by Backend/Printer.Parallel.cs, dumped by
// Mir/MirDump.Parallel.cs; children are frozen like every AirParExpr.

/// <summary>
/// The next index of a grid-stride loop, clamped to the count so it never overflows Int32:
/// `count - index > stride ? index + stride : count`.
/// </summary>
public sealed class AirGridStep : AirParExpr
{
    public MExpr Index = null!;
    public MExpr Count = null!;
    public MExpr Stride = null!;

    public override IEnumerable<MExpr> Children() => [Index, Count, Stride];
}
