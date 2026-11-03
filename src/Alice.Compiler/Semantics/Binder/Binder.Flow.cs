using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Flow checks the binder owns (D1.12, ADR 0033), so they hold on every backend and not only where Roslyn re-checks
/// the generated C#: nullable flow (AL0310, Binder.Flow.Nil.cs), use before assignment (AL0309) and compile-time
/// constant overflow (AL0300, Binder.Flow.Const.cs). Same codes and texts as the C# path's translation of
/// CS8600-CS8625 / CS0165 / CS0220 (Diagnostics/CSharpErrors.cs); the differential harness is Diagnostics/FlowOracle.cs.
/// Non-nullable fields never assigned by a constructor are AL0221 (Binder.NullSafety.cs).
/// </summary>
public sealed partial class Binder
{
    public const string NilCode = "AL0310";
    public const string UnassignedCode = "AL0309";
    public const string NilText = "value may be nil here; check it (`if x`, `x.nil?`, `x || default`) or assert with `x.not_nil!`";

    /// <summary>Flow diagnostics (AL0309, AL0310, AL0300 constant overflow), in source order per file.</summary>
    public readonly List<Diagnostic> FlowErrors = new();

    /// <summary>Collect <see cref="FlowErrors"/> without throwing from <see cref="Bind"/> (the C# path while Roslyn
    /// still reports these; the oracle harness).</summary>
    public bool FlowShadow;

    readonly HashSet<(string File, int Line, int Col, string Code)> flowSeen = new();

    void FlowError(string path, Node at, string code, string msg)
    {
        if (flowSeen.Add((path, at.Line, at.Col, code)))
            FlowErrors.Add(new Diagnostic("error", code, path, at.Line, Math.Max(1, at.Col), msg));
    }

    void CheckFlow()
    {
        foreach (var f in Files)
        {
            new NilFlow(this, f.Path).File(f);
            CheckConstants(f);
        }
    }
}
