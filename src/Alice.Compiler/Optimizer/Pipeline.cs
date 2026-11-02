using Alice.Compiler.Air.Passes;
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
/// <summary>The backend the pipeline optimizes for: IL-only AIR passes (closure conversion) run only for <see cref="Il"/>.</summary>
public enum AirBackend { CSharp, Il }

public static class Pipeline
{
    /// <summary>
    /// The backend of the current compilation (thread-local, default C#). The C# backend gets exactly the passes of
    /// <see cref="Default"/>; under <see cref="AirBackend.Il"/> the passes of <see cref="Il"/> follow them on every body
    /// lowered while an IL method context is set (<see cref="ClosureConvert.Current"/>).
    /// </summary>
    [ThreadStatic] public static AirBackend Backend;

    /// <summary>IL-only passes, after <see cref="Default"/>: they replace what Roslyn lowers on the C# path. The IL
    /// lowerings (foreach, using, switch, patterns, tuples, interpolation, nullable) run first, closure conversion
    /// last, so it sees the smaller node set.</summary>
    public static readonly IReadOnlyList<Func<IMirPass>> Il =
    [
        () => new IlLowerStage(),
        () => new ClosureConvert(),
    ];

    /// <summary>What the IL lowerings left unsupported in the last body this thread ran through <see cref="Il"/>.</summary>
    [ThreadStatic] public static IlLowerContext? LastIlLowering;

    sealed class IlLowerStage : IMirPass
    {
        public string Name => "il-lower";
        public void Run(List<MStmt> body) => LastIlLowering = IlLowering.Run(body);
    }

    public static readonly IReadOnlyList<Func<IMirPass>> Default =
    [
        () => new ConstantFolding(),
        () => new StrengthReduction(),
        () => new LiteralMinMax(),
        () => new LoopTiling(),
        () => new InductionVariables(),
        () => new ShiftCounts(),
        () => new OwnedBounds(),
        () => new LoopUnroll(),
        () => new BranchlessSelect(),
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
        if (Backend == AirBackend.Il && ClosureConvert.Current != null)
            foreach (var make in Il)
            {
                var pass = make();
                if (!Disabled.Contains(pass.Name)) pass.Run(body);
            }
        MirDump.Captured(body);
    }
}
