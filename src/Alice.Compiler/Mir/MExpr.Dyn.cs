using Alice.Runtime;

namespace Alice.Compiler.Mir;

/// <summary>
/// One dynamic operation on an `Any` value (IL backend only, ADR 0033; built by Air/Passes/DynSites.cs): a call of
/// <see cref="Dyn"/> through the module's static <see cref="DynSite"/> number <see cref="SiteId"/>. Never reaches the
/// C# printer (the C# backend keeps C# `dynamic`).
/// </summary>
/// <remarks>
/// Operands by kind: Call/Invoke/GetIndex: <see cref="Target"/> and the arguments; GetMember: Target only; SetMember:
/// Args = [value]; SetIndex: Args = [indices..., value]; BinaryOp: Target = left, Args = [right]; UnaryOp, Convert,
/// Cast: Target = the operand. <see cref="MExpr.Type"/> is `dynamic` (an object on the stack), the static type
/// <see cref="To"/> for Convert/Cast, void for SetMember/SetIndex.
/// </remarks>
public sealed class AirDynCall : MExpr
{
    public int SiteId;
    public DynKind Kind;
    /// <summary>The member name as lowered (`foo`, `empty_q`, `Length`); empty for operators, conversions and invokes.</summary>
    public string Name = "";
    public DynOp Op;
    /// <summary>`x&amp;.name`: a nil receiver yields nil.</summary>
    public bool Safe;
    public MExpr? Target;
    public List<MExpr> Args = new();
    /// <summary>The target type of a Convert/Cast.</summary>
    public AirType? To;
}
