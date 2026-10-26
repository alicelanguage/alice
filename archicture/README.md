# Architecture map

| Path | Content |
|---|---|
| `user/` | Permanent user direction. Read-only for agents. |
| `adr/` | Architecture decisions (monotonic numbers). |
| `modules/` | Boundary notes per project. |

## ADRs

| # | Decision |
|---|---|
| 0001 | [.NET platform language, single Roslyn/IL backend](adr/0001-dotnet-platform-language.md) |
| 0002 | [Separate runtime and native kernels](adr/0002-separate-runtime-and-native-kernels.md) (amended: native kernels removed, pure C#) |
| 0003 | [Self-hosting bootstrap](adr/0003-self-hosting-bootstrap.md) (superseded by 0007; deferred) |
| 0004 | [Safety and typing](adr/0004-safety-and-typing.md) |
| 0005 | [Concurrency model](adr/0005-concurrency-model.md) |
| 0006 | [Ownership as a static layer on the .NET GC](adr/0006-ownership-on-dotnet-gc.md) (proposed; arrays implemented by 0016) |
| 0007 | [HIR/MIR pipeline for allocation-free IL](adr/0007-hir-mir-pipeline.md) |
| 0008 | [Compile-time data-race safety](adr/0008-compile-time-race-safety.md) |
| 0009 | [Tested CLI distribution archives](adr/0009-release-distribution.md) |
| 0010 | [Toolchain manager, installers and nightlies](adr/0010-toolchains-and-updates.md) |
| 0011 | [Shared CLI language server and Rust editor adapters](adr/0011-editor-language-server.md) |
| 0012 | [Alice-authored Wonderland3D film sample](adr/0012-wonderland3d-film-sample.md) |
| 0013 | [Exact numeric semantics and workstation GC default](adr/0013-numeric-semantics.md) |
| 0014 | [MIR pass pipeline](adr/0014-mir-pass-pipeline.md) |
| 0015 | [Fork/join of independent pure calls](adr/0015-fork-join-of-pure-calls.md) |
| 0016 | [Ownership of local arrays and pooled buffers](adr/0016-ownership-and-pooled-buffers.md) |
| 0017 | [Explicit, checked paradigms over inference](adr/0017-explicit-paradigms.md) |
| 0018 | [FFI through blittable imports behind inlined wrappers](adr/0018-ffi-blittable-wrappers.md) |
| 0019 | [.NET names for a library's public API](adr/0019-dotnet-naming.md) |
| 0020 | [Alice on NuGet](adr/0020-nuget-distribution.md) |
| 0021 | [`gpu for` loops through ILGPU](adr/0021-gpu-loops.md) |
| 0022 | [`alice prove --smt` through Z3](adr/0022-smt-proofs.md) |
| 0023 | [WebAssembly packages (`kind = "wasm"`)](adr/0023-wasm-packages.md) |

## Projects

| Project | Role | Note |
|---|---|---|
| `src/Alice.Runtime` | Runtime library (SIMD, sparse, Par, math, concurrency, prelude) | `modules/runtime.md` |
| `src/Alice.Compiler` | Compiler: Syntax -> Semantics -> Lowering/MIR -> Backend (Roslyn IL) | `modules/compiler.md` |
| `src/Alice.Cli` | `alice` command | `modules/cli.md` |
| `src/Alice.Sdk` | MSBuild props/targets for `.alproj` | `docs/tooling.md` |
| `tests/Alice.Tests` | xUnit tests | - |

Dependency direction: `Runtime <- Compiler <- Cli`. Runtime depends on no other project.
