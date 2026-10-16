# .NET 10 performance catalog for Alice codegen

Scope: techniques the Alice backend (MIR -> C#) can apply automatically. Target: .NET 10, CoreCLR RyuJIT + NativeAOT.
Order: (expected gain x applicability to automatic codegen), highest first.
Confidence: `[H]` well documented, `[M]` documented but version/shape details vary, `[U]` uncertain - verify with BenchmarkDotNet + `DOTNET_JitDisasm` before relying on it.

Primary evidence:

| Ref | Link |
|---|---|
| T8 | https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-8/ |
| T9 | https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/ |
| T10 | https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/ |
| JITDOC | https://github.com/dotnet/runtime/tree/main/docs/design/coreclr/jit |
| OSA | https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md |
| PGO | https://github.com/dotnet/runtime/blob/main/docs/design/features/DynamicPgo.md |
| CFG | https://learn.microsoft.com/dotnet/core/runtime-config/ |
| AOT | https://learn.microsoft.com/dotnet/core/deploying/native-aot/optimizing |
| VEC | https://learn.microsoft.com/dotnet/standard/simd and https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/vectorization-guidelines.md |
| EGOR | https://egorbo.com/ (JIT notes, bounds checks, PGO) |
| ANDY | Andy Ayers' JIT design notes in dotnet/runtime issues/PRs (e.g. loop opts, PGO, escape analysis) |

Verification loop for every rule below: emit -> `DOTNET_JitDisasm="Method"` / `DOTNET_JitStdOutFile` -> BenchmarkDotNet (`[DisassemblyDiagnoser]`, `[MemoryDiagnoser]`) on x64 and arm64.

---

## Tier 1 - high gain, always/mostly safe, cheap to automate

| # | Technique | Emit (C#) | Safe when | Gain / evidence |
|---|---|---|---|---|
| 1 | Configure runtime for throughput | runtimeconfig / csproj knobs (see section "Runtime knobs") | always; per-profile | TieredPGO (default on since .NET 8) gives 10-30%+ on dispatch-heavy code `[H]` T8, PGO |
| 2 | Canonical bounds-check-free loops | `for (int i = 0; i < span.Length; i++) s += span[i];` - bound is `.Length` of the *same* local array/span | loop var not written in body; array/span in a local (not a field re-read) | removes compare+branch per access, enables further opts; 5-30% tight loops `[H]` T8/T9/T10 |
| 3 | Hoist field arrays to locals | `var a = this._arr; for (...) a[i]` | field may be mutated by others only if Alice semantics allow snapshot (it does if the loop has no aliasing writes) | prerequisite for #2; JIT cannot prove field invariance across calls `[H]` |
| 4 | Explicit range check -> unsigned compare | `if ((uint)i >= (uint)span.Length) Throw();` / `if ((uint)i < (uint)a.Length) use a[i]` | any int index | one branch instead of two; JIT also drops the subsequent implicit check `[H]` EGOR, T8 |
| 5 | Sealed / exact types by default | emit `sealed class` for every class without subclasses (whole-program knowledge); `internal` everything not exported | Alice knows the closed world of a compilation unit | direct calls + inlining; `is`/cast become simple compares `[H]` T8 (sealed), T10 |
| 6 | Monomorphize generics over structs | Alice generics -> C# generics constrained to structs or specialized copies; call interface members via `T : IFoo` constraint (`constrained.` IL) | value-type instantiation | per-instantiation code, no boxing, devirtualized + inlined `[H]` |
| 7 | `[SkipLocalsInit]` at module level | `[module: System.Runtime.CompilerServices.SkipLocalsInit]` + `<AllowUnsafeBlocks>true` | Alice guarantees definite assignment; `stackalloc` buffers never read before write | removes zeroing of large frames/stackalloc; 2-20% on stackalloc-heavy code `[H]` T5/T8 |
| 8 | Static lambdas + cached delegates | `static (x) => ...`; non-capturing -> Roslyn caches automatically; capturing -> lower to struct state + static method instead of closure | no capture, or capture lowered explicitly | zero alloc per call; closures allocate class + delegate `[H]` |
| 9 | Avoid delegates in hot paths: pass struct "function objects" | `interface IOp { int Invoke(int x); }` `struct AddOne : IOp`; `M<TOp>(TOp op) where TOp : struct, IOp` | higher-order functions whose argument is statically known | full inlining (delegates only inline via PGO GDV, see #20) 2-10x on tiny lambdas `[H]` |
| 10 | Struct enumerators / lower `foreach` | `foreach` over `List<T>` uses struct enumerator; for arrays/spans emit `for`. For `List<T>` hot loops use `CollectionsMarshal.AsSpan(list)` | list not mutated during loop (Alice must enforce or check `version`) | removes version checks + enables BCE; ~1.5-3x vs `IEnumerable<T>` foreach `[H]` |
| 11 | Never iterate via interfaces when concrete type known | emit concrete type in locals/params (`List<T>` not `IEnumerable<T>`) | type known statically | interface enumeration = alloc + 2 interface calls/elt. .NET 10 adds array-interface devirt + enumerator stack alloc in some cases `[M]` T10 |
| 12 | `in`/`ref readonly` for large structs; by-value for <= 16 bytes | `void F(in BigStruct s)`; mark structs `readonly struct` | struct > ~16-32 bytes; struct is `readonly` (else defensive copies) | avoids copies; non-readonly struct + `in` causes hidden copies `[H]` |
| 13 | Interpolated strings via handler | `$"..."` targeting `string` (Roslyn uses `DefaultInterpolatedStringHandler`); for appends to builder use `sb.Append($"...")`; types implement `ISpanFormattable` | always | no boxing of args, no intermediate strings; 2-3x vs `string.Format` `[H]` C#10/T6 |
| 14 | `ValueTask` + `PoolingAsyncValueTaskMethodBuilder` | `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))] async ValueTask<T> F()` | callers await exactly once (Alice can enforce) | removes state-machine alloc on async completion; only helps when it actually suspends; mostly-sync methods already zero-alloc with `ValueTask` `[H]` T6 |

## Tier 2 - high gain, needs analysis in the Alice compiler

| # | Technique | Emit | Safe when | Gain / evidence |
|---|---|---|---|---|
| 15 | Manual vectorization of recognized loops | see "Vectorization" | reductions/maps over contiguous primitive spans, no loop-carried deps except reduction | 4-16x on primitives. **RyuJIT does not auto-vectorize loops in .NET 10** `[H]` (no auto-vectorizer in JIT; T10 lists none; vectorization only in BCL + explicit `Vector*`) |
| 16 | Call BCL vectorized primitives | `span.IndexOf`, `SequenceEqual`, `Fill`, `CopyTo`, `Contains`, `Count`, `IndexOfAny(SearchValues<T>)`, `MemoryExtensions.Sum`? (no - use TensorPrimitives) | pattern-match MIR loops to these | very high; BCL already AVX-512 aware `[H]` T8-T10 |
| 17 | `TensorPrimitives` for numeric spans | `TensorPrimitives.Sum/Add/Multiply/Dot/Max/Min/Exp/Log/CosineSimilarity/...` over `ReadOnlySpan<T>` (generic `T` since 9.0) | element-wise / reduction over spans of float/double/ints | 4-16x; **it is a NuGet package (`System.Numerics.Tensors`), not in the shared framework** `[M]` - verify for 10.0; NativeAOT fine |
| 18 | `Unsafe`/`MemoryMarshal` element access after proven range | `ref T r = ref MemoryMarshal.GetArrayDataReference(a); Unsafe.Add(ref r, i)`; spans: `MemoryMarshal.GetReference(span)` | Alice proved index in range (its own range analysis), array non-null | removes residual checks JIT could not prove; 0-20%. Unsound if proof wrong -> GC hole/UB. Use only behind compiler-proved invariants `[H]` |
| 19 | Escape analysis -> explicit stack data | lower non-escaping small aggregates to structs; small fixed arrays to `stackalloc`/`[InlineArray(N)]` struct | object provably does not escape (Alice can prove more than JIT) | eliminates allocation deterministically; do not rely only on JIT OSA `[H]` |
| 20 | Rely on JIT object stack allocation (.NET 9/10) as backup | plain `new` of small class / box / delegate / small array in non-escaping position | JIT: no escape to heap/field/return/call arg (unless callee inlined), not in loop for some cases, size known, no finalizer | .NET 9: boxes stack-allocated. .NET 10: small fixed-size arrays (value-type and ref-type elems), non-escaping delegates/closures, some enumerators after GDV `[M]` T9, T10, OSA |
| 21 | Struct field count <= 4 / prefer flat structs | keep hot structs small (<= 4 primitive fields, no nested structs in hot locals) | design choice | classic promotion limit was 4 fields; .NET 8 "physical promotion" lifts it partially for field-wise access `[M]` T8 (physical promotion) |
| 22 | GDV via PGO for virtual/interface/delegate calls | nothing to emit; ensure TieredPGO on; avoid megamorphic sites (>3 types) | dynamic dispatch remains | delegate GDV exists since .NET 7/8: monomorphic delegate target is guarded + inlined `[H]` T7/T8 PGO. NativeAOT has no dynamic PGO (static PGO via MIBC only `[M]`) |
| 23 | `AggressiveInlining` on small generated helpers | `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for accessor/operator helpers < ~20 IL ops | small, hot, not recursive; no EH (try/catch blocks inlining improved in .NET 10 `[U]`) | bypasses IL-size (default ~16 bytes "always", up to ~100 with profitability) and budget; overuse bloats code and exhausts per-method inline budget -> other inlines rejected `[H]` JITDOC inlining |
| 24 | Throw helpers out of line | `if (bad) ThrowHelper.ThrowX();` with `[DoesNotReturn]`, helper `NoInlining` | always | keeps hot method small -> inlinable; cold block moved `[H]` |
| 25 | Branchless selects | `x = cond ? a : b;` with simple side-effect-free operands; `Math.Min/Max` | operands cheap, no calls/side effects; JIT if-conversion (since .NET 8) only for single-assignment diamonds, not in loops for some cases `[M]` | removes mispredicts on random data: 2-5x on unpredictable branches `[M]` T8 (if-conversion) |
| 26 | `stackalloc` + Span with fallback | `Span<T> buf = n <= 256 ? stackalloc T[256] : (rented = ArrayPool<T>.Shared.Rent(n));` and `finally` return | T unmanaged; constant cap (<= ~1KB); not in loop | no heap alloc; constant-size stackalloc is cheaper than variable `[H]` |
| 27 | `ArrayPool<T>.Shared` for temp buffers > stack cap | Rent/Return in `try/finally` | buffer does not escape; caller does not rely on exact length | removes LOH/Gen0 churn `[H]` |
| 28 | `static readonly` as JIT constants | config values as `static readonly` primitives/`bool` | class initialized before tier1 rejit (true for hot code) | folded at tier1 -> dead branch removal (e.g. feature flags) `[H]` T8. Not folded in R2R/AOT code unless `const` `[M]` |
| 29 | `const` / generic-`static abstract` constants for compile-time known values | emit `const` where Alice knows value | value compile-time known | folds in all modes incl. AOT `[H]` |

## Tier 3 - loop shapes RyuJIT recognizes (emit to match them)

| # | Opt | Recognized shape | Notes / evidence |
|---|---|---|---|
| 30 | BCE on `for` up to `.Length` | `for (int i = 0; i < a.Length; i++)` ; also `i < span.Length`, `i <= a.Length - 1` `[M]`; reverse `for (int i = a.Length - 1; i >= 0; i--)` | span and array equivalent `[H]`. `i < n` where `n` is a separate local typically keeps checks unless loop cloning kicks in |
| 31 | Loop cloning | `for (i = start; i < n; i++) a[i]` with invariant `a`, `n` -> JIT clones fast path with up-front check | only `for`-shaped counted loops with `+1` stride; .NET 8/9 widened cloning (incl. some `Span` and type tests) `[M]` T8/T9. Prefer exact `.Length` bound anyway |
| 32 | Loop inversion | `while` -> `do-while` + guard; JIT does it; .NET 9/10 redesigned loop recognition (new loop representation, "FlowGraphNaturalLoop") `[M]` T9 | emit plain `for`/`while`; avoid `goto`-built loops: irreducible/multi-entry flow defeats all loop opts |
| 33 | IV widening (.NET 9) | `int i` used as index on x64 -> widened to 64-bit, removes `movsxd` per iteration `[H]` T9 | keep loop index `int`, no need to emit `nint` manually |
| 34 | Strength reduction (.NET 9/10) | `a[i]` in loop -> pointer bump; post-indexed addressing on arm64; downward counting loops `[M]` T9, T10 | emit simple stride-1 indexing; do not hand-roll pointer arithmetic (blocks other opts) |
| 35 | Loop alignment, hoisting (LICM) | invariant expressions hoisted only if side-effect-free and loop is well-formed | emit invariants as locals before loop yourself: deterministic |
| 36 | Do not put try/catch inside hot loops | EH regions block enregistration/opts (improved in .NET 9/10 `[U]`) | move EH outside loop |

## Vectorization pattern (emit for recognized MIR loops)

```csharp
static int Sum(ReadOnlySpan<int> s) {
    ref int p = ref MemoryMarshal.GetReference(s);
    int i = 0, n = s.Length, acc = 0;
    if (Vector256.IsHardwareAccelerated && n >= Vector256<int>.Count) {
        var v = Vector256<int>.Zero;
        int last = n - Vector256<int>.Count;
        for (; i <= last; i += Vector256<int>.Count)
            v += Vector256.LoadUnsafe(ref p, (nuint)i);
        acc = Vector256.Sum(v);               // horizontal sum
    } else if (Vector128.IsHardwareAccelerated && n >= Vector128<int>.Count) { /* same with 128 */ }
    for (; i < n; i++) acc += Unsafe.Add(ref p, i); // scalar tail
    return acc;
}
```

| Rule | Detail |
|---|---|
| ISA check | `Vector128/256/512.IsHardwareAccelerated` are JIT-time constants -> dead branches removed. Vector512 only accelerated on AVX-512 hardware; JIT may prefer 256 even then on some CPUs (throttling heuristic) `[M]` T8 |
| Width choice | Emit 128 + 256 paths; 512 optional. `Vector<T>` is variable-size (= 256 on AVX2, not 512 by default) - fine but less control `[H]` VEC |
| Tail | (a) scalar loop, or (b) for idempotent maps: one final overlapping vector at `n - Count` `[H]` VEC guidelines |
| Masks | `Vector256.ConditionalSelect(mask, a, b)`, `Vector256.GreaterThan(...)`, `ExtractMostSignificantBits` for find-first; .NET 9/10 lowers to AVX-512 mask regs when available `[M]` |
| Reductions | multiple accumulators (2-4) to break dependency chains; float sums change result order -> only when Alice semantics permit reassociation (or opt-in flag) |
| Alignment | unaligned loads are fine on modern x64; aligning large inputs (>~ 1 KB) to 32/64 B gives small gains `[M]` |
| AVX10.2 / AVX-512 | .NET 10 adds AVX10.2 APIs (`Avx10v2`), possibly behind preview flag `[U]` T10. Prefer cross-platform `Vector*` APIs over ISA intrinsics |
| Floats | do not vectorize FP reductions without reassociation permission; maps are safe |

## Runtime knobs

`runtimeconfig.json` = can be emitted by Alice via `runtimeconfig.template.json` or `<ItemGroup><RuntimeHostConfigurationOption>` / msbuild properties. Ref: CFG.

| Knob | Where | Recommendation | Notes |
|---|---|---|---|
| `System.Runtime.TieredPGO` | runtimeconfig / `<TieredPGO>` | `true` (default since .NET 8) | biggest JIT win for dispatch `[H]` |
| `System.Runtime.TieredCompilation` | runtimeconfig / `<TieredCompilation>` | keep `true`; `false` = full opt at first JIT, no PGO, slower startup; good only for short-lived benchmarks | `[H]` |
| `System.Runtime.TieredCompilation.QuickJit` | runtimeconfig | default `true` | |
| `System.Runtime.TieredCompilation.QuickJitForLoops` | runtimeconfig / `<TieredCompilationQuickJitForLoops>` | default `true` with OSR (since .NET 7) - leave on | OSR moves hot loops to optimized code mid-method `[H]` |
| OSR (`DOTNET_TC_OnStackReplacement`) | env only | leave default | env vars `DOTNET_TC_*` (CallCountThreshold, BackgroundWorkerTimeoutMs, ...) are env/diagnostic, not runtimeconfig `[M]` |
| `DOTNET_TC_CallCountThreshold`, `DOTNET_TieredCompilation_BackgroundWorkerTimeoutMs` | env | only for tuning experiments | `[M]` |
| ReadyToRun | csproj `<PublishReadyToRun>true` | yes for startup; R2R code is tier0-quality-ish then rejitted with PGO | build-time, not runtimeconfig `[H]` |
| R2R Composite | `<PublishReadyToRunComposite>` (+ self-contained) | startup of large apps; minor steady-state effect | `[M]` |
| NativeAOT | `<PublishAot>true`, `<OptimizationPreference>Speed</OptimizationPreference>`, `<IlcInstructionSet>native</IlcInstructionSet>` or `x86-x64-v3` | Speed + explicit ISA; without ISA, AOT targets baseline x64 (SSE4.2-ish? `[U]`) and `Vector256.IsHardwareAccelerated` becomes runtime check | no dynamic PGO -> virtual-heavy code may be slower than JIT steady state `[H]` AOT |
| Server GC | `System.GC.Server` / `<ServerGarbageCollector>` | throughput services: true; .NET 9+ enables DATAS by default with Server GC (`System.GC.DynamicAdaptationMode`) | `[H]` |
| Concurrent GC | `System.GC.Concurrent` | `false` for batch/throughput (fewer background threads) | `[M]` |
| `GCHeapAffinitizeMask` / `GCNoAffinitize` / `GCHeapCount` | runtimeconfig `System.GC.*` | only for multi-tenant boxes | `[H]` CFG |
| `System.GC.RetainVM`, `ConserveMemory` | runtimeconfig | workload-specific | |
| `InvariantGlobalization` | `<InvariantGlobalization>true` -> `System.Globalization.Invariant` | yes unless Alice exposes culture APIs; faster string compare/ToUpper, no ICU load | `[H]` |
| `UseSystemResourceKeys` | `<UseSystemResourceKeys>true` | AOT size only | |
| `TieredCompilation` + `[MethodImpl(AggressiveOptimization)]` | attribute | **avoid**: skips tiering and PGO -> often slower `[H]` T8 | |

## Allocation avoidance (codegen rules)

| Rule | Emit |
|---|---|
| Formatting | `ISpanFormattable.TryFormat(Span<char>, out int, ...)` on Alice value types; `IUtf8SpanFormattable` for UTF-8 output |
| String building | interpolated handler; `string.Create(len, state, static (span, s) => ...)` when length known |
| Boxing | generic constrained calls (`T : IEquatable<T>`); never call `object.Equals/GetHashCode/ToString` on struct without override; `EqualityComparer<T>.Default.Equals` is devirtualized `[H]` |
| Enum ops | `HasFlag` is fine (JIT intrinsic); avoid `Enum.ToString` in hot paths |
| Collections | pre-size `List<T>(capacity)`, `Dictionary(capacity)`; `CollectionsMarshal.GetValueRefOrAddDefault` for update-in-place (one lookup) |
| Frozen data | `FrozenDictionary/FrozenSet` for read-mostly lookup tables `[H]` T8 |
| Literals | `ReadOnlySpan<byte> x => [1,2,3];` / `"abc"u8` -> data in PE, zero alloc |
| Collection expressions | `[a, b, c]` to `Span<T>`/`ReadOnlySpan<T>` -> stack/inline array (C# 12/13) |
| Inline arrays | `[InlineArray(N)] struct Buf { T e; }` for fixed small buffers of managed types |
| Params | C# 13 `params ReadOnlySpan<T>` instead of `params T[]` |

## Concurrency

| Topic | Fastest choice | Notes |
|---|---|---|
| Actor mailbox | `Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false })` | SingleReader enables specialized lock-free reader impl `[H]`. Bounded channels lock more -> slower, use only for backpressure `[M]` |
| SPSC hot queue | custom ring buffer with padded head/tail (`[StructLayout(Size=128)]` padding) | 2-5x vs Channel for tight SPSC `[M]` |
| Reader loop | `while (await r.WaitToReadAsync()) while (r.TryRead(out var m)) Handle(m);` | avoids per-item async overhead |
| False sharing | pad per-thread counters to 64 B (128 B on some arm64/Intel adjacent-line prefetch) | `[H]` |
| Parallel.For | for cheap bodies use range partitioning: `Parallel.ForEach(Partitioner.Create(0, n, chunk), r => for(...))` | avoids per-iteration delegate call `[H]` |
| Long-running blocking work | `new Thread` or `TaskCreationOptions.LongRunning`; never block ThreadPool threads | starvation `[H]` |
| Short work | ThreadPool (`ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, preferLocal: true)`) | lowest-overhead queueing |
| Locks | .NET 9 `System.Threading.Lock` (`lock` on it uses the new type) | slightly faster than Monitor `[M]` T9 |
| Counters | `Interlocked` on padded fields; per-core sharding for hot counters | |

## .NET 10 JIT highlights (T10) - verify each

| Item | Status |
|---|---|
| Stack allocation of small fixed-size arrays (value and ref element types) | `[M]` |
| Stack allocation of non-escaping delegates / closures | `[M]` |
| Escape analysis through some struct fields, enumerator stack alloc after GDV / array-interface devirtualization (`IEnumerable<T>` over `T[]`) | `[M]` |
| Improved loop inversion / loop opts on the new flowgraph loop representation | `[M]` |
| Better block layout (3-opt-based reordering using PGO) | `[M]` |
| Inlining of methods with try/finally / EH | `[U]` |
| AVX10.2 API, AVX-512 by default where hardware supports | `[U]` (AVX10.2 may need preview flag) |
| Arm64 SVE APIs (experimental) | `[U]` |

## Do not do

- `AggressiveOptimization` (disables PGO).
- `unsafe` pointers/`fixed` in loops instead of `ref` + `Unsafe.Add` (pins, blocks opts).
- Hand-built `goto` loops (irreducible flow kills loop opts).
- Huge methods: JIT throttles opts (e.g. many locals > ~512 tracked limit `[M]`); split cold paths.
- Struct copies of >64 B by value in hot paths.
- Exceptions for control flow.
