using System.Text;

namespace Alice.Cli.Projects;

/// <summary>
/// MSBuild properties that depend on where the program runs (ADR 0024): the browser (single-threaded, trimmed, SIMD),
/// NativeAOT, ReadyToRun. Kept apart from <see cref="Manifest"/>, which maps the package itself.
/// </summary>
public static class TargetProperties
{
    /// <summary>(always, Release-only) property lines for a package of <paramref name="kind"/>.</summary>
    public static (string Props, string Release) Render(string kind, IReadOnlyDictionary<string, object> build, IReadOnlyDictionary<string, object> runtime)
    {
        var props = new StringBuilder();
        var release = new StringBuilder();
        bool wasm = kind == "wasm";
        // no threads (the browser runs .NET on one): `alice gen --sequential` lowers parallel code to serial loops
        bool threads = build.TryGetValue("threads", out var th) ? th is true : !wasm;
        if (!threads) props.Append("    <AliceSequential>true</AliceSequential>\n");
        if (build.TryGetValue("native", out var nat) && nat is true)
            // fold identical bodies: generic instantiations and the serial clones of forks often compile to the same code
            props.Append("    <PublishAot>true</PublishAot>\n    <OptimizationPreference>Speed</OptimizationPreference>\n" +
                         "    <IsAotCompatible>true</IsAotCompatible>\n    <IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>\n");
        if (build.TryGetValue("ready_to_run", out var r2r) && r2r is true)
            // precompiled code: startup without JIT; `alice build --release` publishes it to bin/r2r
            props.Append("    <PublishReadyToRun>true</PublishReadyToRun>\n");
        if (wasm)
        {
            // the download is the startup cost: trim everything unreachable, and use wasm SIMD (Vector128 paths)
            release.Append("    <WasmEnableSIMD>true</WasmEnableSIMD>\n    <PublishTrimmed>true</PublishTrimmed>\n    <TrimMode>full</TrimMode>\n");
            // ICU data is the largest file of a bundle; culture rules only when alice.toml asks for them
            if (!runtime.ContainsKey("globalization")) release.Append("    <InvariantGlobalization>true</InvariantGlobalization>\n");
        }
        return (props.ToString(), release.ToString());
    }
}
