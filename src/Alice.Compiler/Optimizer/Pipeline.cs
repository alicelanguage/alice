using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer.Passes;

namespace Alice.Compiler.Optimizer;

/// <summary>One MIR optimization: rewrites a method body in place.</summary>
public interface IMirPass
{
    string Name { get; }
    void Run(List<MStmt> body);
}

/// <summary>
/// MIR pass pipeline (ADR 0014), run on every lowered body before it is printed to C#. Lowering stays a direct
/// translation; optimizations live here as small primitive passes, each written once. Order matters: folding
/// exposes constants to strength reduction. `ALICE_DISABLE_PASSES=const-fold,induction` switches passes off
/// for A/B measurements.
/// </summary>
public static class Pipeline
{
    public static readonly IReadOnlyList<Func<IMirPass>> Default =
    [
        () => new ConstantFolding(),
        () => new StrengthReduction(),
        () => new LiteralMinMax(),
        () => new InductionVariables(),
        () => new ShiftCounts(),
    ];

    static readonly HashSet<string> Disabled = (Environment.GetEnvironmentVariable("ALICE_DISABLE_PASSES") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    public static void Run(List<MStmt> body)
    {
        foreach (var make in Default)
        {
            var pass = make();
            if (!Disabled.Contains(pass.Name)) pass.Run(body);
        }
    }
}
