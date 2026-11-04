# ADR 0033: Direct IL backend through System.Reflection.Metadata

Status: Accepted; implemented for loose files in 1.0.2, package builds in 1.1. Amends ADR 0001 and ADR 0007:
"single Roslyn backend" becomes "single IR (AIR, ADR 0032), IL-first". The C# printer remains the differential oracle.

`alice run`, `alice build <file>` and `alice emit --il` compile through the IL backend by default (1.0.2; in 1.0.0/1.0.1 only `emit --il` did, and plain `alice emit` still prints the C# printer's output). Workspace/package builds (alice.toml, Alice.Sdk) use the C# printer until 1.1. `--backend csharp` and `ALICE_BACKEND=csharp` select the printer for loose files; `[build] backend` in alice.toml is accepted and has no effect until package builds get the IL backend in 1.1. Programs the IL backend cannot compile yet fall back to C# with a one-line `note:` on stderr.

Today Alice prints C# and Roslyn emits IL. Roslyn is also the real type checker: nullable flow analysis (CS8600-CS8625
as errors is Alice null safety), `var` inference, overload resolution and generic inference finish what the binder
leaves open, and `alice check` reports Roslyn's diagnostics. Compile time is bounded by Roslyn, and the language is
bounded by what C# can express.

Decision:

- **AIR is emitted to IL directly with System.Reflection.Metadata** (`MetadataBuilder`, `InstructionEncoder`,
  `ControlFlowBuilder`, `PortablePdbBuilder`, `ManagedPEBuilder`). It is inbox, Roslyn uses it, it is deterministic and
  NativeAOT-compatible. Alice writes its own `IlBuilder` (`Backend/Il/`). Generic instances are TypeSpec/MethodSpec;
  AssemblyRefs come from the ref pack. Rejected: Mono.Cecil (an extra dependency) and Roslyn syntax trees (still
  Roslyn). `PersistedAssemblyBuilder` is used only for the S1 spike. ILVerify is a test oracle.
- **The C# backend stays a supported target and the oracle.** `AirToCSharp` serves `[build] backend = "csharp"`,
  `alice emit`, `alice explain`, and the differential harness. A program is auto-routed to the C# backend, with a note
  saying so, when it triggers a source generator (JSImport/JSExport, JsonSerializable, GeneratedRegex, LibraryImport
  and other attribute-driven generators on partial members) or uses an `Expression<T>` lambda. Alice replaces the
  generators it owns over time (ADR 0018, Alice Core).
- Roslyn lowering is replaced by AIR passes (ADR 0032): closures, async/iterator/async-iterator state machines on the
  BCL builders, pattern temps, records and sum types, foreach, interpolation through
  `DefaultInterpolatedStringHandler`, using/lock, switch, tuples, checked arithmetic per node, attributes as blobs,
  `localloc`/pinned locals/`calli` for `unsafe`, `stackalloc` and function pointers.
- **`dynamic`** goes through an own `Alice.Runtime.Dyn` cached-reflection dispatcher. Using it is a compile error under
  `native = true` and on wasm.
- **Debugging**: portable PDB with `.al` sequence points from AIR spans, local scopes, deterministic MVID and Source Link.
  `--debug` builds become real Debug builds; today only Release IL exists.
- **The binder owns type checking before IL becomes the default**: definite assignment, nullable flow, overloads and
  conversions, LINQ generic inference, and `alice check` without Roslyn. The differential harness counts a Roslyn error
  on the C# path that the IL path accepts as a missing binder diagnostic.
- **IL emission quality targets.** The backend aims at IL as good as or better than hand-written C# through Roslyn
  (stream F of `docs/ROADMAP-1.0.md`: within 3% on the suite, ILVerify clean, PDB, NativeAOT, deterministic):
  - peephole pass: `dup`/`stloc`/`ldloc` elimination, redundant load/store and branch-to-branch chaining;
  - `tail.` calls where legal (no byref escapes, matching signatures), for self and mutual tail recursion;
  - `readonly`/`in` ref locals and by-ref passing for large structs instead of copies;
  - proven-in-bounds indexing (proofs, loop bounds) as `ldelema` + `Unsafe.Add` patterns without bounds checks;
  - `[SkipLocalsInit]` on emitted modules, `MethodImplOptions.AggressiveInlining` on small leaf and accessor methods;
  - `constrained.` calls for interface/virtual calls on generic type parameters (no boxing);
  - deterministic MVID and output (ordered metadata tables, content-hashed MVID).
