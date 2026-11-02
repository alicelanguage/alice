# Alice.Compiler
Path: `src/Alice.Compiler`. Pure C#. Depends on: Roslyn, `Alice.Runtime` (reference for emitted code).

| Folder | Stage | Output |
|---|---|---|
| `Syntax/` | `Lexer`, `Parser/`, `Ast/` | AST |
| `Semantics/` | `Binder/`, `Symbols/`, `Types/` | HIR: types, symbols, inferred returns, hoisted locals |
| `Lowering/` | `Lowerer*` | MIR (`Mir/`): inlined/fused loops, typed temps |
| `Backend/` | `Emitter`, `Printer`, `AliceCompiler` | C# with `#line` -> Roslyn Release IL |
| `Air/` (planned, ADR 0032) | `AirType`, `AirDecls`, `AirDump`, `AirVerifier`, `Passes/` | typed structured AIR; `Mir/` is migrated into it in place (byte-identical C# as acceptance) |
| `Backend/Il/` (ADR 0033; `AirToIl` planned) | `IlModule` (facade), `IlBuilder`, `Signatures`, `MetadataWriter` (+ `RefPack`), `PdbWriter` | IL + portable PDB via System.Reflection.Metadata |
| `Backend/CSharp/` (planned, ADR 0033) | `AirToCSharp` | C# (oracle, generator programs) |

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
| `Lowerer.Affine` (planned, ADR 0037) | arena sum types: term packing, `case` Take/Borrow, slab alloc, `scope` regions, lane-local frees |
| `Lowerer.Dataflow` (planned, ADR 0038) | `dataflow do` / `stream def` / proven `Channel`+`spawn` networks -> fused batch stages over SPSC rings |
| `Lowerer.Cm` (planned, ADR 0039) | scan, combine, compaction, permute shapes and `cm for` (AL0227-AL0229) -> `Cm/` kernels |

`Backend/Il/` (D1.2): `new IlModule(name)` -> `DefineType` -> `DefineMethod` -> `IlBuilder` -> `Write(pe, pdb)`. `IlBuilder` is a
stack-oriented instruction list with symbolic operands (`IlType`, `IlMethod`, `IlField`, `IlLabel`, `IlLocal`) and typed
helpers for the AIR emitter (`EmitLoadLocal`, `EmitCall`, `EmitBranch(label, IlCond)`, `EmitBinary(op, checked, unsigned)`,
`EmitConvert`); at write time it runs opt-in peepholes (`IlPeephole`), computes max-stack, relaxes branches to short forms
and encodes with `InstructionEncoder`/`ControlFlowBuilder` (try/catch/filter/finally/fault). Rows follow definition order,
sorted tables are emitted sorted, framework TypeRefs resolve against the ref pack (`RefPack`), and the MVID, PE stamp and
PDB id are content hashes (byte-identical rebuilds). Tests: `tests/Alice.Tests/Backends/IlBuilderTests` (ILVerify,
collectible ALC runs, determinism, PDB round-trip).

`Air/Passes/Synthesize` (D1.6, IL backend only; the C# path never runs it): `Synthesize.Run(binder)` returns an
`AirModuleDecl` (`Mir/AirDecls.cs`) and sets `TypeSym.Synth`. It declares, with MIR bodies, what Roslyn synthesizes for
the C# backend: record/record struct members in Roslyn's shapes (backing fields `<x>k__BackingField`, init setters,
primary and copy constructors, `EqualityContract`, `Equals(T)`/`Equals(object)`, Roslyn's `GetHashCode` formula,
`ToString`+`PrintMembers` printing `Point { x = 1, y = 2 }`, `Deconstruct`, `op_Equality`/`op_Inequality`, `<Clone>$`,
`__With(all fields)` for `with`); sum types (abstract base with private ctor, abstract `Tag`, `Is<Variant>`, sealed nested
variants, unit `Value` + `.cctor`); derived `op_Inequality`/`op_GreaterThan`/`op_GreaterThanOrEqual` (Emitter rules); the
`IEnumerable.GetEnumerator` bridge; auto-properties; default and struct constructors; `.cctor` for static/constant
initializers; `def to_s` as the `ToString` override; `<Module>..cctor` running class bodies. What `AirToIl` must honour:
- Leaves: `AirThis` is ldarg.0; `AirBase` (typed with the base type) is `this` with a non-virtual `call` (`.ctor` on
  base `object` is `object::.ctor`); `AirTypeOf` is ldtoken + `Type.GetTypeFromHandle`; `AirParamRef` of a
  `ByRef(Out)` type as an assignment target is a store through the out parameter.
- `MMember` on an Alice type names a backing field or a property: property reads call `get_`, property assignments call
  `set_` (init setters included). Calls are by name; overloads (`Equals(T)`/`(object)`/`(Base)`, `StringBuilder.Append`
  string/char/object) resolve from the arguments' static `Type`, which every synthesized node carries.
- `As` is `MCond(MIs, MCast, AirNull)`; `MBinary { Checked = false }` is unchecked (the hash); `System.Type` equality is an
  explicit `op_Equality` call.
- `AirTypeDecl.FieldInits` must be prepended to every user constructor that does not chain `this(...)` (synthesized ones
  already contain them); `AirMethodDecl.User != null` takes its body from lowering; `Implements` is a MethodImpl row;
  `AirMethodFlags.InitOnly` is modreq(IsExternalInit); `Virtual` alone is `newslot`; names are CLR names (no `@`).
- Not synthesized here: user operator wrappers (`op_Equality(Self, T)` -> `self.op_Eq`), events, plain/`@x` fields
  (declarations, D0.8). Tests: `tests/Alice.Tests/Compiler/SynthesizeTests` (dumps, and an MIR evaluator compared with
  the C# backend's output).

`Optimizer/` (ADR 0014): `Pipeline` runs `Passes/` over MIR with `MirRewriter`/`MirFacts`. `Backend/CEmitter`:
`@[Native]` functions -> C for clang. `Backend/Emitter` also lowers `rule` declarations for `alice prove` (AL0214).

`Semantics/Binder/Binder.Races`: AL0202 race check (ADR 0008). Planned: `Binder.Affine` (arena eligibility, move analysis, whole-program demotion; AL0220-AL0223, ADR 0037) and `Binder.Dataflow` (graph, SPSC/acyclic and capture checks; AL0224-AL0226, ADR 0038). `Semantics/Binder/Binder.Mixins`: `include`/`extend` (module `def x` = template, re-bound per class). `AliceCompiler.LoadWithRequires`: transitive `require`. `Syntax/Formatter`: `alice fmt`.

Invariants:
- Single IL backend (ADR 0001, 0007); planned: single IR (AIR), IL-first, C# kept as supported target and oracle (ADR 0032, 0033).
- Literal-block iterator kernels allocate 0 bytes (`AllocationTests`).
- Null-safety diagnostics are errors; `val` reassignment and data races in concurrent contexts are errors (ADR 0004, 0008).
- Parallelization only for code proven pure; serial otherwise.
- Optimized paths give the same results as serial/scalar code (integer exact; parallel float sums reassociate; SIMT bit-identical).
