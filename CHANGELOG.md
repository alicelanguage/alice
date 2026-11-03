# Changelog

## Unreleased

## 1.0.0-rc.1 - 2026-11-02

- IL backend wiring (backend plan D1.6/D1.8/D1.9/D1.10, ADR 0033): `alice run --backend il` / `alice emit --il` now compile async/iterator/async-iterator methods (own state machines, `Air/Passes/StateMachines`), `Any` operations (`Alice.Runtime.Dyn` call sites, AL0232), records, struct records and sum types (`Synthesize`), and `lib` P/Invokes; the three stub tests `Il_async/iterator/async_iterator_matches_csharp` are real C#-vs-IL differential tests. Fixes found on the way: `break` inside `for v in <IEnumerable>` left the try with `br`, `String == String` was a reference compare under IL.
- dataflow (ADR 0038, P3.2/P3.3): `dataflow do` with `source`/`stage(state:, par:)`/`sink`, `stream def`; binder checks AL0244-AL0246; lowered to AirDataflow/AirStage/AirRing and static stage methods over the runtime function-pointer overloads (one `Graph.Run`); `benchmarks/dataflow/alice/dataflow.al` matches the hand C# graph.
- Alice Core library modules (ADR 0036; stdlib plan C3.1-C3.5, C4.1, C4.2), one namespace per module under
  `src/Alice.Runtime/Core/`: `Text` (`Utf8String`, SIMD `count/delete/squeeze/tr` over UTF-8, `Format.sprintf/try_format`
  without boxing), `Time` (Ruby `Time`, `Duration` with `5.minutes`, 0-allocation `strftime` into spans), `Csv` (RFC 4180
  reader on a 64-byte SIMD structural index, zero-copy fields, writer), `Toml` (TOML 1.0 parser and writer, passes
  toml-test; `alice.toml` is now read with it through `Projects/ManifestToml.cs`), `Fs`, `Process` (`Shell`, `Open3`,
  `Signals.trap`), `Crypto` (CRC-32C, XXH3-64, digests, HMAC, `SecureRandom`), `Collections` (`Set`, `Deque`, `Ring`,
  `BitSet`), `Env` (`Env`, `Argv`, `Platform`), `Net` (TCP/Unix/UDP sockets, `TcpServer.serve` per connection, owned
  `RingBuffer`) and `Net.Http` (`HttpSession` over `SocketsHttpHandler`, JSON body hooks for Alice.Core.Json).
  Prelude: duration literals (`Prelude.CoreTime.cs`). Benchmarks: `benchmarks/core/lib`, `results/core-lib.md`
  (CSV 1.6 GB/s, CRC-32C 10-11 GB/s, Deque 1.5x Queue and 8.7x LinkedList, echo 0 bytes/message with 256
  connections). Docs: `docs/stdlib.md`, `docs/language.md`,
  "Alice Core modules".
- Affine arena lowering P1.3 (ADR 0037 step 2, ADR 0040 section 2), driven by `Binder.Affine`: arena sum types are 64-bit terms over per-type lane slabs (packed variants in the term, slot variants as `ulong` words of the binder's slot union); AIR nodes `AirTermPack`, `AirTermTag`, `AirTermPayload`, `AirArenaAlloc`, `AirArenaTake`, `AirArenaBorrow`, `AirArenaDrop` (recursive by default: Bend's free of an unconsumed subtree; `ALICE_ARENA_DEEP=0` for slot-only), `AirSlotWord`, `AirArenaRegion` (`scope` in the main body), `AirLane` (`Mir/MExpr.Arena.cs`). The C# printer calls the public `Arena` API (`Backend/Printer.Arena.cs`); the IL backend inlines slot access through the lane cursor (`Backend/Il/AirToIl.Arena.cs`); `Arena`/`ArenaSlab` gain an `[EditorBrowsable(Never)]` raw-access surface and `Arena.DropDeep`. `ALICE_NO_ARENA=1` keeps records. Results in `benchmarks/bend2/results/arena.md`; tests in `tests/Alice.Tests/Compiler/AffineLowerTests.cs`.
- samples (ROADMAP-1.0 stream H): a verified, documented starting point per domain, each in `examples/verify.sh`
  and `Integration/SampleTests`: `samples/netchat` (TCP chat, `spawn` + `Channel`), `examples/web/aspnet` grown
  into a minimal API (products, JSON bodies, DI, EF Core in memory, WebSocket echo, 14 `WebApplicationFactory`
  checks; `/health` and `/quote` unchanged), `examples/ui/avalonia` as an MVVM app (two views, JSON settings,
  headless run), `samples/raylib-pong` (rules tested and proven), `samples/alice-grep` (globs, `Par.For` scan,
  NativeAOT: 2.7 MB, 3.3 ms start), `examples/data/pipeline` (CSV to JSON), `samples/lowlevel` (libc, `@[Native]`,
  pointers, index tree, safety story). Compiler gaps found on the way: /work/reports/domain-compiler-issues.md;
  new "Not ready" rows in docs/production.md.

- core: Alice.Core.Json engine (ADR 0036, C2.1-C2.4) replaces the JSON stubs: stage 1 structural index (AVX-512BW / AVX2
  vpshufb classes, carry-less-multiply prefix xor, 128-bit and scalar tiers, ALICE_JSON_TIER to force one), simdjson-style
  tape (JsonDocument, JsonAny, JsonProperty), on-demand JsonIterator, JsonWriter (Ryu shortest doubles), Eisel-Lemire
  numbers (Int64 -> UInt64 -> Float64), JsonMap mapping pieces. Prelude: JSON.parse / generate / pretty_generate,
  JSON::Any sugar (as_i, dig, nil?, to_h ...); to_json and parse_json go through it (to_json keeps 1.0 and non-ASCII
  as Ruby does). JSONTestSuite 283/283 y_/n_ correct; forge, twitter: stage 1 5.2 GB/s, DOM 2.56 GB/s (3.4x
  JsonDocument), writer 2.4-1.5x Utf8JsonWriter (benchmarks/core/json/results.md).
- perf: re-measured AggressiveOptimization on a serial core pair (`taskset -c 11,27`, interleaved cold runs; the
  earlier gains were partly measured on one cpu, where tier 1 never arrives). A primitive loop that only calls
  helpers no longer gets AO (kmeans `chunk`, mandelbrot, nbody: within 3% either way); loops over owned arrays
  keep it (bfs 3-8% faster). New `ALICE_NO_AO=1|kernel,leaves,nest,loop` switch for A/B runs. bend2 serial
  table re-measured (benchmarks/bend2/results/summary.md).
