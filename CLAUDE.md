# CLAUDE.md

Follow `AGENTS.md (workspace root)` process rules (TDD, ADR, CHANGELOG, commits). This repo is .NET, not Rust (ADR 0001).

## Commands

```bash
dotnet build Alice.sln
dotnet test
dotnet run --project src/Alice.Cli -- run file.al
dotnet run --project src/Alice.Cli -- emit file.al
```

## Layout

| Path | Role |
|---|---|
| `src/Alice.Compiler/Syntax` | lexer, parser, AST |
| `src/Alice.Compiler/Semantics` | binder (incl. `Binder.Races`, AL0202), symbols, types (HIR) |
| `src/Alice.Compiler/Lowering` | AST/HIR -> MIR: Iterators, Vectorize, Parallel, LoopPar, Fork (ADR 0015), StackArrays, Idioms, SoA, SoAFusion, Simt |
| `src/Alice.Compiler/Mir` | MIR nodes |
| `src/Alice.Compiler/Optimizer` | MIR pass pipeline (ADR 0014): `MirRewriter`, `MirFacts`, `Passes/` (const-fold, strength-reduce, literal-minmax, induction) |
| `src/Alice.Compiler/Backend` | `Il/` direct IL + PDB emitter (AirToIl, IlBuilder); C# printer + Roslyn (differential oracle) |
| `src/Alice.Runtime` | runtime library (pure C#) |
| `src/Alice.Cli` | `alice` entrypoint (thin): `Commands/`, `Projects/` (alice.toml, workspace, lock) |
| `src/Alice.Sdk` | MSBuild props/targets for `.alproj` |
| `samples/` | workspace (`alice.toml`): interop, MonoGame (BounceGame, Breakout) |
| `benchmarks/` | Alice vs idiomatic and hand-optimized C# (`README.md`, run 5) |
| `tests/Alice.Tests` | xUnit: `Compiler/`, `Runtime/`, `Tooling/`, `Integration/`, `Architecture/` |

## Invariants

- IL backend is the default target at 1.0 (Alice -> MIR/AIR -> IL, ADR 0033); the C# printer (MIR -> C# -> Roslyn, ADR 0007) remains the differential oracle until removed. Priority: best .NET IL.
- All tests stay green (`dotnet test`) before commit.
- Allocation tests must stay at 0 bytes (`AllocationTests`).
- `alice.toml` is the project source of truth; generated `.alproj` lives in `.alice/` (gitignored). No `sln` command.
- Every guarantee in `INVARIANTS.md` has a pinning test; change it only with the test and a CHANGELOG line.
- Null-safety, `val` reassignment, data races in concurrent contexts (AL0202, ADR 0008) are compile errors.
- Race checks and the purity gate decide parallelization: never parallelize code not proven pure.
- Compiler and runtime are pure C# (ADR 0035 withdrawn: no self-hosting). Native code only where the user opts in: `@[Native]` functions (C via clang, managed fallback) and `lib`/`fun` imports.
- TDD: failing test first.
- Version is `MAJOR.MINOR.PATCH` from 1.0.0 on, release candidates `-rc.N` first (`.github/scripts/release-version.ps1`); no more 0.x releases.
- Never touch `archicture/user/`.

Docs: `archicture/` (ADRs, module notes), `docs/` (language, errors, performance, tooling, getting started).
