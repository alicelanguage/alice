# Alice 1.0 roadmap

Status: completed. Alice 1.0.0 shipped on 2026-11-04 ([CHANGELOG.md](../CHANGELOG.md)); this is the historical plan,
drafted by the CTO agent on 2026-11-02. Not in 1.0 and planned for 1.1: signed and notarized archives
([releases.md](releases.md#10-release-checklist)) and compile-time regex emission (ADR 0036 amendment 2). Direction from the lead (verbatim goals in `archicture/user/` are
untouched; this file is the engineering plan). Baseline: v0.14.0 (`b82a902`), 1484 tests, 2 failing on Linux x64.

## The promise

Secure as Rust, fast as C, parallel as Bend, easy as Ruby. One language from low level (FFI, `@[Native]`, unsafe,
stack/arena memory) to high level (web, desktop, games, data), on .NET and on WebAssembly, with a standard library
that is better than the BCL where the BCL is weak (regex, JSON, HTTP, text). The
compiler stays C# and emits its own optimized .NET IL.

## Workstreams

| # | Stream | 1.0 exit criterion |
|---|---|---|
| A | Benchmarks vs Bend 2 / C | All 16 `bench/runtime` workloads of bendlang/bend ported 1:1 to Alice; harness measures 1 core and all cores against the C versions; Alice beats Bend PAR-CPU and matches C SEQ on every workload or the gap has a named cause and a tracked fix. Gap fixes: affine arena sum types (ADR 0037) for the tree trio, merkle, symreg, kmeans, hashmap; CM primitives (ADR 0039) for histogram, kmeans, mandelbrot, terrain; a second, idiomatic (non-1:1) column in `benchmarks/bend2` beside the 1:1 gate (ADR 0039) |
| B | Security and feature audit | Fable review of compiler, runtime, CLI, installers, telemetry, package/toolchain fetch, FFI, `unsafe`, proofs; every finding fixed or documented as a conscious limit in `docs/production.md` |
| C | Core library ("Alice Core") | Own, allocation-free, SIMD regex (compile-time when the pattern is literal), SIMD JSON (parse/serialize, zero-copy), text (UTF-8 strings, spans), HTTP/1.1+2 client and server, sockets, fs, process, time, crypto digests, CSV, TOML; each beats the BCL equivalent on the suite and is documented. Streaming (HTTP, JSON/CSV, logs) through static dataflow networks (`dataflow do`, `stream def`; ADR 0038), shipped in 1.0 (Wave 3) |
| D | Alice IR and .NET IL backend | A typed, SSA-like Alice IR (AIR) between MIR and the targets; direct IL emission (System.Reflection.Metadata) with PDB; the C# backend stays for interop readability and as the differential oracle; bit-identical results on the whole test corpus |
| E | WebAssembly as a first-class target | AIR -> wasm (GC proposal or linear memory) without the .NET wasm runtime for pure Alice code; `kind = "wasm"` keeps the .NET interop path; browser and WASI; size and startup measured against the .NET and Rust wasm outputs |
| F | IL backend quality | Emitted IL within 3% of hand-written C# on the suite, ILVerify clean, PDB, NativeAOT, deterministic (ADR 0033) |
| G | Language: Crystal and Ruby completeness | Gap list vs Crystal and Ruby (macros, union types, enums with values, named args, splats, `select`, `with`, pattern matching, string formatting, ranges, `Comparable`, `Enumerable` protocol, exceptions hierarchy, `ensure`, `retry`, operator overloading, `method_missing` static, refinements) closed or rejected with a reason in `docs/language.md` |
| H | Domains | Verified samples and docs for: network (HTTP service, websocket, TCP), desktop (Avalonia app with packaging), games (MonoGame, raylib, Celeste), CLI tools, data (CSV/JSON/EF Core), low level (FFI, native kernels, arenas) |
| I | Product | Docs site complete, `alice guide` complete, installers signed/notarized, Linux arm64 + Alpine archives, VS Code/Zed/JetBrains extensions, LSP, playground, release notes, 1.0 announcement |

## Order

Wave 1 (now): A harness + ports, B audit, G gap list, D/E design (ADRs), fix the 2 failing tests.
Wave 2: D1 IL backend S1-S3 (ADR 0033), A gap fixes (arena/struct trees, branchy integer codegen, startup), C regex +
JSON, G features.
Wave 3: D1 IL backend S4-S5 + F quality gate, E wasm backend (ADR 0034), C networking + dataflow (ADR 0038), H samples.
Wave 4: I product, 1.0 release candidate, soak.

## Machines

- `alice-forge` (EC2 c7i.8xlarge, Arch, 32 cores): builds, tests, benchmarks. Repo at `/work/alice`, worktrees under `/work/wt/`.
- macOS: GitHub Actions `macos-15` runners (CI); the lab Mac has no inbound access and is not used.
- Windows: GitHub Actions `windows-2025` (CI).

## Rules for this program

- TDD, ADR for each architectural change, CHANGELOG per change, `dotnet test` green before merge.
- Benchmarks report cold-process and warm numbers, with checksums, on the forge; never compare across machines.
- Never parallelize unproven code; never trade safety for a benchmark.