- **Why an own backend.** The backend may emit IL that the C# compiler cannot express: `tail.` and `no.` prefixes,
  `ldelema` without bounds checks under proofs, custom modifiers (modreq/modopt), and other valid IL with no C#
  spelling. Going through C# would cap Alice at what C# can say; that is the reason this backend exists.
- **Closure ABI** (pass `Air/Passes/ClosureConvert.cs`, IL backend only; nodes in `Mir/MExpr.Closures.cs`). A
  capture-free lambda is a static method `<M>b__N` on the module's `<>c` holder; its delegate is created once, lazily,
  and cached in the static field `<>9__M_N` (`AirDelegateCreate` with `CachedField`). A capturing lambda that never
  escapes (a Par kernel, a `Par.Range/Reduce/Fork/For` argument, a fork branch, an immediately invoked lambda; any
  other position escapes) gets a by-ref-like display struct `<>c__DisplayClass_M_N` on the caller's stack: read-only
  captures are value fields, written ones `ref` fields to the caller's locals, and the body is a static method whose
  first parameter is `ref` the display. Any other capturing lambda gets a sealed display class with one field per
  capture, filled at creation, and the body as an instance method. A local or parameter that is written and captured by
  an escaping closure is hoisted into a shared display of its declaring block (created at block entry, so fresh per
  loop iteration); the method and the closures reach it through that display (a link field `<>8__N`). `this` is the
  field `<>4__this`. A Par kernel or fork branch runs on other threads, so its display is a plain struct with value
  fields only, copied once into the runtime's job: it implements `IParKernel` / `IParKernel<T>` / `IForkBranch<T>` with
  the closure method as `Invoke` and is passed to the struct overloads `Par.Range<TKernel>(n, ref k)`,
  `Par.Reduce<T, TKernel>(n, ref k, combine)` and `Par.Fork<A, B, TA, TB>(ref a, ref b)` (`Parallel/ParKernels.cs`,
  jobs reused per thread: no allocation in steady state); a kernel that writes a capture is demoted to a display class.
  `ref` fields appear only in directly invoked closures (a `ref struct`). `ALICE_CLOSURE_STRUCTS=off`
  (`ClosureContext.StructDisplays = false`) demotes every display struct to a class. The pass runs after the IL
  lowerings (`Pipeline.Il`: IlLowering, then ClosureConvert). A body the pass cannot convert soundly (for example a written `for` variable captured by an
  escaping closure) keeps its lambdas and is reported as unsupported.
- Staged path (a gate must pass before the next stage starts):

| Stage | Deliverable | Gate |
|---|---|---|
| S0 | AIR typing, `MCode` removed, declaration model, `alice emit --air` (ADR 0032) | byte-identical C#; all tests green |
| S1 | `PersistedAssemblyBuilder` spike: static primitive/array methods, loops, calls | nbody, queens, editdist bit-identical, within 3% of the C# path |
| S2 | SRM `IlBuilder`, PDB, EH, types, records, sum types, closures; `--backend il` | ILVerify clean; 60% of corpus identical; AllocationTests under IL |
| S3 | state machines, `Dyn`, FFI, attributes, generics, tuples, patterns | 100% of the non-generator corpus |
| S4 | binder completion, `AirVerifier` v2, `alice check` without Roslyn, Debug builds | "Roslyn rejects, IL accepts" = 0 |
| S5 | IL is the default; C# auto-selected for generator/expression-tree programs | CI runs the tests under both backends for one release |

Consequences: Roslyn stays a dependency of the C# backend, and `alice check` no longer needs it. Two printers can drift.
Up to 1.0 the C# printer is the oracle, and after that a round-trip test guards it. The 0-byte iterator invariant
(AllocationTests) and the benchmark numbers must hold under both backends from S2 on: any allocation delta or a
slowdown above 3% fails. `modules/compiler.md`'s "single IL backend" invariant now reads "single IR, IL-first".

Tests: `tests/Alice.Tests/Backends/DifferentialTests` (C# vs IL output and behaviour over the corpus), IlVerify tests,
AllocationTests parametrized by backend, `TrimAotTests` and `WasmCliTests` on IL.
