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
| `Lowerer.Idioms` | span Split, alternate lookup, single-probe dict increment, parallel word count |
| `Lowerer.SoA` | `@[SoA]` column loops (store: `Backend/SoAStore`) |
| `Lowerer.SoAFusion` | fuse row-local SoA passes + loop interchange, registers, fused `sum` |
| `Lowerer.Simt` | SIMT lanes for counted loops with a data-dependent `while` |
| `Lowerer.Fork` | fork/join of independent pure recursive calls, serial clones (ADR 0015) |
| `Lowerer.Effects` | `pure def` / `safe def` checks (AL0210, AL0212), explicit `par a, b = ...` (AL0211) |
| `Lowerer.Ownership` | owned arrays, moves, `own`/`borrow` parameters, `scope` (ADR 0016; AL0207, AL0208, AL0213) |
| `Lowerer.StackArrays` | small fixed-size non-escaping arrays -> `stackalloc` |
| `Lowerer.FlatRows` | arrays of fixed-width literal rows flattened to one `T[]` |
| `Lowerer.Presize` | `List`/`SoA` presized for the counted loop that fills it |
| `Lowerer.Sharing` | constant immutable objects shared instead of allocated |
| `Lowerer.Ranges` | integer value ranges: unchecked arithmetic where overflow is impossible (ADR 0013, 0014) |
| `Lowerer.Gpu` | `gpu for` -> ILGPU kernels (ADR 0021) |
| `Lowerer.Callbacks` | lambdas passed to `lib` `Callback(...)` -> `[UnmanagedCallersOnly]` function pointers (ADR 0018; AL0215) |

`Optimizer/` (ADR 0014): `Pipeline` runs `Passes/` over MIR with `MirRewriter`/`MirFacts`. `Backend/CEmitter`:
`@[Native]` functions -> C for clang. `Backend/Emitter` also lowers `rule` declarations for `alice prove` (AL0214).

`Semantics/Binder/Binder.Races`: AL0202 race check (ADR 0008). `Semantics/Binder/Binder.Mixins`: `include`/`extend` (module `def x` = template, re-bound per class). `AliceCompiler.LoadWithRequires`: transitive `require`. `Syntax/Formatter`: `alice fmt`.

Invariants:
- Single IL backend (ADR 0001, 0007).
- Literal-block iterator kernels allocate 0 bytes (`AllocationTests`).
- Null-safety diagnostics are errors; `val` reassignment and data races in concurrent contexts are errors (ADR 0004, 0008).
- Parallelization only for code proven pure; serial otherwise.
- Optimized paths give the same results as serial/scalar code (integer exact; parallel float sums reassociate; SIMT bit-identical).
