# ADR 0034: WebAssembly target without the .NET runtime (`runtime = "alice"`)

Status: Accepted (1.0 backend direction; not yet implemented). Extends ADR 0023 and ADR 0024.

`kind = "wasm"` (ADR 0023) ships the .NET WebAssembly runtime: 1.5-2.5 MB to download and 300-800 ms to start, and
AOT only works with the wasm-tools workload. Rust ships 10-40 KB. Pure Alice code needs none of the .NET runtime.

Decision:

- `[package] kind = "wasm"` takes `[build] runtime = "alice" | "dotnet"`:
  - **`runtime = "dotnet"`** is today's path (ADR 0023/0024), unchanged. It is required for BCL-wide code, reflection
    and `dynamic`.
  - **`runtime = "alice"`** compiles AIR (ADR 0032) straight to a wasm module with a thin runtime written in Alice
    (`src/Alice.Core.Wasm`). It becomes the default for new wasm packages once it passes its gates.
  - Rejected: NativeAOT-LLVM (experimental, still carries the .NET runtime) and a hybrid per package.
- Memory model: **linear memory with an own GC**. A size-class allocator and bump arenas. Owned arrays and `scope`
  values (ADR 0016) are not GC-managed. A non-moving mark-sweep collector finds roots through a compiler-maintained
  shadow stack. WasmGC may come later behind the same AIR: it forbids interior pointers, linear views and SIMD loads
  from GC arrays, and has no standard string.
- Exceptions use wasm EH (`exnref`); `--panic=abort` drops them. Strings are UTF-8 from Alice Core. Math comes from a
  musl libm port, and its last-ulp float differences from .NET are documented (ADR 0013). The first runtime is
  single-threaded; `par`/`gpu` forms take their serial form (ADR 0024 `Sequential`).
- **Core profile first**: numbers, Bool, Char, UTF-8 String, arrays/Span, structs, classes without .NET bases,
  interfaces, enums, sum types, closures, iterators, Alice Core collections, exceptions, `own`/`scope`, stack arrays,
  flat rows, SIMD128 vectorization, `@[JSImport]`/`@[JSExport]`. Excluded at first: `dynamic`, reflection, async
  (phase 2), BCL types outside Alice Core, threads, `lib` FFI, and `@[Native]` C (phase 2, through clang wasm32). Using
  an excluded feature is a target-conditional compile error that suggests `runtime = "dotnet"`.
- Interop: `@[JSImport]`/`@[JSExport]` keep their syntax. The backend generates the wasm imports/exports and a
  `<pkg>.js` ESM glue file (UTF-8 strings, typed-array views, a handle table). There is no .NET source generator on
  this path.
- WASI preview 1 through `--target wasm32-wasi`. `alice run` uses wasmtime or Node. DWARF and source maps come from AIR
  spans. Binaryen `wasm-opt` comes through `alice toolchain` (ADR 0010); the module is valid without it.
- Targets, measured on the forge (Node and headless Chromium) by `benchmarks/wasm` against Blazor and Rust:

| Metric | Target |
|---|---|
| hello world download | <= 50 KB |
| thin runtime | <= 150 KB |
| time to first call | < 20 ms |
| nbody, editdist | <= 1.3x Rust wasm |
| build | < 3 s |

Consequences: a third printer (`AirToWasm`) and a runtime in Alice to maintain. Floating-point results may differ in the
last ulp from .NET for transcendental functions. Packages that need the BCL stay on `runtime = "dotnet"`.

Tests: `WasmTests`/`WasmCliTests` run both runtimes. Corpus programs in the core profile give identical output on
`runtime = "alice"` and on .NET. The size/startup/perf harness fails a build that misses a target. Diagnostics tests
cover each excluded feature.
