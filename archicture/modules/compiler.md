# Alice.Compiler
Path: `src/Alice.Compiler`. Pure C#. Depends on: Roslyn, `Alice.Runtime` (reference for emitted code).

| Folder | Stage | Output |
|---|---|---|
| `Syntax/` | `Lexer`, `Parser/`, `Ast/` | AST |
| `Semantics/` | `Binder/`, `Symbols/`, `Types/` | HIR: types, symbols, inferred returns, hoisted locals |
| `Lowering/` | `Lowerer*` | MIR (`Mir/`): inlined/fused loops, typed temps |
| `Backend/` | `Emitter`, `Printer`, `AliceCompiler` | C# with `#line` -> Roslyn Release IL |

Lowering files (`Lowering/`):

| File | Role |
|---|---|
| `Lowerer.Iterators` | inline literal blocks, fuse chains |
| `Lowerer.Vectorize` | `Vector<T>` numeric pipelines |
| `Lowerer.Parallel` | pure pipelines -> `Par.Reduce`/`Par.Range` (n >= 65536) |
| `Lowerer.LoopPar` | counted `for` loops: reduction, `arr[i]` writes, pure calls |
| `Lowerer.Idioms` | span Split, alternate lookup, single-probe dict increment |
| `Lowerer.SoA` | `@[SoA]` column loops (store: `Backend/SoAStore`) |

`Semantics/Binder/Binder.Races`: AL0202 race check (ADR 0008). `Syntax/Formatter`: `alice fmt`.

Invariants:
- Single IL backend (ADR 0001, 0007).
- Literal-block iterator kernels allocate 0 bytes (`AllocationTests`).
- Null-safety diagnostics are errors; `val` reassignment and data races in concurrent contexts are errors (ADR 0004, 0008).
- Parallelization only for code proven pure; serial otherwise.
