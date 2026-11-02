namespace Alice.Compiler.Mir;

// Helper nodes of the IL-only lowering passes (Air/Passes, backend plan D1.7). They appear only in bodies lowered for
// the IL backend: the C# printer never sees them. Kept to the three shapes the passes cannot express otherwise.

/// <summary>
/// Block expression: runs <see cref="Pre"/> in order, then evaluates <see cref="Value"/> as the result. <see cref="Pre"/>
/// holds only straight-line statements (MDecl, MAssign, MExprStmt, MIf without break/continue/return), so the IL
/// emitter lowers it in place with whatever is already on the evaluation stack. <see cref="MExpr.Type"/> is the
/// type of <see cref="Value"/>.
/// </summary>
public sealed class AirSeq : MExpr
{
    public List<MStmt> Pre = new();
    public MExpr Value = null!;
}

/// <summary>`E as T` for a reference type T: `isinst T`, null when the value is not a T (no exception).</summary>
public sealed class AirAs : MExpr
{
    public string TypeCs = "";
    public MExpr E = null!;
}

/// <summary>
/// Dense jump table over an integral <see cref="Value"/>: slot <c>Value - Min</c> (computed in the type of
/// <see cref="Value"/>, compared unsigned against <c>Table.Count</c>) selects <c>Cases[Table[slot]]</c>; a slot of -1
/// or a value outside the table runs <see cref="Default"/> (nothing when null). Cases never fall through. It is not
/// a break/continue target: <see cref="MBreak"/> in a case leaves the enclosing loop, as in the MIf chain it replaces.
/// </summary>
public sealed class AirSwitch : MStmt
{
    public MExpr Value = null!;
    public long Min;
    public List<int> Table = new();
    public List<List<MStmt>> Cases = new();
    public List<MStmt>? Default;
}
