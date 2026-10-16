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
| 0002 | Separate runtime; clang/LLVM native kernels via P/Invoke |
| 0003 | Self-hosting bootstrap with fixed point |
| 0004 | Safety and typing |
| 0005 | Concurrency model |
| 0006 | Ownership as static layer on .NET GC (proposed) |

## Projects

| Project | Role | Note |
|---|---|---|
| `src/Alice.Runtime` | Runtime library (SIMD, sparse, Par, math, native, prelude) | `modules/runtime.md` |
| `src/Alice.Compiler` | Stage0 compiler: Alice -> C# -> Roslyn IL | `modules/compiler.md` |
| `src/Alice.Cli` | `alice` command | `modules/cli.md` |
| `tests/Alice.Tests` | xUnit tests | - |
| `src/Alice.Runtime/Native/` | P/Invoke + clang kernels | ADR 0002 |
| `selfhost/` | Compiler in Alice (`*.al`) | ADR 0003 |

Dependency direction: `Runtime <- Compiler <- Cli`. Runtime depends on no other project.