- Affine arena analysis (ADR 0037, P1.2): `Semantics/Binder/Binder.Affine.cs` + `Binder.Affine.Flow.cs` decide per sum type arena vs records (payload rule, packing of 0-field and <=32-bit variants, slot layout), per `case` arm Take/Borrow/Keep, per use Move/Keep/BorrowArg and Drops, over a CFG with liveness and move states; Bend-style borrow inference (a parameter the body only reads lends, whole-program fixpoint; written `borrow` stays a contract); any escape (field, collection, nullable, block, .NET call, `==`/`hash`/`to_s`, `rescue`) or use after move demotes the type to records whole-program. `affine enum` (new modifier) makes those sites AL0240-AL0243 (now live in docs/errors.md; docs/language.md `affine enum`). Facts on `binder.Affine` and `TypeSym.Arena`; `alice explain` prints one row per sum type (`arena` / `record (demoted: ...)`); the ADR 0016 borrow check (AL0213) trusts the binder for arena parameters, so `borrow t : Tree` with a `case` on t compiles. No lowering change: emitted C# is unchanged. Tests `tests/Alice.Tests/Compiler/AffineTests.cs` (all 7 sum types of the bend2 ports tree-radix, tree-bitonic, tree-matmul, merkle, symreg are arena).
- Book compiler issues (/work/reports/book-compiler-issues.md), tests in `tests/Alice.Tests/Compiler/BookIssuesTests.cs`:
  - Syntax: `puts await f(x)` (`await` starts a command argument); `^name` pins inside array patterns, also of a name
    bound earlier in the pattern (`in [a, ^a]`); `rescue A, B => e` no longer emits `--1` in the second catch (a
    negative literal merged into its token is not merged again when the clause body is re-parsed per type).
  - Semantics: `Counter.total += 1` on a `class_property`; `raise ... unless n` narrows `n` like `return unless n`;
    a generic .NET method with an Alice type argument returns that type, nil-able when declared `T?`
    (`JsonSerializer.Deserialize<Array(Tea)>(s)`: `if loaded`, `loaded || d`); `xs << v` on `List(T)` of a type
    parameter; `format`/`puts` and the other Kernel functions inside a reopened .NET type call the Prelude function;
    `sort_by_key` returns a named tuple, and `s.keys`/`s.values` read its elements (element names win over the
    named-tuple helpers).
  - Slices and negative indexes: `xs[a..b]`, `xs[a...b]`, `xs[a..]`, `xs[..b]`, `s[start, n]` on Array, List and
    String (Prelude `slice_range`/`slice_len`, negative bounds from the end, a start past the end gives an empty
    slice); a literal negative index `xs[-1]` reads `xs[xs.Length - 1]` (computed negative indexes still raise).
  - Purity: `Int32::MAX`/`MIN` and other literal .NET constants are pure (in `pure def` and rules); a Prelude
    extension call is pure when every overload for its receiver is `[Pure]`; `upcase`, `downcase`, `capitalize`,
    `strip`, `reverse`, `includes?`, `starts_with?`, `ends_with?`, `ljust`, `rjust`, `center`, `chomp`, `chop`,
    `squeeze`, `swapcase` on String are marked `[Pure]`.
  - Diagnostics: `@[Strict]` is compiler-only (no `StrictAttribute` in the C#); a lowering error that names its code
    keeps it (AL0215 was reported as AL0001).
  - Tooling/docs: `alice explain` counts the `__par_` helper of a parallel pipeline for its method (and top-level
    helpers for `Main`); `alice guide` prints the installed version in its header; guide/AGENTS.md: the rescue
    modifier exists, `Hash(K, V).new(0)` for counters; docs/errors.md: one row per code (duplicate AL0230 removed,
    live AL0220/AL0221 before the planned AL0240-AL0249); docs/language.md: slices, negative indexes, pins in
    patterns, `class_property` writes, nil-able generic .NET results; README: the range pipeline is "one fused
    loop" (it is serial; the parallel reduce applies to array sources of 65,536+ elements).
- IL backend D1.4 (ADR 0033): `Backend/Il/AirToIl` emits IL + portable PDB from the post-optimizer MIR through `IlBuilder` (declarations as the C# backend makes them, typed expressions, checked arithmetic per node, EH, foreach, interpolation, vectors, sequence points and local names); `alice run --backend il`, `alice emit --il <file>` (writes `<name>.dll`/`.pdb`, prints the IL listing). Unsupported nodes are reported with their kind; differential, ILVerify, decode, allocation and corpus tests in `AirToIlTests`.
- IL backend dynamic sites (backend plan D1.9, ADR 0033): `Alice.Runtime.Dyn` (`src/Alice.Runtime/Dynamic/`) dispatches
  `Any` values without the C# runtime binder: one `DynSite` per call site with a monomorphic entry and a 4-entry
  polymorphic inline cache keyed by receiver and argument runtime types, `MethodInvoker` thunks (allocation-free hit
  path), overloads by runtime types with numeric widening, nil, Alice operator semantics (checked, floored `/` `%`), `define_method` /
  `method_missing`, public members only. The IL-only pass `Air/Passes/DynSites.cs` rewrites dynamic operations into
  `AirDynCall` (`Mir/MExpr.Dyn.cs`) and lists the site fields; AL0232 reports dynamic sites under `native = true` and
  on wasm; `DynSites.Declaration()` declares the `<>DynSites` holder type (AirDecls). The C# backend is unchanged
  (its C# `dynamic` still truncates `/` and `%`: docs/production.md).
- IL closure conversion (backend plan D1.5, ADR 0033 closure ABI): `Air/Passes/ClosureConvert.cs` turns lambdas into static methods with a cached delegate (capture-free), display structs passed by ref (non-escaping: Par kernels, fork branches) or display classes with hoisted written captures (escaping); nodes `AirDelegateCreate`, `AirDisplayNew`, `AirDisplayField` (`Mir/MExpr.Closures.cs`, dump and debug C# forms). Kernels and fork branches use new struct overloads `Par.Range<TKernel>`, `Par.Reduce<T, TKernel>`, `Par.Fork<A, B, TA, TB>` (`IParKernel`, `IParKernel<T>`, `IForkBranch<T>`; reused jobs, 0 B in steady state); `ALICE_CLOSURE_STRUCTS=off` demotes structs to classes. Runs only under `Pipeline.Backend == AirBackend.Il` with a method context, after the IL lowerings; emitted C# is byte-identical.
- IL backend Synthesize pass (backend plan D1.6, ADR 0032/0033): `Air/Passes/Synthesize.cs` builds, for the IL backend only, explicit declarations with MIR bodies (`Mir/AirDecls.cs`: `AirModuleDecl`/`AirTypeDecl`/`AirMethodDecl`/`AirFieldDecl`/`AirPropertyDecl`, on `TypeSym.Synth`) for what Roslyn synthesizes today: record and record struct members (backing fields, init accessors, primary/copy constructors, EqualityContract, Equals, GetHashCode, ToString/PrintMembers, Deconstruct, ==/!=, `<Clone>$`, `__With`), sum types (abstract base, sealed nested variants, `Tag`, `Is<Variant>`, unit `Value`), derived `!=`/`>`/`>=`, the IEnumerable bridge, auto-properties, field/constant initializers, default and struct constructors, `to_s` as ToString and the module initializer. Emitted C# is byte-identical on the corpus.
- IL backend FFI (backend plan D1.10, ADR 0018/0033): `Backend/Il/AirToIl.Ffi.cs` emits `lib` funs as blittable P/Invoke
  rows (ImplMap: EntryPoint, ExactSpelling, WinApi, no CharSet/SetLastError; `[SuppressGCTransition]` for `@[Fast]`)
  behind inlined wrappers (UTF-8 strings on a `localloc` buffer via `Utf8Arg`, pinned arrays, out/ref byrefs, borrowed
  string returns, Bool as byte), `[UnmanagedCallersOnly]` callback trampolines passed by `ldftn`, `@[Native]` `calli`
  with the managed fallback, and pointer load/store/arithmetic/`sizeof`; `IlModule`/`MetadataWriter` gain P/Invoke
  (`IlMethodDef.PInvoke`, ModuleRef + ImplMap rows). Not wired into AirToIl yet; the FfiTests calls, driven from an IL
  `Main`, print what the C# backend prints, and NativeAOT publishes the emitted dll.
- IL backend state machines (backend plan D1.8, ADR 0032/0033): `Air/Passes/StateMachines*.cs` rewrites async methods, iterators and async iterators, for the IL backend only, into C#-shaped state-machine types (`AirTypeDecl`: `<>1__state`, method builder, hoisted locals, awaiter fields, MoveNext with `AwaitUnsafeOnCompleted`, try/finally across awaits, awaits in finally, `await foreach`/`await using`; iterator `Dispose` running pending finally blocks, `GetEnumerator` thread fast path; `AsyncIteratorMethodBuilder` + `ManualResetValueTaskSourceCore<bool>`; awaits and yields inside rescue clauses, yields inside begin/rescue, async lambdas via `StateMachines.AsyncLambdas`) plus a kickoff body, with the jump nodes `AirLabel`/`AirGoto`/`AirGotoTable` (`Mir/MExpr.StateMachines.cs`). The C# backend never runs it: emitted C# is unchanged.
- AIR D0.6: the vectorizer, SIMT loops, stack arrays and flat rows build typed AIR nodes instead of `MCode` text
  (`Mir/MExpr.Vector.cs`: `AirVector.Of(elem, width)` vector types, `AirVectorOp`/`Load`/`Store`/`Reduce`/`Fits`,
  `AirStackAlloc`; printed by `Backend/Printer.Vector.cs`, dumped by `Mir/MirDump.Vector.cs`). Emitted C# is
  byte-identical on the corpus.
- Fix (soundness, ADR 0013): a wrapping operator wrapped the checked arithmetic inside its operands too:
  `1 &* (b * c)` returned 0 for b = c = 65536 instead of raising, `1 &+ (-b)` did not trap on `Int32::MIN`. Operands
  with trapping arithmetic are now `checked(...)` (MirFacts.KeepChecked), so only the wrapping operator wraps. Found by
  the prove differential fuzz. Emitted C# changes only where a wrapping operator has such an operand.
- `alice prove` for CI and agents: `--format json` (machine-readable summary, rules in name order), `--timeout s` (per
  rule; a rule that runs out is `not proven` and fails the check), failures print one line
  `rule X FAILS: x = 7, lo = -3 -> false` (or `-> raised OverflowException`), exit 1 on any failing, unproven or stale
  rule. `alice build --prove` / `[build] prove = true | "smt"` runs `alice prove --check` after the build when the package
  has a RULES.al. `alice agents` tells agents to gate commits on it and explains the counterexample loop.
- Fix: `alice prove` crashed on a rule domain ending at `Int32::MAX` (the loop counter overflowed), and treated a domain
  near `Int64::MAX` as one case (its size went through double). Every variable now counts an exact offset from its Lo.
- `alice prove --smt` replays every Z3 counterexample through the compiled rule (a disagreement is `not proven`, never a
  verdict), translates `to_i!`, `to_u16!`, `to_u64!` and hex/binary/octal literals, and a differential fuzz test checks
  enumeration and Z3 agree on random straight-line rules. CI installs Z3 on Linux, macOS and Windows and sets
  `ALICE_REQUIRE_Z3=1`, so the SMT tests can no longer pass by skipping.
- Docs: ADR 0040 (paradigms in IL, accepted as direction): how AirToIl lowers fork/join, affine arenas, CM primitives and dataflow, with the AirVerifier rules they add; IL-lowering paragraphs in ADRs 0015, 0037, 0038, 0039.
- IL-only lowering passes (backend plan D1.7, ADR 0032/0033) in `Air/Passes/`, entry `IlLowering.Run`, called only by
  the IL backend (C# output unchanged): ForeachLower (arrays, spans, strings as counted loops; struct enumerators kept
  in a typed local, never boxed; other enumerables in try/finally with a null-checked Dispose), InterpLower
  (`string.Concat` for up to 4 string parts, else an invariant-culture `DefaultInterpolatedStringHandler`),
  PatternTemps (declaration and `var` patterns as explicit isinst into typed locals, with C# scopes), UsingLockLower
  (try/finally Dispose, `Monitor.Enter(o, ref taken)`, `Lock.EnterScope()`), SwitchLower (dense int/char jump tables,
  string cases as a length switch plus an ordinal `string.Equals` chain, no hashing), TupleLower (ValueTuple
  construction, ItemN/Rest access, deconstruction, element names recorded for TupleElementNames) and NullableLift
  (C# lifted-operator semantics with HasValue/GetValueOrDefault). Helper nodes `AirSeq`, `AirAs`, `AirSwitch` live in
  `Mir/MExpr.Lower.cs`; what a pass cannot lower (`await foreach`/`await using`, untyped operands, `bool?` logic) is
  left as is and listed in `IlLowerContext.Unsupported`.
- Docs: ADR 0035 (bootstrap) withdrawn by lead decision 2026-11-02, compiler stays C#; ROADMAP-1.0 stream F is now IL backend quality (within 3% of C#, ILVerify, PDB, NativeAOT, deterministic) and ADR 0033 lists IL emission quality targets.
- AIR helper declarations (backend plan D0.7c): the `__par_*`, `__parfor_*` and `__fork_*` helper methods and the
  `__shared_*` constant fields are typed declarations (`AirHelperDecl` over `AirMethodDecl`/`AirFieldDecl`, MIR bodies) in
  `Lowerer.TypedExtra`; their C# in `Lowerer.Extra` is printed from them (`Printer.Helper`), byte-identical on the corpus.
- AIR parallel/ownership nodes (backend plan D0.7a): Parallel, LoopPar, Fork, Ownership, Sharing and Presize lowering
  build typed nodes instead of C# text (`AirPar`, `AirParGuard`, `AirReferenceEqualsGuard`, `AirParProbe`, `AirForkJoin`,
  `AirForkable`, `AirRent`, `AirRelease`, `AirLength`, `AirTripCount`, `AirOffset`, `AirPresizeCount` in
  `Mir/MExpr.Parallel.cs`; leaves via `AirLeaf`), with C# printing and `--air` dump forms. Emitted C# is byte-identical
  on the corpus.
- IL backend infrastructure (backend plan D1.2, ADR 0033): `Backend/Il/` emits assemblies and portable PDBs directly with
  System.Reflection.Metadata: `IlModule` facade, `IlBuilder` (labels, named/pinned locals, scopes, EH regions, max-stack,
  short branches, prefixes, sequence points, peepholes), signature encoders, metadata writer with ref-pack AssemblyRefs
  and deterministic MVID, PDB with Source Link and embedded sources. Not wired to the compiler yet (`AirToIl` is D1.4).
- AIR foundation (backend plan D0.2/D0.3): MIR nodes gain an optional typed `AirType Type` and source `Span`
  (`MStmt.Line` reads the span's line when one is set). `AirType` is a closed sum (Prim, ClrRef, AliceType,
  TypeParam, Array, ByRef, Pointer, FnPtr, Tuple, Unknown, plus a nullable bit) whose `ToCs()` spells exactly what
  `AType.Cs` spells; `AType.ToAir()` is the one conversion. `MBinary.Op`/`MUnary.Op` are enums (`AirBinOp`,
  `AirUnOp`, `await` is `AirUnOp.Await`) with a `Checked` flag, cleared inside compiler-made `unchecked(...)`.
  Emitted C# is byte-identical on the corpus (`tools/emit-corpus.sh`, reused by later AIR work). Hidden
  `alice emit --air <file>` prints the MIR text dump (`MirDump`) for tests.
- AIR typed leaves (backend plan D0.4, batch 1): `Lowerer.Expressions/Statements/Calls` no longer build opaque
  `MCode` text; they build `AirConst`, `AirNull`, `AirThis`, `AirBase`, `AirLocalRef`, `AirParamRef`, `AirFieldRef`,
  `AirArrayLength`, `AirTypeRef`, `AirMethodGroup`, `AirDefault`, `AirTypeOf`, `AirSizeOf`, `AirDiscard`,
  `AirYieldBreak` and `AirMemberPath` (`Mir/MExpr.Core.cs`) with the binder's type and source span where the HIR has them.
  They derive from `MCode` until the passes are ported (D0.9), and `alice emit --air` dumps them by kind; batch 2's
  stand-in leaves are merged into them. Emitted C# is byte-identical.
- AIR batch 4b (backend plan D0.7b): `Lowerer.SoA/SoAFusion/Specialize/Gpu/Callbacks` build typed nodes: the SoA
  vector prologues use the vector nodes, `SoaCtx.StoreLeaf` carries the store as a typed leaf (so the SoA store
  is no text), `AirStoreCount` (Mir/MExpr.Soa.cs) is a store row count. The parallel lowerings share `AirLeaf` and
  `AirLength`/`AirTripCount`/`AirOffset` from Mir/MExpr.Core.cs. No text escape hatch is left in the IR: the
  `AirRawCs` and `AirName` nodes are gone. The ILGPU host launch is typed statements (`MDecl.Using`,
  `AirAssignExpr`), the event adapter is an `MLambda` (`BareParam`, `InlineBody`), the `gpu for` reduction value
  is `AirFrozen` (not rewritten by the passes), and constant paths, inherited members and unresolved names lower
  as `AirFieldRef`/`AirTypeRef`/`AirParamRef`/`AirLocalRef` (`AirMemberPath` for a member chain). Emitted C# is
  byte-identical.
- Every helper the lowering adds is a typed declaration (`AirHelperDecl`; Lowerer.Extra holds no hand-written C#):
  SoA fusion, specializations, native callbacks, the parallel word count and the `gpu for` kernel and its fields.
  The word-count helper and the GPU kernel print with standard formatting (braces, parentheses, one field per
  declaration); no other C# changes. Fixed: a `gpu for` whose range end is a conditional (`a..(c ? b : a)`)
  launched the wrong number of threads; the host trip count now parenthesizes the end. Lowering builds no `MCode`
  text leaf anywhere (a test checks it).
- `MFor.Step` is typed: `AirForStep` (an operator token plus an AIR value: `++`, `--`, `+= k`, `>>= k`,
  `= unchecked(i + 1)`), and the `gpu for` grid-stride step is an `AirGridStep` node. InductionVariables, LoopTiling,
  LoopUnroll, Dependence, StateMachines, AirToIl and the IL spike read the fields instead of parsing text. Emitted
  C# is byte-identical.
- AIR MCode removal batch 2 (backend plan D0.5): the allocation-free iterators, stream fusion and idiom rewrites
  (`Lowerer.Iterators/Fusion/Idioms`) build typed nodes instead of C# text: locals, literals, `default`, static
  receivers, and `AirRangeLength`, `AirRefOrAddDefault`, `AirRefOrNullRef`, `AirAlternateLookup`, `AirSpanSplit`,
  `AirStackBuffer`, with air-b4's `AirParGuard` for the parallel gate (Mir/MExpr.Iterators.cs, printed by Printer.Iterators.cs, dumped by
  MirDump.Iterators.cs). Emitted C# is byte-identical on the corpus; loop starts the optimizer analyses stay its
  literal form until the optimizer reads AIR leaves.
- Ruby core behaviour that needed the compiler: `Hash(K, V).new(default)` and `Hash(K, V).new { |h, k| ... }` build an
  `Alice.Runtime.DefaultHash` (missing keys read as the default, `h[k] += 1` counts from it; a block ending in `h[k] = v`
  returns v; a field or parameter declared `Hash(K, V)` that is only ever given such hashes is typed DefaultHash too),
  `Hash(K, V).new(capacity: n)` is the capacity; `to_i?`/`to_f?` are `Int32?`/`Float64?`, `match` is
  `MatchData?`, `Hash#delete` is `V?`, `dup`/`clone` keep the receiver's type, multi-key `dig` is the leaf `V?`,
  `max(n)`/`min(n)` are arrays, `each_char`/`reverse_each`/`step` without a block are sequences; `slice_when` blocks
  get two elements; `filter_map` with a nilable value-type result compiles; `t[0]` on tuples and `kv[0]`/`kv[1]` on
  pairs; `===` is Ruby case equality (range `include?`, regex match, type test, else `==`); `[1, 2] * 2` and
  `[1, 2] * ","`; `String.build { |io| ... }`; named tuples print with their names (`{name: "ada", age: 36}`) and
  have `keys`, `to_h`, `merge`, `to_s`, `inspect`.
- Compiler fixes from the bend2 ports: `x &+= y` / `&-=` / `&*=`; integer literals keep their suffix type when they
  first assign a hoisted local or fill a constant table (`w = 7_u32` in an `if`, `RK = [256_u32, 5394_u32]`); an enum
  constructor call as another's argument parses (`T::N(T::L(a), T::L(a))`, `M::Node(M::Free, r)`); a local first given
  a field-less variant is typed as its enum (`r = M::Free` then `r = M::Node(r, f)`); `UInt32 >> UInt32` (the count is
  converted to Int32); `val CUT = 3` is a constant; `"7".to_u32` reports `undefined method 'to_u32' for String`
  (AL0302) instead of a .NET constraint error; `nil = 3` is a reserved-word error.
- `require_relative "x"` loads the sibling file in single-file builds (`alice run/build/check`), like `require`.
- Integration tests read child-process output asynchronously and turn off MSBuild node reuse and the compiler server
  for the child, so the suite no longer hangs on inherited pipes (SdkPackageTests, SampleTests).


First release candidate for 1.0. Versions are `MAJOR.MINOR.PATCH` from now on (no more 0.x); release candidates
are published as GitHub prereleases. Every guarantee the toolchain makes is listed in [INVARIANTS.md](INVARIANTS.md).

### Language

- Ruby/Crystal syntax lowered in the parser: string and char escapes (`\u{1F600}`, `\101`, `\xHH`), single-quoted strings, adjacent literals, `%q()`/`%Q[]`/`%w{}`/`%i<>`, plain and raw heredocs, operator symbols (`inject(:+)`), `_1`/`_2`/`it`, `def name=(v)` writers, unary `-@`/`+@` and `<<`/`[]=` operators, visibility sections, `getter?`/`property!`/`class_property` and friends, `loop do ... break v end`, `begin ... end while`, all `rescue`/`ensure` forms, open ranges, `__FILE__`/`__DIR__`/`__LINE__`/`__method__`; unsupported forms are AL0101 instead of C# errors.
- Slices and negative indexes on Array, List and String: `xs[a..b]`, `xs[a...b]`, `xs[a..]`, `xs[..b]`, `s[start, n]`, literal `xs[-1]`.
- `affine enum` modifier for sum types stored in an arena (ADR 0037); its rules are AL0240-AL0243 ([errors](docs/errors.md)).
- Strict typing mode (AL0220): `[build] strict = true`, `--strict` or `@[Strict]` makes every untyped expression a compile error at its root cause.
- The binder types more programs: sum-type pattern bindings, generic .NET extension methods, block parameters of .NET delegates, `super` in .NET overrides, member chains on constants, Prelude functions with implicit self.
- Ruby core behaviour: `Hash(K, V).new(default)` and `.new { |h, k| ... }`, `===` case equality, `[1, 2] * 2`, `String.build`, named tuples with `keys`/`to_h`/`merge`, precise nilable result types (`to_i?`, `match`, `Hash#delete`, `dig`).
- Compiler fixes from the Bend ports and the book: `&+=`/`&-=`/`&*=`, literal suffix types on hoisted locals and tables, nested enum constructors, `UInt32 >> UInt32`, `val CUT = 3` constants, `puts await f(x)`, pins in array patterns, `class_property` writes, `raise ... unless n` narrowing, generic .NET results typed `T?` ([BookIssuesTests](tests/Alice.Tests/Compiler/BookIssuesTests.cs)).
- `require_relative "x"` loads the sibling file in single-file builds.

### Safety

- AL0221: a declared non-nilable field or getter that no `initialize` assigns is a compile error (audit F17).
- AL0202 follows copies of shared references inside concurrent blocks, and writes through ivars make a method mutating for `pure def` (AL0210) and concurrent callers (audit F9, F10).
- AL0230 checks generic message types with their type arguments; type parameters, pointers and unknown types are not sendable (audit F11).
- Pooled buffers: more escape routes are checked (`spawn`, `using`, `await`, if/case values, dynamic calls, `xs << v`), and borrows through virtual calls must hold for every override (audit F2, F3, F7, F18 partial).
- Loop parallelization stays serial when a written array is passed on or read whole, unless shown distinct (AL0209 for `par for`, audit F8); `a[i] = f(a, i)` parallelizes when every override of `f` reads `a` only at `i`.
- Purity: `xs << v` is a write, user operators must be pure, only System.Math/MathF counts as pure math; literal .NET constants and `[Pure]` Prelude String methods are pure.
- Range analysis: locals passed by `ref`/`out` lose their range, so their arithmetic stays checked (audit F15).
- `@[Native]` C: bounds-checked indexing, trapping overflow and division, masked shifts, collision-free generated names (audit F16).
- Fix (ADR 0013): a wrapping operator no longer wraps the checked arithmetic inside its operands; found by the prove differential fuzz.
- Review r1 (F3/F6/F8/F20): `--backend il` runs the C# path's diagnostic gate first, so AL0310 (nil) and AL0300 (constant overflow) reject the program on both backends; inside `unchecked ... end` the operands of `&+ &- &*` wrap too; `alice prove --smt` follows the compiled numeric promotion (Int8/UInt8/Int16/UInt16 at Int32, Int32 next to UInt32 at Int64) and sample-checks every Z3 proof against the compiled rule; a proof or counterexample the compiled rule contradicts is reported as a failure with an internal error, never as proven; docs and RULES.al state that narrow ints promote to Int32.
- `alice prove`: domains ending at `Int32::MAX` or near `Int64::MAX` are enumerated exactly; `--smt` replays every Z3 counterexample and evaluates every statement of a rule (audit F14).
- Affine arena analysis (ADR 0037, P1.2) decides arena or records per sum type; any escape or use after move falls back to records ([AffineTests](tests/Alice.Tests/Compiler/AffineTests.cs)).

### Performance

- AggressiveOptimization re-measured on a serial core pair: loops that only call helpers no longer get it, loops over owned arrays keep it; `ALICE_NO_AO` switch for A/B runs ([bend2 results](benchmarks/bend2/results/summary.md)).
- `@[Native]` C: counters of exclusive ranges step unchecked (they cannot overflow).
- Runtime arenas (ADR 0037, P1.1): tree(23) build+sum 145 ms serial (records 522 ms), 15 ms with forks on 32 cores ([benchmarks/arena](benchmarks/arena)).
- Runtime dataflow networks (ADR 0038, P3.1): bounded SPSC rings and stages, 0 bytes per item ([benchmarks/dataflow](benchmarks/dataflow)).
- Runtime `Cm` data-parallel primitives (ADR 0039, P2.1/P2.2): scans, reductions, segmented forms, histogram, scatter-add, compaction, permutation and sort by key with SIMD tiers.

### IL backend

- Direct IL + portable PDB emission (ADR 0033, D1.2/D1.4): `alice run --backend il`, `alice emit --il`, deterministic MVID, Source Link, embedded sources, `.al` sequence points.
- Closure conversion (D1.5), synthesized declarations for records and properties (D1.6), IL-only lowering of foreach, interpolation, patterns, using/lock, switch, tuples and nullable lifting (D1.7), async/iterator state machines (D1.8).
- Dynamic sites (D1.9): `Alice.Runtime.Dyn` dispatches `Any` values with inline caches and Alice operator semantics; AL0232 reports dynamic sites under NativeAOT and wasm.
- FFI (D1.10): `lib` funs as blittable P/Invoke, callback trampolines, `@[Native]` `calli` with the managed fallback.
- AIR (ADR 0032, D0.2-D0.7): typed MIR nodes and spans replace C# text in lowering; emitted C# stays byte-identical on the corpus (`tools/emit-corpus.sh`); `alice emit --air` dumps it.

### Standard library

- Prelude Hash: `fetch` with a block, `delete`, `dig`, `each_key`/`each_value`, `store`, `update`, `merge` with a block, `values_at`, `slice`, `except`, `DefaultHash`.
- Prelude Enum: `to_i`/`value`, `@[Flags]` helpers, `values`/`names`/`parse`/`from_value` on the type.
- Prelude String/Char: `lstrip`/`rstrip`, `bytesize`, `codepoints`, `blank?`, `camelcase`/`underscore`, `to_i(base)`, `gsub`/`sub` with a block, Char predicates.
- Prelude Range/Array/Tuple: string and char ranges, `flatten(depth)`, `max(n)`, `slice_when`, `to_set`, `grep`, `transpose`, `sum(init)`, `filter_map`, Tuple `size`/`to_a`.
- Prelude objects: `p a, b`, `pp`, `hash`/`eql?`/`equal?`, `ENV`, `STDOUT`/`STDERR`/`STDIN`, `at_exit`; hashes print as `{"a" => 1}`, tuples as `{1, "one"}`.
- Alice Core (ADR 0036): frozen contracts in `Alice.Runtime/Core/Abstractions`; Alice spellings bind to .NET names on `Alice.Core` types (`empty?` -> `IsEmpty`).

### Tooling

- `alice prove` for CI and agents: `--format json`, `--timeout`, one-line counterexamples, exit 1 on failing, unproven or stale rules; `alice build --prove` and `[build] prove = true | "smt"`.
- `alice explain` prints one row per sum type (arena or records) and counts parallel helpers per method; `alice guide` shows the installed version.
- `alice fmt` keeps heredoc bodies byte for byte and ignores `%w[]`/`%q()` contents.
- CI installs Z3 on Linux, macOS and Windows and requires it (`ALICE_REQUIRE_Z3=1`).
- Tests: integration tests no longer hang on inherited pipes; steady-state allocation tests run alone; the telemetry field test checks names against docs/telemetry.md.
- Release: version validation accepts `MAJOR.MINOR.PATCH[-rc.N]` from 1.0.0 on; release candidates publish as prereleases.

### Security

- `SECURITY.md` (disclosure policy, supported versions, workspace trust) and a security limits table in [docs/production.md](docs/production.md#security-limits).
- `safe def` (AL0212) is an allow-list; reflection and dynamic-dispatch Prelude entry points carry `[Ambient]` and are rejected (audit F5, F6).
- `instance_variable_get/set` reach only Alice ivars and public foreign fields, never write readonly fields, and return copies of foreign or readonly arrays (audit F4).
- `alice-toolchain` files must name an exact version or channel; only toolchains under `~/.alice/toolchains` start.
- `dotnet`, `node` and `clang` start from absolute `PATH` entries only.
- `alice.toml` names, ids, versions and frameworks are validated; other values are escaped in the generated `.alproj`.
- `GITHUB_TOKEN` goes only to `api.github.com`; release assets download only over https from GitHub.
- `alice publish` pushes to NuGet itself over https with the key in a header (`--dotnet-push` keeps the old path).
- Telemetry endpoints must be https (loopback excepted); the collector has a global hourly cap.
- `install.sh` refuses plain-http URLs and redirects on the wget fallback.
- The `alice run` cache is owner-only and HMAC-checked.

### Docs

- [INVARIANTS.md](INVARIANTS.md): every guarantee, its scope, the tests that pin it and how to report a violation.
- ADRs 0032-0034 (AIR, direct IL backend, wasm runtime) and 0036-0039 (Alice Core, affine arenas, dataflow, Connection-Machine primitives) accepted; ADR 0040 (paradigms in IL) accepted as direction; ADR 0035 (bootstrap) withdrawn, the compiler stays C#.
- docs/errors.md has one row per code; docs/language.md covers slices, negative indexes, pattern pins, `class_property` writes, `affine enum`.
- README: the range pipeline is one fused loop (serial); parallel reduces apply to large array sources.

## 0.14.0 - 2026-10-27

- Telemetry ships disabled in this release: `alice` never asks, records or sends, and `alice telemetry` only reports
  that it is disabled. The opt-in machinery below stays in the code behind `TelemetryPolicy.Available`.

- Higher-order functions are specialized per lambda literal (ADR 0026): `apply(xs) { |x| x * k }` calls a clone of
  `apply` whose `yield` is a direct call of the block body, with `k` passed as a parameter. No delegate, no closure
  allocation, and the JIT can inline the body (a HOF-heavy benchmark: about 2.3x faster). `ALICE_NO_SPECIALIZE=1`
  turns it off.

- Borrow and lifetime checks (ADR 0027): passing one mutable value twice to a call that writes through one of the
  parameters is AL0217 (`copy_into(a, a)`); a `@[Stack]` value captured by a block, stored in a heap type's field
  or put in a collection is AL0218 (was a C# error at emit time); using a mutable value after sending it through a
  `Channel`/`Actor` is AL0219; writing a value a running `spawn` reads before the task is joined is AL0202.
- Loop interchange and cache tiling (ADR 0025), the `loop-tile` MIR pass. It applies to 2-3 deep counted nests over
  flat arrays. An affine dependence analysis delinearizes `i * n + j` and computes direction vectors. Each nest gets
  the legal order with stride-1 innermost access (`i-j-k` matmul becomes `i-k-j`). 3-deep nests and transposes are
  tiled when every dependence is non-negative. Tiles: 64 for matmul (half of L2), 32 for transpose (half of L1).
  `ALICE_TILE=n` overrides the size. The new nest runs behind a once-per-call guard (in bounds at every corner, no
  aliasing between written and read arrays); otherwise the original runs. Results are bit-identical. Checked
  integer arithmetic is not reordered. `benchmarks/tiling.sh` measures before and after.
- BEAM-style processes (ADR 0028). `Actor(T)` is now a supervised, linkable process: a crash kills only that actor,
  `link`/`on_exit` propagate exits, and `Supervisor.one_for_one` / `one_for_all` restart children in place with
  restart intensity (`max` restarts per window) and nested trees. `Actor(S, T).new(init, handler)` keeps isolated
  state built inside the actor. The mailbox is a lock-free queue and the actor is its own thread-pool work item:
  0 bytes per message in steady state, ~6 M msgs/s on one actor and ~11 M msgs/s over 64.
  Isolation is compiler-enforced: non-sendable message types are AL0230, actor blocks capturing mutable locals AL0231.

- `gpu for` launches (ADR 0021): explicitly grouped kernels with an occupancy-sized group (a power of two, a multiple
  of the warp size) and a capped grid, and a grid-stride loop. Reductions add in registers, reduce per group (warp
  shuffles, then shared memory) and do one atomic add per group instead of one per iteration: a 10M Int64 sum on an
  Iris Xe (OpenCL) 18 ms -> 4-6 ms. The device accumulator is cached, so a launch allocates no `new T[1]` or buffer.
  Write-only arrays are not uploaded (only the launch's range comes back), read-only arrays are not copied back, and
  a region skips the upload of an array its first loop overwrites whole.
- Opt-in CLI telemetry, disabled for now (ADR 0031, docs/telemetry.md): off by default, asked once on an interactive terminal (never in
  CI), and after `alice self update` only when no answer is stored. `alice telemetry on|off|status|show|reset|perf`;
  `ALICE_TELEMETRY_OPTOUT=1` / `DO_NOT_TRACK=1` always win. Command name, versions, OS/arch, exit code, duration,
  sanitized crash stacks and error-code counts; never source, paths or names, and never inside compiled programs. The
  default endpoint is disabled until a server is deployed. `tools/telemetry-server` is a self-hostable collector
  (ASP.NET Core + SQLite, strict validation, rate limit, 180-day retention, admin-only aggregates, Dockerfile).

- Stream fusion (ADR 0029): `take(n)` fuses into map/select/reject chains (`xs.map{}.select{}.take(n).sum` is one loop, no intermediate arrays); `to_a`/`map` after maps and takes write an exact-size array for arrays, `s.chars` and Int32 ranges; effectful stage blocks are no longer interleaved (an effectful stage before another block, a take or an early-exit sink runs stage by stage, as written); small literal arrays inside an inlined block go to one stack buffer before the loop.
- Tensor primitives in the runtime (ADR 0030): `Tensor<T>` (strided, zero-copy views over 64-byte aligned native
  memory), `TensorArena`, `BFloat16` and `Half` conversions, `QuantizedMatrix` (int8/int4 blocks of 32 with a Half
  scale, GGML Q8_0/Q4_0 layout) with fused dequantize-dot, `MappedFile` (memory-mapped spans and tensors, no copy), and
  `Nn` kernels: dot, GEMV (f32, bf16, Half, int8, int4), tiled parallel GEMM, elementwise, sum/max, fast exp, softmax,
  RMS/layer norm, silu/gelu/tanh/relu. AVX-512/AVX2/Vector128/scalar tiers, a persistent allocation-free worker pool,
  0 bytes allocated per call. Callable from Alice with `using Alice.Runtime`. `benchmarks/tensors`: GEMV 4096x4096 f32
  2.0 ms (naive loop 17.7 ms), int8 0.67 ms, int4 0.59 ms on the 12-thread laptop.

- WebAssembly packages (ADR 0023): `kind = "wasm"` in alice.toml, `alice new <name> --wasm` (page, boot script and an
  `@[JSExport]` example), `alice build` publishes the static site to `dist/wwwroot`, and `alice run` runs it under
  Node. `@[JSExport]` / `@[JSImport] partial def` call between Alice and JavaScript. No workload needed.
- Parallelization soundness fixes (never parallelize unproven code):
  - `for` loops writing one array in two index shapes (`a[i]` and `a[i * w + j]`), or reading `a[i]` while writing
    `a[i * w + j]`, stay serial; distinct arrays in different shapes get a runtime `ReferenceEquals` guard. Before,
    iteration 0's row overlapped iteration 1's cell.
  - A call to an instance method of a class with subclasses is pure only when every override is: a subclass's
    field-writing override could run inside a parallel loop or fork.
  - A loop whose body assigns a branch-declared local (`t = ... if c`, read later) stays serial: the local carries
    across iterations. Before, the parallel helper did not compile.
- AL0202 catches races it missed: a spawned/parallel block calling a field-writing method on self (`spawn { bump }`);
  index writes keyed by `par_each` values or `Par.Range`'s `hi` (only `Par.For`/`Parallel.For`'s index and
  `Par.Range`'s `lo` are disjoint, and only when the block does not reassign them); methods that write fields from a
  block, or by calling a field-writing method on self, now count as mutating.
- `Par.Sum` forks by index halves (`ParReduce`) instead of adding partials under a lock: no lock or closure, and the
  same float result on every run.

## 0.13.1 - 2026-10-23

- Errors that come from the generated C# point at the right Alice column: the name they quote (`frob`, or `to_widget`
  for .NET's `ToWidget`), or the line's first token. `#line` maps lines only, so these carried the C# column.
- ADR 0022 records `alice prove --smt`.
- `alice prove --smt` calls Z3's C API directly instead of the Microsoft.Z3 package (x64-only): it now runs on
  Apple Silicon and arm64 Linux, and the CLI has one dependency fewer.
- `find` is nilable (`T?`), as the lowering already produced: `if big = xs.find { ... }` on numbers is a nil check
  instead of a C# error, and null safety applies to its result.

## 0.13.0 - 2026-10-23

- `alice prove --smt`: opt-in Z3 (system libz3 or ALICE_Z3) proves rules over full `x : Int32` / `x : Int64` ranges, with overflow, division by zero and floored `/ %` semantics; untranslatable rules fall back to enumeration.
- Package builds type-check once: `alice gen` skips its in-process Roslyn check (`--no-check`) and csc errors are reported as Alice diagnostics. A one-file edit rebuild: 3.7 → 2.2 s. `AliceGenCheck=true` restores the old check.
- Version 0.13.0. This release was tracked as 0.12.3 until its scope (FFI, NuGet distribution, GPU, Avalonia, EF Core, source generators...) made a minor version the honest number. A bare `alice prove` skips nested directories that have their own `alice.toml`.
- Integration tests delete their temporary projects, feeds and NuGet caches. A full session had filled a 7.7 GB `/tmp` RAM disk with 2021 leftover directories, 175 MB per SDK-package run.
- `gpu ... end` regions keep the arrays of the `gpu for` loops inside on the device: each is uploaded once, and written ones are copied back at `end`. A CPU read inside the region is a compile error. 51 launches over 1M floats: 29 ms in a region against 132 ms with per-launch transfers.
- `gpu for` reductions: `t += e` into an outer Int32/Int64/Float32/Float64 is an atomic add on the device (100k-element Int64 sum, exact on the OpenCL GPU and the CPU accelerator).
- `gpu for i in a...b` (ADR 0021): the loop body is compiled to an ILGPU kernel and run on the preferred device. It ran on this machine's OpenCL GPU, with results identical to ILGPU's CPU accelerator, which `ALICE_GPU=cpu` forces. Admission is the `par for` independence proof plus GPU rules (arrays of numbers and numbers; arithmetic, indexing, `Math.*`, conversions; no calls, allocation or reductions), each rejection naming its cause. Kernel bodies are unchecked and not FMA-contracted: the device compiler contracts, and an explicit `FusedMultiplyAdd` was rejected by the OpenCL driver.
- GPU kernels in Alice: `examples/gpu/ilgpu` runs an Alice module function as an ILGPU kernel on the preferred device (it ran on this machine's OpenCL GPU; CUDA works the same, and ILGPU's CPU accelerator is the fallback). Covered by an integration test. To get there: a method reference to a static method lowers to the method group, not a wrapping lambda (C# caches the delegate, and APIs that need the real method get it); C#-style type arguments `ArrayView1D<Float32, Stride1D::Dense>` are accepted inside generic calls, with `>>` split; explicit type arguments work on generic extension methods.
- Avalonia UI: an `.axaml` window with its code-behind in Alice (`examples/ui/avalonia`, run headless in an integration test). Fixes on the way: the compiler reflected over reference assemblies (NuGet `ref/`, a project's `obj/.../ref/`), whose stripped private members crashed type loading (`TypeLoadException` on Avalonia's `StyledElement`), and now reflects over the implementation assemblies, while Roslyn still compiles against the references; `src/**/*.axaml` are `AvaloniaXaml` items in the generated project; extension methods inherited by a .NET base class are callable with implicit `self` (`FindControl<TextBlock>("x")`); a capitalized generic call without a receiver parses; and a paren-less member of unknown type standing alone as a statement is a call.
- Type-aware references and rename. The language server binds the project and keeps only occurrences resolving to the symbol under the cursor, or to an override of it in the same class hierarchy: `Square#area` renames without touching `Invoice#area`. Unresolved code falls back to name matching.
- Owned lists and hashes of any element type (`List(String)`, `Dictionary(String, Int32)`), not only of numbers. Only the collection must stay local, and `Clear` on release drops element references, so the pool keeps nothing alive.
- Class discovery: `Controller.subclasses`, `Controller.descendants` and `ObjectSpace.each_object(typeof(Controller)) { |c| }` over the program's own classes, which is what a framework needs to find controllers or plugins. Live-object enumeration is not offered: it has no safe .NET equivalent.
- Refinements: `refine String do ... end` inside a module is a monkey patch visible only in files that write `using <module>`. In C# it is an extension class in namespace `__Refine_<module>`, imported only by those files, so the scope holds there too.
- Forks of three calls in one expression (`trib(n - 1) + trib(n - 2) + trib(n - 3)`) via a three-way `Par.Fork`: `trib(32)` 335 -> 43 ms on 12 cores. A failing branch's exception surfaces once every branch has finished.
- Fix: an assignment inside a branch of a block now assigns the enclosing method's variable, as in Ruby. Before, `h.each { |k, v| best = v if v > best }` declared a new block-local `best`, and the method's `best` silently stayed 0. Found while checking the owned-hash benchmark's checksum, which was 0 under the old compiler too.
- Owned hashes (ADR 0016 amendment): a local `Dictionary(K, V)` of numbers that never escapes is pooled and its buckets reused (`benchmarks/gc/hashes.al`: 137-146 -> 97-104 ms, 2239 -> 0 MiB, 374 -> 0 gen0 collections). `Keys`/`Values` views count as escapes. `h.each { |k, v| ... }` on a Dictionary is inlined to `foreach (var (k, v) in h)`, with no closure or delegate.
- Owned lists (ADR 0016 amendment): a local `List(T)` of numbers that never escapes is rented from a per-thread pool and released at its last use, keeping its grown backing array. A scratch list built per call (`benchmarks/gc/lists.al`, 200k calls) took 59 ms, allocated 2926 MiB and ran 489 gen0 collections; now it takes 31 ms with 0 MiB and 0 collections. Views, spans, enumerators, storing and capturing keep a list on the GC, and a returned list moves out unreleased.
- View models for XAML UIs: `event PropertyChanged : PropertyChangedEventHandler` (capitalized event names, required to implement .NET interface events) and Ruby's private `@count` beside a public `def count` (the field takes another C# name instead of colliding). An `INotifyPropertyChanged` + `ObservableCollection` view model is tested.
- Language server: find references and rename. Matches come from the lexer, so strings and comments are left alone but `#{...}` holes are found. Top-level names are renamed across the project and locals within their enclosing `def`; an invalid new name is refused.
- Incremental package builds: `alice gen` runs only when a source, the reference or analyzer list, the compiler or the project changed (MSBuild `Inputs`/`Outputs` on `AliceGenerate`, with a sources list so that added and deleted files rebuild). A no-op `alice build` takes 1.0 s instead of 2.35 s. Covered by an integration test for no-op, added-file and deleted-file builds.
- `partial def` (no body): a declaration a source generator completes, for `[GeneratedRegex]` (regex compiled at build time) and `[LibraryImport]` (.NET's source-generated P/Invoke) next to Alice's own `lib`/`fun`.
- `docs/production.md`: what is ready for production, the test behind each claim, recommended `alice.toml` settings per workload, and the known gaps.
- F# consumes Alice libraries built with `naming = "dotnet"`: `Geometry.Distance`, `IsEmpty`, records, and sum types matched with `:? Shape.Circle as c -> c.R` (`Integration/FSharpInteropTests`, a real `.fsproj`).
- Roslyn source generators work on Alice code (`[JsonSerializable]` contexts for reflection-free, NativeAOT-safe JSON; `[GeneratedRegex]`; `[LoggerMessage]`; NuGet generators). `alice gen`'s type check now runs the generators MSBuild supplies (`@(Analyzer)`) instead of failing on members they add, and single-file builds find the framework's generators in the targeting pack. Framework generators load only when their attribute appears: hello world compiles in 1.06 s as before, not the 1.55 s it took with every generator loaded.
- Multi-line blocks inside call parentheses parse: `Task.Run(->() {` followed by statements on separate lines. The lexer dropped every newline inside `(`/`[`, so statements in a nested `{ }` ran together. It now tracks the innermost bracket.
- FFI stress tests (`FfiStressTests`): strings at the 85/86-char stack-buffer boundary with 2- and 4-byte UTF-8, lone surrogates (encoded as U+FFFD), 8 threads x 20k concurrent string calls, repeated callback exceptions, and empty and 16 MiB pinned arrays.
- Entity Framework Core works from Alice. `examples/data/efcore` defines a `DbContext` subclass with an `OnConfiguring` override and `DbSet` properties, and runs LINQ queries that EF translates to SQL from the Alice lambdas (`WHERE "b"."rating" > 3`). It is covered by an integration test. To get there: extension methods from any referenced assembly are callable without parentheses (`query.ToQueryString`), not only LINQ's; `obj.tap { |o| ... }` and `obj.then { |o| ... }` were added; and a multi-statement block passed where an `Action` is expected drops its tail value.
- `x.try &.m(args)` (Crystal): the safe call `x&.m(args)`, no longer "planned".
- Lambdas as blocks: `xs.sum(&->sq)`, `xs.select(&->(x : Int32) { x.even? })` and `xs.map(&f)`. A lambda literal or method reference passed this way is inlined into the loop like a literal block, with no delegate.
- Named tuples: `{name: "ada", age: 36}` and the type `{min: Int32, max: Int32}` are C# named tuples (`(string name, int age)`, no allocation), read with `t.name` or `t[:name]`, where an unknown element is a compile error. Hashes keep `=>`.
- Assignment as a condition: `if user = find(id)` and `while line = reader.ReadLine`. The `while` form assigns before every test, and `elsif x = f` evaluates `f` only when the earlier conditions failed.
- Flow narrowing for nullable numbers: inside `if n` and after a guard clause (`return unless n`, `break`/`next unless n`, `raise ... unless n`), an `Int32?` local reads as `Int32` (`n.Value`). Narrowing stops where the branch reassigns the local. C# already narrows nullable references.
- Conditions use Ruby truthiness: `if s`, `unless t`, `while q`, `s ? a : b` and `!s` on a nullable or reference value are nil checks, and `Bool?` counts only when it holds `true`. Before, `if s` on a `String?` failed with "expected Bool", despite the guide recommending it.
- `gc = "auto"` caps server GC's gen0 budget at 16 MB (`System.GC.Gen0MaxBudget`). Polyglot suite at its default size (cold runs of 60-110 ms, before vs after this release): uncapped server GC added 6-17 ms to some workloads (merkle 79 -> 93 ms); with the cap the cost is 0-10 ms (merkle 87.5 ms). Large parallel heaps keep the win (tree(23) build 116 vs 698 ms on workstation). Short-lived CLIs that must start fastest can set `gc = "workstation"`.
- Examples: `data/trees.al`, `dotnet/library_api.al`, `interop/libc.al`, and `tools/wand` (a generator CLI packaged as a .NET tool). `Integration/ExampleTests` runs every standalone example and checks its output.
- `lock obj ... end` (C# `lock`; `System.Threading.Lock` or any reference). A value-type target is a compile error, and writes inside are still race-checked: `Mutex(T)` remains the way to share Alice data.
- Method references: `->sq`, `->sq(Int32)`, `->shape.area`, `->Geometry.dist`. A reference is a lambda whose parameter types come from the method (explicit types pick an overload), so it passes as a block value, a `Func` or any delegate.
- Building and folding sum types fork. A variant constructor (`Tree::Node(l, r)`) is allocation, not an effect, and a `case` whose subject, patterns, guards and arms are pure is pure. Before, neither `build` nor `total` of `benchmarks/inet/tree.al` forked.
- `[runtime] gc = "auto"` is the new default. The build picks server GC (DATAS) when the compiled program forks or runs parallel loops, and workstation GC otherwise. Parallel code building long-lived data was collection-bound on workstation GC: tree(20) build took 85 ms (57 gen0 collections copying the live tree) against 24 ms on server GC, and tree(23) took 698 vs 137 ms. A serial hello world starts in 0.02 s either way, with 27.9 vs 28.9 MB. `workstation`, `server` and `low-memory` still force a mode.
- `Span(T)` / `ReadOnlySpan(T)` take `each`, `each_with_index`, `sum`, `count` and the other inlined iterators, lowered to an indexed loop over the span (no allocation, no enumerator). Before, they failed with AL0312.
- A `struct` with initialized properties (`property port : Int32 = 80`) failed to compile, because C# runs initializers only through a declared constructor. It now gets one. Field-only attributes on properties (`FieldOffset`, `MarshalAs`, `NonSerialized`, `ThreadStatic`) target the backing field, so `StructLayout(Explicit)` unions for C interop compile.
- Alice on NuGet (ADR 0020): `Alice.Sdk` is an MSBuild project SDK carrying the compiler, so `<Project Sdk="Alice.Sdk/x.y.z">` compiles `.al` files in any project with nothing installed (tested with an empty NuGet cache). `Alice.Cli` installs the `alice` command with `dotnet tool install -g Alice.Cli`. `Alice.Runtime` is packable for C# hosts. `scripts/pack-nuget.sh` builds all three packages, and the release guide covers pushing them.
- `alice new <name> --tool` creates a .NET tool package (`kind = "tool"`, `command`, an ARGV template). `alice new --lib` now writes `description` and `naming = "dotnet"`, so a new library is ready for `alice publish`.
- `[package] naming = "dotnet"` (ADR 0019): a library's public members compile to .NET names (`total_area` -> `TotalArea`, record `x` -> `X`, `empty?` -> `IsEmpty`), so C# and F# callers see a conventional API. Alice code keeps the Alice spelling, and `send`/`respond_to?` fall back to the .NET name. Private members and Ruby hooks are unchanged. Default `"alice"` leaves output as before.
- `await using r = x ... end` in `async def`: `DisposeAsync` is awaited at `end` (EF Core contexts, async streams). `alice fmt` now indents `using` / `await using` blocks, which it never did, as well as interface default methods and `unsafe def`.
- Declaring .NET events (`event clicked : EventHandler`, raised with `clicked&.Invoke(self, args)`), delegate types (`delegate Op(a : Int32, b : Int32) : Int32`) and default interface methods (an interface `def` with a body). An Alice library can now expose the same API shapes a C# library does.
- Nullable type arguments of .NET generics: `Dictionary(String, String?)` and `List(String?)` emitted as `string` (CLR generics carry no nullable-reference annotation), so `d[k] = nil` was rejected as AL0310. They now keep `string?`, and reading the value still has to be nil-checked.
- Iterators: a method returning `IEnumerable(T)`, `IEnumerator(T)` or `IAsyncEnumerable(T)` with no block parameter is a generator. `yield x` is `yield return`, a bare `return` is `yield break`, and evaluation is lazy (`nums(1_000_000_000).Take(3)`). `async def` streams work with `for await`. A class listing `IEnumerable(T)` as a base gets the non-generic `GetEnumerator` bridge. Ruby block `yield` is unchanged.
- Attributes on record parameters: `record Span(start : Int32, @[JsonPropertyName("end")] stop : Int32)`. They target the generated property, where System.Text.Json and other serializers read them; `@[param: X]` targets the parameter. JSON keys that are Alice keywords (`end`) now map to typed records.
- `fun` parameters can be `out x : T` / `ref x : T`. C receives a pointer to the caller's variable, pinned for the call, so out-parameters and structs passed by pointer need no `unsafe` (`LibC.clock_gettime(0, out ts)`, `LibC.frexp(8.0, ref e)`). `out` locals at the call site get the declared type.
- Native callbacks: a `fun` parameter of type `Callback(A, B, R)` takes a lambda literal, lifted to a static `[UnmanagedCallersOnly]` C function (`qsort` with an Alice comparator). No delegate, no GC handle, no allocation; the call site needs no `unsafe`. A capturing lambda is error AL0215. An exception raised in the callback no longer kills the process: it is caught and rethrown once the C call returns.
- `unsafe ... end` blocks and `unsafe def`: the only places where `Pointer(T)` values (from `fun malloc ... : Pointer(UInt8)`, `p[i]`) are allowed. Elsewhere they are AL0216 with a hint, instead of a raw C# error. Safe FFI types (numbers, strings, arrays) need no `unsafe`.
- FFI (ADR 0018): `lib` `fun` takes `String`/`String?` parameters as UTF-8 C strings on every OS (stack buffer, no allocation; an embedded NUL raises instead of truncating), returns `String?`/`String` as borrowed UTF-8 (`NULL` is `nil`, or raises for `String`), and pins `Array(number)` arguments. Every import is now blittable behind an inlined wrapper: no marshalling stubs, so NativeAOT has none to precompile. Fixes heap corruption: a `String` return used to be freed by the .NET marshaller (`getenv` crashed with `free(): invalid pointer`). Per call, a 12-char string costs 15.5 ns against 15.6 ns for .NET's UTF-8 marshaller.
- .NET members take Alice spellings on instances too: `sw.elapsed.total_milliseconds`, `sb.append_line(x)`, `sw.is_running`, and predicates `d.contains_key?(k)` / `uri.absolute_uri?` / `dir.exists?` (tries `IsX`, then `X`; must return `Bool`). Before, only statics (`Math.sqrt`, `Stopwatch.start_new`) were mapped. A Prelude method of the same name still wins wherever its receiver type fits (checked from the Prelude's signatures), so `s.split`, `xs.sum` and `task.wait` keep their Ruby meaning.
- Fork/join: a forking method gets `m__serial`, a clone with no forks whose recursion stays in the clone, and the sequential branch of every fork calls it. Before, every call below the fork depth read the thread-static `Par.Forkable`, which made `pow2` 2x slower than plain recursion. Compiled `pow2(26)`: 51.3 -> 15.7 ms. Found by `benchmarks/inet`, an HVM2-style interaction-net reducer on .NET. It runs (60-100 M interactions/s per thread) but stays 17-30x slower than serial code on 12 cores for this workload, so fork/join stays the parallel model ([README](benchmarks/inet/README.md)).
- `alice pack` and `alice publish`: Alice libraries and tools ship as NuGet packages. `[package]` takes `id`, `description`, `authors`, `license`, `repository`, `homepage`, `tags`, `copyright`, `icon`, `readme` (default `README.md`); bad ids, versions and kinds fail before `dotnet pack`. `kind = "tool"` (with `command`) packs a .NET tool, so a CLI or framework generator written in Alice installs with `dotnet tool install`. Library packages carry `Alice.Runtime.dll`, so C# consumers need nothing else. `alice publish` pushes to nuget.org (`--api-key` or `NUGET_API_KEY`, never echoed), a private feed, or a local folder; `--dry-run` packs only. Tested end to end: pack, push to a folder feed, `dotnet tool install`, run.
- Owned results lent straight to a borrowing parameter (`t += dist(seq(a), seq(b))`) live in a temporary for the statement and go back to the pool after it. `benchmarks/gc` `rows`: 0.06 MiB per run down to 0.00.
- `alice build file.al --native` keeps the directory layout of the entry file and everything it `require`s. A flat copy broke relative `require` paths, so multi-file programs could not be built natively. `benchmarks/alice/celeste_update` now builds: 1.8 ms per trial native against 11.2 ms under the JIT (the program ends before tiered compilation warms up), 15.5 MB against 40 MB peak.
- Presized collections: `xs = List(T).new` or `SoA(T).new` directly followed by a counted loop that adds exactly once per iteration (no `break`/`next`) is sized to the trip count first. SoA stores gain `reserve(n)`. `benchmarks/alice` `particles`: 128 to 61 MiB allocated, gen2 collections 10 to 2 (C#: 3), 9% less wall time. Found with a per-process profile (JIT time, methods compiled, bytes allocated, GCs) of every benchmark against its C# baseline; `wordfreq` reports 85-158 ms of JIT time, but it is mostly tier-1 recompilation on the JIT's background thread: turning tiering off cuts it to 35 ms with no wall-time gain and a 19% slower kernel, and forcing full optimization makes the run 26% slower. The default tiered + PGO setup stays.
- Polyglot suite profile (17 workloads, same per-process hook, Alice vs C#): Alice is faster and allocates far less on every substantial workload (tree-radix 117 vs 486 ms and 201 vs 1031 MiB; editdist 0.6 vs 1025 MiB with 0 vs 170 collections; symreg 116 vs 571 ms; hashmap 181 vs 409 ms; merkle 469 vs 663 ms). On the ten 40-60 ms workloads Alice is 5-10 ms slower; an empty Alice program starts as fast as an empty C# one (8 ms, 33 vs 30 methods), and the gap came from the Alice adapter returning `Dictionary(String, Object)` results that serialize through boxing. With typed records (same JSON, outputs byte-identical on all 17 workloads) the small workloads run within 2-5 ms of C# or ahead (histogram 39 vs 37 ms, symreg 40 vs 41, kmeans 43 vs 47). `lexer` keeps a dictionary: its JSON key `end` is an Alice keyword, and record parameters do not take attributes such as `JsonPropertyName` yet.
- `[runtime] globalization = "invariant"` in `alice.toml`: no ICU at run time, 2-4 MB less memory on every program measured. Strings sort ordinally (Ruby's order) in that mode; the default stays `culture`.
- Runtime knob survey (median of 5, four program shapes): turning off tiered compilation, PGO or quick JIT for loops helps short programs and slows long ones; ReadyToRun for `Alice.Runtime.dll` and GC gen0/conserve tuning gave no gain. The performance guide records the results and recommends `--native` for short-lived programs.
- Guide: no `rescue` modifier and no `<` on strings (use `<=>`) in the Ruby habits table.
- Every `benchmarks/alice` program builds with NativeAOT with no trimming or AOT warnings and prints the same results as under the JIT, using 20-70% less memory.
- Forks of `1 + l.check + r.check` over nullable tree children stay serial on purpose: forking it measured 21.5 -> 32.6 ms on `benchmarks/gc` `trees` (tasks of under a microsecond). The reason is recorded in the code and a test.

## 0.12.2 - 2026-10-22

- Explicit, checked paradigms on top of inference (ADR 0017):
  - Effects: `pure def` (AL0210) and `safe def` (secure execution, no ambient authority, AL0212).
  - Parallel let: `par a, b = f(x), g(y)` (Bend's fork without the recursion rule, AL0211).
  - Inlining: `inline def`.
- Ownership:
  - `own` parameters (the caller moves in).
  - `borrow` parameters (checked, AL0213).
  - `scope ... end` lifetime regions (arrays must be owned and are released at `end`).
  - In-place `xs = xs.map { }` for owned arrays. A 3-stage map over 1M `Float64`: 4.0 ms and 3 B per call, against 9.3 ms and 32 MB for hand-written C# loops.
- Owned results (ADR 0016 amendment): an array returned by a method that returns only its own owned local is owned by the caller and recycled. `benchmarks/gc` `scratch`: 3.81 MiB per run down to under 400 B, 0 collections.
- `alice run file.al` caches the compiled image by content hash (`~/.cache/alice/run`, `ALICE_NO_CACHE=1` to disable): an unchanged program starts in 0.13 s instead of 1.2 s.
- Shared immutable objects: `T.new(constants)` of an immutable class whose identity the program never observes is one static instance. `benchmarks/gc` `trees`: 68 to 34 MiB per run, half the collections, about 1.35x faster than C# (21.5 vs 28.9 ms).
- Interop: `fun` signatures over numbers and `Bool` are blittable (a `Bool` crosses as one byte behind an inlined wrapper), with no marshalling stub; a `Bool` C call is 2.7x faster. `@[Fast]` on a `Bool` function no longer fails at run time. Imports use `ExactSpelling`.
- Rules and proofs: `rule name(x in lo..hi, b : Bool) ... end` (RULES.al) declares a pure property over a finite domain. `alice prove` checks every case and writes `PROOF.al`, a certificate tied to a hash of the sources. `alice prove --check` fails when a rule fails or PROOF.al is stale. Domains over 2^32 cases are AL0214. The repository's `RULES.al` proves Alice's numeric semantics; `dotnet test` checks it.
- `divmod`, `tdiv`, `remainder` and `modulo` count as pure.
- `alice explain file.al`: per method, the optimizations applied (fork/join, parallel loops, owned arrays, in-place maps, shared objects, stack arrays, SIMD, fast native calls).
- `alice guide [performance|proofs|interop]` prints the guide from `guide/`, embedded in the CLI, for people and AI agents; every `alice` code block in it is compiled by the tests. `alice agents [--write]` prints or merges an Alice block into a project's `AGENTS.md`: workflow, Ruby/Crystal habits that are wrong in Alice, error codes. `alice new` writes `AGENTS.md` with that block and a `CLAUDE.md` that imports it (`@AGENTS.md`).
- Tests that run programs in `ExplicitParadigmTests` join the `console` collection.
- Fork/join of independent redexes (HVM-style): a statement whose value is a pure arithmetic term with exactly two calls to pure user methods, one recursive, forks the two calls without named results (`fib(n - 1) + fib(n - 2)`, `1 + l.count + r.count`). Calls under `&&`, `||`, `??` or `?:` are never forked, and a fork never captures a nullable local, because the helper would lose flow narrowing. Naive `fib(38)` on 12 cores: 250 ms -> 145 ms.

## 0.12.1 - 2026-10-22

- Loops over an owned array bounded by its fixed length (`for k in 0...n` over `Array(T).new(n)`) compare against `buf.Length`, so the JIT removes their bounds checks as it does for C#'s `new T[n]` (ADR 0016 amendment).
- README: mutability, ownership, fork/join and benchmark sections.
- `benchmarks/gc`: allocation-heavy workloads (rolling rows, per-call scratch buffers, 4 MiB frames, object trees) in Alice with inferred ownership, Alice with explicit `own`, and C#, under workstation and server GC. Reports time, allocated bytes, collections, GC pause and peak memory, with charts. Alice runs `rows` and `frames` with 0 collections (C#: 74-1002) and is about 2x faster than C# on `scratch` and `frames` under server GC.

## 0.12.0 - 2026-10-22

- Breaking (ADR 0013): signed `/` and `%` are floored like `divmod` (`-7 / 2 == -4`); `tdiv`/`remainder` truncate. Float `%` follows the divisor.
- Breaking (ADR 0013): integer overflow raises `OverflowException`; `&+ &- &*` wrap. `2 ** -1` raises instead of returning 1.
- Breaking (ADR 0013): `to_u8` and the other sized conversions raise when out of range (NaN too); `to_X!` wraps. `2.5.round` is `3.0` (half away from zero).
- Breaking: floats print with a decimal point and Ruby exponent rules everywhere (`2.0`, `1.0e-07`, `1.0e+16`).
- Generated programs default to workstation GC (faster startup, lower RSS); `[runtime] gc = "server" | "low-memory"` in `alice.toml`.
- Startup: the `format` regex is source-generated (no Reflection.Emit on every launch); an `if`/`case` with a `raise` branch no longer becomes `dynamic` (the C# runtime binder was loading at startup).
- Fix `(-5).abs` and `-7.5.abs` returning negative values (the C# emitter dropped the parentheses).
- Fix `round(n)`/`floor(n)`/`ceil(n)` on decimal fractions (`0.29.floor(2)` was `0.28`).
- `[a, b, c].min`/`.max` on a literal lower to `Math.Min`/`Math.Max` with no array allocation (editdist inner loop).
- Compiler: MIR pass pipeline (ADR 0014) in `src/Alice.Compiler/Optimizer`: constant folding, strength reduction (`x / 8` is `x >> 3`, `x % 16` is `x & 15` with floored semantics), literal `[a, b].min/max` without allocation, unchecked counted-loop steps. `ALICE_DISABLE_PASSES` for A/B runs.
- Compiler: fork/join of independent pure recursive calls (ADR 0015): `a = f(x)` / `b = f(y)` run in parallel with lazy task creation (only the top log2(cores)+3 levels spawn tasks). Writes into arrays allocated in the method keep it pure. Suite `tree-radix` at 1M elements: 3226 ms → 319 ms (C# 665 ms).
- Breaking: `Channel(T).new` is unbuffered (a rendezvous, as in Crystal): `send` waits for a receiver. It was silently unbounded. `Channel(T).new(n)` buffers n. The channel is a lock-guarded ring (no Task per blocking call, no allocation per message); `send` after `close` raises `ClosedChannelError`.
- Row flattening: an array of fixed-width literal rows filled by a full `for i in 0...n` loop and never escaping (`points[i] = [x, y]`) is one flat array `points[i * k + j]` with the column still bounds-checked (suite kmeans at 1M points: 0.44 s / 95 MB → 0.38 s / 63 MB).
- Fix `Array(T).new(n) { |i| ... }` ignoring its block.
- Heap-free small arrays: a local array of literal size (<= 256 unmanaged elements, or a literal of <= 32 items) that never escapes lives in one `stackalloc` buffer at method entry, also when declared inside a loop.
- Fix `Array(T).new(n, v)` ignoring `v` (it returned zeros); a non-default fill uses an uninitialized allocation plus one `Fill`.
- Range analysis: counted loops bounded by `.Length`/`.Count` and their `i ± k` offsets drop provably dead overflow checks (editdist: 998 → 884 ms, C# 841 ms).
- Stdlib fork/join: `sort` of a large primitive array is a parallel merge sort with a pooled buffer (10M `Int32`: 756 → 207 ms).
- Docs: [fork/join](docs/fork-join.md) (idea credited to Victor Taelin's BendRT) and [heap and GC](docs/heap-gc.md).
- Value-range analysis: integer `+ - *` whose operand ranges (literals, masks, shifts, widened Int32, `^`/`|` of non-negatives, floored `% d`) prove no overflow are emitted unchecked; a proven `x >= 2^31 ? x - 2^32 : x` (also 8/16 bits) is one sign extension instead of a data-dependent branch. Suite terrain at 1M: 345 → 212 ms (C# 189 ms).
- Purity gate: `Alice.Runtime.Pure` marks runtime functions the compiler may parallelize through (`Digest::*`); static `Module.f(x)` receivers, string interpolation, array literals, `min/max/sum` and the new numeric primitives are pure reads. Suite merkle at 1M leaves: 1812 → 1489 ms (C# 1885 ms).
- Stdlib fork/join: `sum`, `min`, `max` of primitive arrays of 2^20+ elements split into SIMD chunks across cores (32M `Float64` sum + max: 68.7 → 16.4 ms).
- `2.0 ** n` lowers to `Math.ScaleB` and `x ** 2` to `x * x` (both bit-identical to `Math.Pow`); widening conversions (`to_i64` on Int32, `.to_f`) are plain casts, narrowing ones an inlined checked helper.
- Fork/join also forks Bend's single-statement parallel call `a, b = f(x), g(y)` (pure, independent, recursive), including into existing variables.
- Startup: Prelude's dynamic/reflection/regex caches are created on first use instead of when every program starts.
- Compiler: a loop nested in a serial loop is no longer auto-parallelized with the "heavy" threshold of 2 (game of life dispatched its 3-cell neighbourhood loop to the thread pool per cell: 197 ms → 60 ms); constant short trip counts emit no parallel path.
- Fix `for k in 0...m` with `m : Int64` (and `n.times` on Int64) declaring an `int` counter that could never reach the bound.
- Allocation-free: `round(n)`/`floor(n)`/`ceil(n)`, `q, r = a.divmod(b)` (no array), `a.step(limit, k)` with a literal step (inlined loop), and `puts`/`str`/`inspect` of numbers (no boxing).
- `Digest::SHA256.hexdigest`, `Digest::SHA1`, `Digest::MD5` (Ruby API).
- New primitives: `to_i8`..`to_u64`, `to_i32`, `to_f64`, `fdiv`, `sign`, `popcount`, `leading_zeros_count`, `trailing_zeros_count`, `bit_length`, `finite?`; `Int32::MAX`, `UInt8::MIN`, `Float64::INFINITY`, `Float64::NAN`; snake_case .NET statics (`Math.sqrt`, `Math.atan2`).
- Ownership (ADR 0016): local arrays of unmanaged elements that provably never escape are owned; their buffers return to a per-thread exact-length pool (`Own(T)`) before reassignment and at every exit, so a loop building a fresh row per iteration stops allocating. Moves (`prev = row`), interprocedural borrows and returns keep ownership. `own a = ...` makes it a checked contract: escape AL0207, use after move AL0208. 12 of the 17 suite workloads use it; editdist RSS 40.7 MB (C# 46.9 MB).
- Auto-parallel loops prove row-major writes disjoint: `img[y * w + x] = ...` (directly or through a local bound once) with `x` counting `0...w` in a nested loop runs rows across cores, as game of life, mandelbrot and image loops are written.
- Runtime: `Par.Fork` offers its left half as one thread-pool work item instead of a `Task` (no Task, wrapper closure or continuation per fork); the join runs an unclaimed half inline, spins briefly, then blocks on the job. When the right half throws, the left half finishes before the exception unwinds.
- Syntax: `par for i in a...b` parallelizes at any trip count and is compile error AL0209 when iterations are not provably independent; `unchecked ... end` is a wrapping region for integer `+ - *` (ADR 0013). `alice fmt` indents both.
- Benchmarks: `run.sh` runs Alice with the same server GC as the C# baselines; run 8 recorded (Alice at or ahead of idiomatic C# on all eight). The recursive-kernel rule requires a primitive result, so object-building recursion keeps tier-1 PGO.
- `examples/verify.sh` expects 0.12 float printing (`30.0`).
- Version 0.12.0.
- JIT tiering (ADR 0014): methods with a loop nest, and primitive-only static functions that loops call, compile fully optimized from the first call instead of running instrumented tier-0 code until OSR / delayed tier-up (suite nbody 151 → 78 ms, terrain at 512x512 143 → 115 ms).
- Parallel loops probe before dispatching: the first two iterations run serially, and the rest goes to the pool only when it projects past the dispatch cost (25 ms cold, 50 us warm); the runtime scheduler samples a chunk the same way. Short loops in short programs no longer pay thread-pool startup (small mandelbrot 80 → 75 ms); large ones stay parallel (4M-pixel mandelbrot 247 → 157 ms vs serial). Parallel chunk bodies compile fully optimized.
- Floored `/` and `%` by a positive literal are branch-free (`floor_div_pos`, `floor_mod_pos`).
- Fix: a loop whose body assigns its own counter is never auto-parallelized.
- Fix (ADR 0008): a counted loop whose body read the array it writes at another index (`row[j] = row[j - 1] + ...`) was auto-parallelized from 65,536 iterations and could compute wrong values. Such loops stay serial; stencils over a different array stay parallel behind a runtime `ReferenceEquals` alias check.
- Fix: an exception raised inside an auto-parallelized loop reaches the caller as itself, as in the serial loop, not wrapped in `AggregateException`.
- Runtime: `Par.Range`/`Reduce`/`MapSum` run on a lean chunk scheduler (`ParJob`) instead of `Parallel.For`: one allocation per loop, an atomic chunk counter, and the calling thread works through chunks itself, so nested parallel loops cannot starve the pool and no TPL machinery is JIT-compiled at startup.
- Shift counts are modular (ADR 0013): the `+ - *` computing `x << n` drop their overflow checks.
- A self-recursive function over primitives and primitive arrays compiles fully optimized from its first call (`AggressiveOptimization`). Suite queens: 0.92 s → 0.81 s (C# 0.91 s).
- `q, r = 7.divmod(2)`: multiple assignment destructures arrays and lists.
- Hex and binary literals accept `_` separators and type suffixes (`0xFFFF_FFFF_i64`, `0xFF_u8`).

- `samples/wonderland3d`: Alice-authored procedural 3D Wonderland film, interactive preview, deterministic 30-second MP4 export, GLSL bloom and an original synthesized stereo score (ADR 0012).

- Windows installer: launch Microsoft's .NET SDK installer with a child-process execution policy override, preserving the user's policy and reporting SDK installation failures.

- Root npm commands build/test Alice and generate VSIX/Zed packages from the companion `extensions/` checkout.

## 0.11.2 - 2026-10-16

- Fix editor document-symbol ranges after edits and mixin expansion: outlines use current source declarations and keep selections inside their full ranges.

## 0.11.1 - 2026-10-16

- `alice lsp --stdio`: incremental editor diagnostics, completion, .NET/NuGet members, auto-import quick fixes, hover with XML documentation, definitions, document symbols, formatting and signature help (ADR 0011).
- Editor project assistance: NuGet ID/version completion in `alice.toml` and `.alproj`, setting hover, manifest/XML formatting, and restored C#/NuGet/framework references.
- Rust editor adapters in the companion extensions repository: VS Code/VSCodium napi-rs client, grammar, snippets and icons; Zed Rust/WASI client and pinned tree-sitter highlighting.
- Project metadata uses collectible contexts without locking C# dependency DLLs; rebuilt APIs refresh in the editor.
- Fix incomplete parameter lists looping at EOF; serialize shared CLR type lookup for concurrent editor queries.

## 0.11.0 - 2026-10-16

- Toolchains (ADR 0010): `alice toolchain list|available|install|uninstall|default|pin|unpin|which|show`; versions side by side in `~/.alice/toolchains`, `alice-toolchain` pin files, `ALICE_TOOLCHAIN`, and `alice +VERSION <command>`.
- Updates: `alice self update|check|uninstall`, `alice upgrade`; a daily background check prints a one-line notice when a newer stable exists (off in CI and with `ALICE_NO_UPDATE_CHECK=1`).
- Install: `curl -fsSL https://alice-lang.org/install.sh | sh` or `irm https://alice-lang.org/install.ps1 | iex`; an `Installers` workflow tests both on clean Windows, Linux and macOS runners after every release.
- Installers: `scripts/install.sh` and `scripts/install.ps1` verify SHA-256, install the .NET 10 SDK per-user when missing (no admin rights, no Visual Studio), and set PATH. They are attached to every release and served by the site.
- CLI: `alice doctor` checks the environment; `alice completions bash|zsh|fish|pwsh`.
- REPL: live syntax highlighting, bracket matching, grey history hints (→ accepts), ↑/↓ prefix search, Ctrl-R reverse search, word motions and kills, a column completion menu, auto-indented continuation lines, `:type`, `:history`, `:clear`.
- Release: nightly channel (manual `channel: nightly` or a daily schedule) publishes `X.Y.(Z+1)-nightly.YYYYMMDD` as the rolling `nightly` prerelease; unchanged commits are skipped. `alice version` reports the full informational version.

## 0.10.1 - 2026-10-16

- Fix: escape package-mode brackets in the interactive REPL banner and `:help` so they render without Spectre.Console markup exceptions.

## 0.10.0 - 2026-10-16

- Fix: manual Release runs publish the validated version's GitHub release after packaging, including when the full test stage is skipped.
- Fix: build and smoke-test the ReadyToRun `osx-x64` package on `macos-15-intel`; reject mismatched packaging hosts before publishing.
- Release: optional `skip_tests` checkbox for manual runs and `RELEASE_SKIP_TESTS=true` Actions variable for tag pushes; package smoke checks remain required.
- Fix: package smoke checks recognize the current `alice test` summary (`2 examples, 0 failures`).
- Docs: feature a captured Celeste Classic gameplay demo in the README gallery, with sample instructions and original credits.
- Tests: use the platform newline in REPL assertions and tolerate Windows assembly locks during temporary-directory cleanup.
- Fix: generated-project type checking retains ASP.NET and other framework-pack references instead of dropping every SDK pack.
- Tests: use platform-native math libraries, paths and newlines; account for ARM64 fused multiply-add support.
- Docs: refreshed Alice introduction, quick start, practical example catalog and contribution guide.
- Examples: organized basics, data, concurrency and web programs; ASP.NET Core API written in Alice, backed by an Alice pricing library.
- Automation: cross-platform tests and gated binary release archives with package smoke checks and SHA-256 checksums (ADR 0009).
- Compiler rewritten as Syntax -> Semantics (HIR) -> Lowering (MIR) -> Backend (C# + Roslyn Release IL) (ADR 0007).
- Inference: Crystal-style return types across later defs; unresolvable -> `dynamic`; Ruby function-scoped locals.
- Language: symbols, `send`/`respond_to?`, `yield` with `&block : A -> R`, `if`/`case` expressions, `val`, `Any` = dynamic.
- Attributes: `@[Inline]`, `@[NoInline]`, `@[Hot]`, `@[Stack]` (ref struct).
- Concurrency: `spawn`/`t.wait`, `Channel(T)`, `Actor(T)`, `par_map`; spawn data-race check is a compile error.
- Performance: literal-block iterators inlined to loops, map/select chains fused (0 allocations), `static` capture-free lambdas, virtual only where subclassed, invariant non-boxing interpolation.
- CLI: `alice run|build|emit`; `build --native` via NativeAOT; `-r` extra references.
- Runtime: `Simd`, `CsrMatrix`/`SparseVector`, `Par`, `AMath`, `Channel`, `Actor`, `Prelude`.
- Removed: clang native kernels, `Native` class, `alice bootstrap`, `selfhost/` (ADR 0003 superseded; ADR 0002 amended).
- Nullable reference types enforced; null-safety diagnostics are errors.
- Architecture docs and ADRs 0001-0008.
- Language: sum-type enums with fields + exhaustive `case` (AL0201); `record` / `record struct`; generic classes and `forall T where` constraints; reopening .NET types (extension methods); untyped params monomorphized; .NET base members + overriding .NET virtuals; `async def`/`await`; `@[SoA]` + `SoA(T)` store.
- Safety: data races in all concurrent contexts are compile errors (AL0202, ADR 0008); `Mutex(T)`, `Atomic`, async `Channel`, async-mailbox `Actor`.
- Performance: auto-vectorization (`Vector<T>`), auto-parallelization of pure pipelines and counted loops (interprocedural purity), idiom rewrites (span Split, alternate lookup, single-probe increment), sealed leaf classes, `SkipLocalsInit`, server+concurrent GC default. Parallel float sums reassociate.
- Tooling: `alice new|build|run|check|test|fmt|add|clean|update|emit|version`; rustc-style diagnostics with AL codes and hints; en/pt/es (`ALICE_LANG`, `--lang`); MSBuild SDK (`src/Alice.Sdk`, `.alproj`); interop and MonoGame samples.
- Docs: `docs/errors.md`, `docs/performance.md`, `docs/tooling.md`.
- Modules: `require "./path"` (transitive, single-file CLI too), `include`/`extend` mixins, `def self.x` module functions, nested modules, `using <Module>` (using static).
- Syntax: heredocs (`<<~`, `<<-`), `%w[]`/`%i[]`, regex literals + `=~`/`!~`/`match?`, `&:sym`, `lambda {}`/`proc {}`, endless `def`, `attr_accessor/reader/writer`, inferred `@x` fields, `alias`, `retry`, `String#%`/`*`, chainable `<<`, array `+ - & |`, `Hash(K, V)`, exhaustive `case` over plain enums (AL0201).
- Stdlib: Ruby/Crystal String, Enumerable, Hash, number, Range, file/time/env and JSON helpers in `Prelude`.
- Tooling: `alice.toml` manifest (NuGet, path and workspace deps, `[build] native`); generated project hidden in `.alice/<name>.alproj`; cargo-style workspaces (globs, dependency-ordered builds, `alice run <member>`); `alice.lock` + `alice update`; `alice new --workspace` with auto member registration; `alice add --path`; `alice test` builds the package and uses its references.
- Removed: `alice sln` (C# references `<pkg>/.alice/<pkg>.alproj`; Alice references C# via path deps).
- Performance: SoA pass fusion + loop interchange (registers, fused `sum`); SIMT vectorization of loops with a data-dependent `while`; load-balanced heavy parallel loops; parallel word-count map-reduce with order-preserving merge. Benchmarks run 5: within ~10% of hand-optimized C#.
- Samples: `samples/` is a workspace; MonoGame `Breakout`.
- `@[Native]` functions: C via clang -O3 -march=native, function-pointer calls, automatic managed fallback (opt-in); small-data specialization (per-length workers on stack copies) — nbody at C speed.
- Floating-point contraction (FMA) by default, `@[StrictMath]` to opt out.
- C# compatibility suite (`CSharpCompatTests`, 17 programs): events, delegates, nullable `||`, tuples, flags, out/ref, `using r = ... end`, constrained generics (`where T : I` members), spans, LINQ, async WhenAll.
- Ruby reflection: `instance_variable_get/set`, `instance_variables`, `methods`, `public_send`.
- CLR events accept Alice blocks/Func values (`obj.Event += block`, `-=` unsubscribes the same handler).
- Ruby DSLs: implicit `yield` blocks, `instance_eval`/`instance_exec`, executable class bodies (macros), `@@class_vars`, runtime `define_method`, `send` → `method_missing` fallback (`RubyDslTests`).
- `for await x in stream` (IAsyncEnumerable); explicit generic method arguments on member calls (`Channel.CreateUnbounded<Int32>()`).
- `alice test`: RSpec-style specs, matchers, hooks, focus/skip/pending, xUnit `@[Test]`/`@[Cases]`, `file:LINE`/`-e`/`--seed`/`--fail-fast`/`--profile`, doc/progress/JUnit output; app top-level code is not run in test builds.
- Block bodies carry `#line` (stack traces and caller lines point at Alice source); block values passed to `Action` parameters are discarded; Prelude zero-arg functions callable without parens; `include` is no longer reserved outside type bodies.
- Block iteration over generic collections of Alice types (`List(Entity)`) now lowers to loops instead of closure calls; `benchmarks/alice/celeste_update.al` (headless Celeste update benchmark).
- CLR generics over Alice types (`List(Entity)`, `Dictionary(String, Entity)`) are fully typed via stand-in proxy types: members, indexers and element types resolve (was untyped, e.g. `Count()` via LINQ). Celeste update benchmark 3.5 → ~1.9 ms.
- Native calls: leaf `@[Native]` functions skip the GC transition; `@[Native(fast)]`, `lib` `@[Fast]` opt in; `Bool` marshals as 1-byte C bool.
- `each` over a `List` iterates its backing span when the body provably can't modify the list. `benchmarks/run.sh`: interleaved rounds (ROUNDS, default 3; pass names to run a subset), median + spread, ratio vs hand-optimized C#.
- `benchmarks/run.sh`: ~37 s for a full run (was 10+ min): incremental builds, ReadyToRun compiler (compile 1.8 s → 0.8 s per file), parallel compilation, 7 trials with 0.3 s warm-up.
- CI: the full test suite runs on ARM64 macOS; the `osx-x64` release package is built and smoke-tested on an Intel runner. Release packages are ReadyToRun (faster compiler startup).
- CLI: every usage mistake is `error:` + `hint:` (exit 2) or a missing file/dir (exit 1) with "did you mean"; no stack traces (exit 70 + `--verbose` for internal errors); no more `.alproj` in messages.
- Packages: `alice remove`, `list`, `search`, `install` (`--tool`), `cache`; restores share the standard .NET NuGet cache.
- Errors from the generated C# are translated (AL0300–AL0315) with Alice type names; no `CS####` or `global::` in messages.
- New tagline: "curiouser and faster: write it like Ruby, run it like C, on all of .NET".
- REPL (`alice` / `alice repl`): Julia-style help/shell/package modes, multi-line input, history, Tab completion, `ans`, :reset/:load/:save/:emit/:time; piped mode for scripts.
- Brand: white-rabbit mark (question-mark ear, diamond + spiral eyes), app icon, logo/wordmark (light/dark), mono mark, Ace-of-Rabbits card, social preview, PNG/ICO exports (`assets/brand`); `alice.exe` carries the icon.
- Fix: auto-parallel loop helpers in instance methods can read `@ivars` (were emitted static → C# error).
- Constants holding array literals infer their type (`ROWS = ["a", "b"]`); `String#each_char_with_index`.
- Sample `samples/life`: Game of Life (terminal animation, PPM frame export → `docs/media/life.gif`), specs.
- Licensing: source code under Apache-2.0 (`LICENSE`, `NOTICE`); brand assets © Julia Klee, all rights reserved (`assets/brand/LICENSE.md`).
- Fix: diagnostics and stack traces inside lowered blocks map to the right Alice line (synthesized statements re-anchor `#line`).
- `to_i`/`to_f` for UInt8/Int8/Int16/UInt16/UInt32/UInt64.
- Sample `samples/raytracer`: parallel ray tracer (shadows, mirrors, supersampling), PNG and orbit GIF in `docs/media/`.
- Fix: array patterns accept literal elements (`in ["go", dir]`, `in [1, _, x]`); literals were wrongly treated as bindings.
- Fix: `instance_eval` as the only statement of a block.
- Sample `samples/wonderland`: text adventure written as a Ruby-style DSL (yield builders, instance_eval, case/in).
- Fix: overloads of .NET methods are chosen by argument types (`Math.Floor(Float64)` no longer picks `Decimal`).
- Branch locals (also destructuring) are visible after `if`/`case` inside blocks and loops, like Ruby.
- Project builds type-check in `alice gen`: errors are reported in Alice terms, never raw `csc` output; MSB3073 noise removed.
- Sample `samples/fractal`: parallel Mandelbrot zoom; README rewritten with GIFs, architecture, capabilities, roadmap.
- Fix: `@[Native]` builds on Windows (no `-fPIC`/`-lm` for the MSVC target).
