# ADR 0032: AIR, a typed structured IR replacing MIR in place

Status: Accepted (1.0 backend direction; not yet implemented). Evolves ADR 0007 (MIR stage) and ADR 0014 (passes run on AIR).

MIR is a C# AST with strings where types and symbols should be: `MDecl.TypeCs`, `MNew.TypeCs`, `MCast.TypeCs` and
`MIs.TypeCs` hold C# text, `MBinary.Op`/`MUnary.Op` are strings, `MDecl.TypeCs == null` means `var` (Roslyn infers the
local), and the opaque `MCode` leaf has 387 construction sites across Lowering, Optimizer and Backend. Only a line number
is carried. Nothing but a C# printer can consume it, so a direct IL or wasm backend (ADR 0033, 0034) needs a real IR.

Decision:

- **AIR (Alice IR) replaces MIR in place.** It is a typed, structured tree IR per method body plus a typed declaration
  model (`AirModule`, `AirType`, `AirMethod`, `AirField`) instead of string-built declarations. Not SSA and not a CFG:
  IL is a stack machine with free locals (RyuJIT allocates registers), wasm requires structured control flow, and a CFG
  would need a stackifier/relooper for no gain. The optimizations that matter already run on HIR. Exception regions stay
  regions.
- What every node carries:
  - Types: `AirType` sum (Prim, ClrRef, AliceType, TypeParam, Array, ByRef, Pointer, FnPtr, Tuple), a nullability
    bit, and the `IsByRefLike` fact.
  - Symbols: `AirMethodRef`, `AirFieldRef`, `AirLocal`. No name strings at use sites.
  - Operators: enums with a `Checked` flag.
  - Structured control: If, While, counted For, Foreach patterns, Switch, Try/Catch/Finally, Using, Lock, Block. No
    goto.
  - `AirLambda` until closure conversion. `AirAwait`/`AirYield` until the state-machine pass. `AirInterp`.
  - Allocation facts (Owned, Rent/Release, StackAlloc, Fresh). Effect flags (Pure, Safe, Forked, Sequential).
    `AirPar`.
  - `Span {File, Line, Col, Len}` on every node, for diagnostics, PDB sequence points and source maps.
- Declarations are built as AIR. A `Synthesize` pass expands records and sum types. Later backend passes replace the
  lowering Roslyn does today: ClosureConvert, StateMachines, PatternTemps, ForeachLower, InterpLower, UsingLockLower,
  SwitchLower, CheckedScopes, DefaultParams, TupleLower, NullableLift, DelegateCreate.
- **Migration in place; the C# printer never stops working.** Steps:
  1. Add `AirType` and `Span` to every node: `TypeCs` becomes `AirType`, `Op` becomes an enum.
  2. Replace the `MCode` sites with typed nodes, file by file.
  3. Move declaration text out of `Backend/Emitter` into `AirDecls`.
  4. Port the ADR 0014 passes to `AirRewriter`/`AirFacts` with real types, then rename `Mir/` to `Air/`.

  **Acceptance for each step is byte-identical C# output on the corpus** (samples, benchmarks, examples, tests,
  `tests/parity`) against the pre-migration tree. HIR lowerings keep their semantics; only their output constructors
  change. `Lowerer.Gpu` keeps emitting ILGPU kernels.
- **`AirVerifier`** runs after lowering and after each pass in Debug and test builds. It checks that types match at
  every use, that a byref-like value is never stored to the heap, that `Unknown` types appear only where the target
  allows dynamic (it is rejected under the IL backend from day one), that locals are defined before use, and that
  nesting is well formed.
- **`alice emit --air <file.al>`** prints the AIR dump (`AirDump`). New compiler tests assert AIR dumps and behaviour
  rather than C# text.

Rejected: an SSA CFG IR with register allocation (it needs a stackifier for IL and a relooper for wasm, EH in SSA is
the hardest part, it costs 2-3x the effort, and the gains are speculative; RyuJIT and Binaryen already run GVN/LICM).
Also rejected: keeping C# text as the only target, which would tie AIR to printable C# and conflict with ADR 0033/0034.
`docs/ROADMAP-1.0.md` says "SSA-like" for workstream D; this ADR narrows that to typed structured.

Consequences: the node set grows from 32 kinds to about 45. The binder must supply real types wherever `var`/`dynamic`
used to hide them. A CI metric counts `Unknown` locals on the corpus and must go to zero. The 209 `Transpile(` tests and
157 C#-text assertions stay green because the C# printer stays.
Follow-up from step 2 (D0.4): the value added in a `gpu for` reduction is still printed to C# before the MIR passes
run on the kernel body (an `AirRawCs` in `Lowerer.Statements`). Making it typed lets StrengthReduction and
ConstantFolding rewrite it (`floor_div(x, 4)` becomes `x >> 2`), which changes emitted kernels, so it waits for a
deliberate behaviour change. Until the passes are ported, the typed leaves derive from `MCode` so that the passes'
leaf-text matching keeps working.

Tests: byte-identical C# against the baseline on the corpus at each migration step; `AirVerifier` on every compiled
test program; AIR dump tests through `alice emit --air`; the AllocationTests and ADR 0014 pass tests unchanged.
