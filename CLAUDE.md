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
| `src/Alice.Compiler/Semantics` | binder, symbols, types (HIR) |
| `src/Alice.Compiler/Lowering` | AST/HIR -> MIR |
| `src/Alice.Compiler/Mir` | MIR nodes |
| `src/Alice.Compiler/Backend` | C# emitter/printer, Roslyn |
| `src/Alice.Runtime` | runtime library (pure C#) |
| `src/Alice.Cli` | `alice` entrypoint (thin) |
| `tests/Alice.Tests` | xUnit |

## Invariants

- Single backend: Alice -> MIR -> C# -> Roslyn IL (ADR 0007). Priority: best .NET IL.
- Literal-block iterator kernels allocate 0 bytes (`AllocationTests`).
- Null-safety, `val` reassignment, spawn data races are compile errors.
- Compiler and runtime are pure C#; no native code.
- TDD: failing test first.
- Version stays `0.10.x`-`0.19.x`.
- Never touch `archicture/user/`.

Docs: `archicture/` (ADRs, module notes), `docs/` (language, getting started).
