# ADR 0033: Direct IL backend through System.Reflection.Metadata

Status: Accepted (1.0 backend direction; not yet implemented). Amends ADR 0001 and ADR 0007: "single Roslyn backend"
becomes "single IR (AIR, ADR 0032), IL-first".

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
