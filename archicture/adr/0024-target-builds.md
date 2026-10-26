# ADR 0024: Target-aware builds (single-threaded lowering, trimmed wasm, NativeAOT, ReadyToRun)

Status: Accepted (0.13.2)

A wasm bundle carried the parallel paths of every loop, pipeline and fork (`Par.Range`, `Par.Fork`, helper methods,
serial clones) although the browser runtime has one thread: dead code to download, and closures the trimmer must keep.
NativeAOT and trimmed builds had no check that what the compiler emits survives the trimmer.

Decision:

- `Lowerer.Sequential` (set by `alice gen --sequential`, MSBuild `AliceSequential`): `ParallelOrSerial`,
  `TryParallelFor` and the fork emitters keep the serial form. The proofs still run, so a `par for` or `par a, b = ...`
  that is not provably independent is the same error on every target. Only the entry points change; the analyses do not.
- `kind = "wasm"` is sequential by default; `[build] threads = false` makes any package sequential.
- Wasm Release: `PublishTrimmed` + `TrimMode=full`, `WasmEnableSIMD`, `InvariantGlobalization` unless
  `[runtime] globalization` is set. `alice build --release` adds `RunAOTCompilation=true` when `dotnet workload list`
  shows `wasm-tools`; without it the IL is interpreted, as before.
- `[build] native = true`: `IsAotCompatible` and `IlcFoldIdenticalMethodBodies` (fork clones and generic instances
  often compile to identical code).
- ReadyToRun is opt-in (`[build] ready_to_run = true` with `--release`, or `alice build --r2r`): a framework-dependent
  publish to `bin/r2r` for the portable RID of this machine (distro runtimes report RIDs like `arch-x64` that have no
  runtime pack).

Not done: the parallel word-count idiom (`Lowerer.Idioms`) still emits its guarded parallel path; it is behind
`Par.Workers > 1` at run time, so a single-threaded runtime never takes it.

Tests: `Compiler/SequentialTargetTests` (same output, no `Par.`), `Tooling/TargetManifestTests`,
`Tooling/WasmCliTests`, and `Integration/TrimAotTests`, which publishes generated code plus Alice.Runtime with NativeAOT
and with wasm full trimming and requires zero IL2xxx/IL3xxx warnings.
