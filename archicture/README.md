# Architecture map

| Path | Content |
|---|---|
| `user/` | Permanent user direction. Read-only for agents. |
| `adr/` | Architecture decisions (monotonic numbers). |
| `modules/` | Boundary notes per project. |

## ADRs

| # | Decision |
|---|---|
| 0001 | .NET platform language; single Roslyn/IL backend |
| 0002 | Separate runtime (amended: native kernels removed, pure C#) |
| 0003 | Self-hosting bootstrap (superseded by 0007; deferred) |
| 0004 | Safety and typing |
| 0005 | Concurrency model |
| 0006 | Ownership as static layer on .NET GC (proposed) |
| 0007 | HIR/MIR pipeline for allocation-free IL |
| 0008 | Compile-time race safety (AL0202) |
| 0009 | [Tested CLI distribution archives](adr/0009-release-distribution.md) |
| 0010 | [Toolchain manager, installers and nightlies](adr/0010-toolchains-and-updates.md) |

## Projects

| Project | Role | Note |
|---|---|---|
| `src/Alice.Runtime` | Runtime library (SIMD, sparse, Par, math, concurrency, prelude) | `modules/runtime.md` |
| `src/Alice.Compiler` | Compiler: Syntax -> Semantics -> Lowering/MIR -> Backend (Roslyn IL) | `modules/compiler.md` |
| `src/Alice.Cli` | `alice` command |
| `src/Alice.Sdk` | MSBuild props/targets for `.alproj` | `docs/tooling.md` | `modules/cli.md` |
| `tests/Alice.Tests` | xUnit tests | - |

Dependency direction: `Runtime <- Compiler <- Cli`. Runtime depends on no other project.
