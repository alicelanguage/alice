# CLAUDE.md

Follow `AGENTS.md (workspace root)` process rules (TDD, ADR, CHANGELOG, commits). This repo is .NET, not Rust (ADR 0001).

## Commands

```bash
dotnet build Alice.sln
dotnet test
dotnet run --project src/Alice.Cli -- run file.al
dotnet run --project src/Alice.Cli -- bootstrap
```

## Layout

| Path | Role |
|---|---|
| `src/Alice.Compiler` | stage0 compiler (C#) |
| `src/Alice.Runtime` | runtime library |
| `src/Alice.Cli` | `alice` entrypoint (thin) |
| `tests/Alice.Tests` | xUnit |
| `src/Alice.Runtime/Native/` | clang kernels + P/Invoke |
| `selfhost/` | compiler in Alice |

## Invariants

- Single backend: Alice -> C# -> Roslyn IL. No second backend.
- Self-hostable: every stage0 feature is ported to `selfhost/` in the same change; `alice bootstrap` must reach fixed point (S1 == S2) and match stage0 output on the corpus.
- Null-safety diagnostics are errors.
- Native kernels keep a managed fallback.
- TDD: failing test first.
- Version stays `0.10.x`-`0.19.x`.
- Never touch `archicture/user/`.

Docs: `archicture/` (ADRs, module notes), `docs/` (language, getting started).
