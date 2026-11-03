using System.Runtime.CompilerServices;

namespace Alice.Compiler.Backend.Il.Opt;

/// <summary>Switches of the IL-side optimizer (il-opt). `ALICE_IL_OPT` = `0` (all off), or a comma list of
/// `-name` / `+name` edits of the default set (all on), e.g. `-inline,-noao`; a leading `0` starts from none
/// (`0,+peephole`).</summary>
[Flags]
public enum IlOptPass
{
    None = 0,
    /// <summary>R9: [AggressiveInlining] on small loop-free non-virtual methods (<see cref="MethodFlags"/>).</summary>
    Inline = 1,
    /// <summary>R10: no [AggressiveOptimization] (keeps PGO and static-readonly folding). Off by default: it measured
    /// 3-13 % slower on bend2 serial (<see cref="MethodFlags"/>); `+noao` turns it on.</summary>
    NoAo = 2,
    /// <summary>ADR 0033 peepholes beyond IlBuilder's set (Backend/Il/Opt/IlBuilder.Opt.cs).</summary>
    Peephole = 4,
    /// <summary>R2: proven-in-bounds array accesses without bounds checks (Air/Passes/IlOpt/InBounds.cs).</summary>
    InBounds = 8,
    /// <summary>Overflow checks proven dead by the same intervals (Air/Passes/IlOpt/InBounds.cs).</summary>
    NoOverflow = 16,
    /// <summary>R10 per shape: no [AggressiveOptimization] on loop-free, non-recursive bodies (the hot policy's loop
    /// leaves such as `prng`, `dst`, `word_prng`); methods with a loop and recursive kernels keep it.</summary>
    NoAoLeaves = 32,
    /// <summary>R10 per shape, experiment: [AggressiveOptimization] on every loop-carrying body the hot policy left
    /// tiered (e.g. a primitive loop that only calls helpers, kmeans `chunk`). Off by default (see <see cref="MethodFlags"/>).</summary>
    AoLoops = 64,
    /// <summary>Loop-carried store-to-load forwarding over proven-in-bounds fresh arrays (Air/Passes/IlOpt/StoreForward.cs).</summary>
    Forward = 128,
    /// <summary>The default set.</summary>
    All = Inline | Peephole | InBounds | NoOverflow | Forward,
}

/// <summary>
/// The IL-side optimizer (ADR 0033 quality targets, reports/il-shapes.md rules): MIR passes run per body after
/// IlLowering (Air/Passes/IlOpt) and method-level rewrites over the emitted module before encoding (this folder).
/// Registered into <see cref="IlPipeline"/> when the compiler assembly loads; the C# backend never sees them.
/// </summary>
public static class IlOpt
{
    /// <summary>The enabled passes (from `ALICE_IL_OPT` at load; tests may set it).</summary>
    public static IlOptPass Enabled { get; set; } = Parse(Environment.GetEnvironmentVariable("ALICE_IL_OPT"));

    public static bool On(IlOptPass p) => (Enabled & p) == p;

    public static IlOptPass Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return IlOptPass.All;
        if (spec.Trim() == "0") return IlOptPass.None;
        var r = IlOptPass.All;
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "0") { r = IlOptPass.None; continue; }   // `0,+inline`: start from nothing
            bool off = raw[0] == '-';
            string n = raw.TrimStart('-', '+');
            if (!Enum.TryParse<IlOptPass>(n.Replace("-", ""), ignoreCase: true, out var p)) continue;
            r = off ? r & ~p : r | p;
        }
        return r;
    }

#pragma warning disable CA2255 // the hook lists live in this assembly; registering at load is the point
    [ModuleInitializer]
    internal static void Register()
    {
        IlPipeline.AfterLowering.Add(_ => new Air.Passes.IlOpt.InBounds());
        IlPipeline.AfterLowering.Add(_ => new Air.Passes.IlOpt.StoreForward());
        IlPipeline.BeforeEncode.Add(MethodFlags.Apply);
    }
#pragma warning restore CA2255
}
