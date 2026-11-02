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
}
