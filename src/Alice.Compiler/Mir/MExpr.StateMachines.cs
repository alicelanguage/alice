namespace Alice.Compiler.Mir;

// Statement nodes of the IL-only state-machine pass (Air/Passes/StateMachines.cs, backend plan D1.8). They exist only
// in MoveNext/Dispose bodies built for the IL backend; the C# backend never sees them (Roslyn builds its own state
// machines from `async`/`yield`).

/// <summary>A branch target in the enclosing method body (IL: a marked label).</summary>
public sealed class AirLabel : MStmt
{
    public string Name = "";
}

/// <summary>
/// An unconditional jump to <see cref="Label"/>. <see cref="Leave"/>: the label lies outside at least one try block
/// that encloses the jump, so IL needs `leave` (enclosing finally blocks run); otherwise `br`. A jump never enters a
/// try block: the pass places the label before the `try` and dispatches again inside it.
/// </summary>
public sealed class AirGoto : MStmt
{
    public string Label = "";
    public bool Leave;
}

/// <summary>
/// IL `switch`: jumps to <c>Labels[Value - Min]</c>; a null slot or a value out of range falls through to the next
/// statement. <see cref="Value"/> is an Int32. The labels are in the same protected region as the table.
/// </summary>
public sealed class AirGotoTable : MStmt
{
    public MExpr Value = null!;
    public int Min;
    public List<string?> Labels = new();
}
