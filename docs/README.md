# Alice documentation

Grouped by what you are doing. File paths are stable; link to them directly.

## Start

| Page | Content |
|---|---|
| [getting-started.md](getting-started.md) | install, first program, first project |
| [releases.md](releases.md) | install scripts, manual install, nightlies, cutting a release |
| [../guide/GUIDE.md](../guide/GUIDE.md) | the short guide `alice guide` prints (people and AI agents) |

## Language

| Page | Content |
|---|---|
| [language.md](language.md) | reference: types, null safety, ownership (`own`, `borrow`, `scope`), sum types, `gpu for`, SoA records, concurrency, `par`, rules |
| [errors.md](errors.md) | every `AL` error code with an example and a fix |
| [design/memory-safety.md](design/memory-safety.md) | ownership design notes (ADR 0006, 0016) |

## Performance and memory

| Page | Content |
|---|---|
| [performance.md](performance.md) | what the compiler optimizes and how to check it (`alice emit`, `alice explain`) |
| [fork-join.md](fork-join.md) | fork/join of independent pure calls (ADR 0015) |
| [heap-gc.md](heap-gc.md) | GC choice, stack arrays, flat rows, what still allocates |
| [design/dotnet-performance-catalog.md](design/dotnet-performance-catalog.md) | .NET performance techniques and where Alice uses them |

## Tooling

| Page | Content |
|---|---|
| [tooling.md](tooling.md) | CLI, `alice.toml`, workspaces, tests, NuGet packages, WebAssembly packages, `alice prove`, REPL, toolchains, editors |
| [../guide/PROOFS.md](../guide/PROOFS.md) | rules, `PROOF.al`, `alice prove --smt` |
| [production.md](production.md) | what is ready for production, recommended settings, known gaps |

Architecture decisions and module notes: [../archicture/](../archicture/README.md).
