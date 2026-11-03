namespace Alice.Compiler.Mir;

/// <summary>
/// A member the lowering adds to the enclosing type next to the method it lowers (D0.7c): a parallel helper method
/// (`__par_*`, `__parfor_*`, `__fork_*`, ADR 0015) or a shared constant field (`__shared_*`). Typed, with a MIR body,
/// so the IL backend can emit it; the C# backend prints it (<c>Backend.Printer.Helper</c>) into
/// <c>Lowerer.Extra</c>, which keeps its order among the helpers still built as text. <c>Lowerer.TypedExtra</c> maps
/// each such Extra string (by reference) back to its declaration.
/// </summary>
public sealed class AirHelperDecl
{
    /// <summary>A helper method; its <see cref="AirMethodDecl.Body"/> has been through the MIR pass pipeline.</summary>
    public AirMethodDecl? Method;
    /// <summary>A static field, initialized by <see cref="FieldInit"/> (already optimized).</summary>
    public AirFieldDecl? Field;
    public MExpr? FieldInit;
    /// <summary>Attributes of the helper method (`[MethodImpl(AggressiveInlining)]`, `[UnmanagedCallersOnly]`).</summary>
    public List<AirAttribute> Attributes = new();
    /// <summary>The method needs an `unsafe` context (pointers, a function's address).</summary>
    public bool Unsafe;
    /// <summary>An expression body (`=> e;`) instead of <see cref="AirMethodDecl.Body"/>, printed as lowered.</summary>
    public MExpr? ExprBody;
}

/// <summary>A custom attribute: constructor arguments and named arguments (`CallConvs = ...`).</summary>
public sealed class AirAttribute
{
    public AirType Type = AirType.Unknown;
    public List<MExpr> Args = new();
    public List<(string Name, MExpr Value)> Named = new();
}
