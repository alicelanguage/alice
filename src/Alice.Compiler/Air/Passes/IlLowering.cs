using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// The lowering the IL backend needs where the C# backend relies on Roslyn (ADR 0032/0033, backend plan D1.7).
/// Runs only when the IL backend emits a body, after the MIR optimization pipeline; the C# backend never calls it,
/// so C# output is untouched. After it, a body holds only the node set AirToIl emits (see
/// <see cref="IlLowerContext.Unsupported"/> for what was left as is). Order matters: foreach and using produce
/// try/finally and deconstructions; switch chains are recognized before patterns, tuples and nullable operators
/// are rewritten inside them.
/// </summary>
public static class IlLowering
{
    public static readonly IReadOnlyList<Func<IlLowerContext, IMirPass>> Passes =
    [
        c => new ForeachLower(c),
        c => new UsingLockLower(c),
        c => new SwitchLower(c),
        c => new PatternTemps(c),
        c => new TupleLower(c),
        c => new InterpLower(c),
        c => new NullableLift(c),
    ];

    /// <summary>Lowers <paramref name="body"/> in place; the context lists what the IL backend must still reject.</summary>
    public static IlLowerContext Run(List<MStmt> body, IlLowerContext? ctx = null)
    {
        ctx ??= new IlLowerContext();
        foreach (var make in Passes) make(ctx).Run(body);
        return ctx;
    }
}
