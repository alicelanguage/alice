# Performance model

Alice rewrites common source patterns into the IL shape a hand-optimizing C# author would write (ADR 0007). Memory and GC: [heap and GC](heap-gc.md). Parallel recursion: [fork/join](fork-join.md). Inspect with `alice emit file.al`. Code: `src/Alice.Compiler/Lowering/`. Tests: `tests/Alice.Tests/Compiler/`.

## Pipeline

| Pattern | Emitted shape | Lowering | Evidence test |
|---|---|---|---|
| literal block on `each/map/select/reject/sum/count/any?/all?/find/reduce/times/upto/downto/each_with_index` | inlined `for` loop, no delegate | `Lowerer.Iterators` | `AllocationTests.Inlined_chains_emit_no_lambdas` |
| `map/select/reject` chain + sink | one fused loop, 0 bytes allocated | `Lowerer.Iterators` | `AllocationTests.Iterator_kernels_allocate_nothing` |
| `map` over array | exact-size result array | `Lowerer.Iterators` | `AllocationTests.Map_over_array_preallocates_exact_size` |
| numeric array pipeline (`+ - *`, compare, captured scalars) into `sum/count/max/min/map` | explicit `Vector512`/`Vector256`/`Vector128` tiers (widest accelerated one that fits; folds at JIT/AOT, WASM keeps 128), `ConditionalSelect` for filters; `max/min/map` end with an overlapping final vector, `sum/count` with a scalar tail | `Lowerer.Vectorize` | `VectorizeTests.Numeric_pipelines_emit_vector_code` |
| block with a non-vectorizable call (`Math.Sin`) | scalar loop | `Lowerer.Vectorize` | `VectorizeTests.Non_vectorizable_blocks_stay_scalar` |
| pure pipeline over array, `n >= 65536` and >1 worker | chunked `Par.Reduce` / `Par.Range`; serial below threshold | `Lowerer.Parallel` | `ParallelizeTests.Large_pure_pipelines_emit_a_parallel_path` |
| impure block (mutates captured local, I/O) | serial | `Lowerer.Parallel` | `ParallelizeTests.Impure_blocks_stay_serial` |
| counted `for` with one `+=` reduction | `Par.Reduce` | `Lowerer.LoopPar` | `LoopParallelizeTests.Reduction_loop_is_parallel_and_exact` |
| counted `for` writing `arr[i]` | `Par.Range` | `Lowerer.LoopPar` | `LoopParallelizeTests.Disjoint_array_writes_parallelize` |
| loop body calls user methods proven pure (interprocedural) | parallel (nested-loop body: threshold 2) | `Lowerer.LoopPar` | `LoopParallelizeTests.Loops_calling_pure_user_methods_parallelize` |
| impure call, `break`, list append, last-write, self-dependent `+=` | serial | `Lowerer.LoopPar` | `Loops_calling_impure_methods_stay_serial`, `Order_dependent_loops_stay_serial` |
| loop body calls user methods / nested loops (heavy, uneven iterations) | grain 1: many small chunks (`Workers * 64`), scheduler load-balances | `Lowerer.LoopPar`, `Par.Range`/`Par.Reduce` | `LoopParallelizeTests.Loops_calling_pure_user_methods_parallelize` |
| counted `for` whose body has a data-dependent `while` (escape loop) | SIMT: `Vector<double>.Count` iterations at once, per-lane locals, masked updates (`ConditionalSelect`); bit-identical to scalar | `Lowerer.Simt` | `SimtTests.Inner_while_runs_on_vector_lanes`, `Lanes_reproduce_scalar_results_exactly` |
| SIMT candidate with integer division or `Float32` | scalar loop | `Lowerer.Simt` | `SimtTests.Integer_division_and_float32_stay_scalar` |
| `@[SoA]` store `each/sum/count` | per-column loop, no struct gather; vectorized (`StoreUnsafe`) + `Par.Range` | `Lowerer.SoA`, `Backend/SoAStore` | `SoATests.Column_loops_touch_columns_and_vectorize`, `ParallelizeTests.Soa_each_update_runs_in_parallel_and_is_correct` |
| consecutive row-local `ps.each` passes (optionally inside `for step`) + following `ps.sum` | pass fusion + loop interchange: one sweep, columns loaded once into registers, all passes/steps on registers, stored once, sum fused; parallel when large | `Lowerer.SoAFusion` | `SoAFusionTests.Passes_are_fused_into_one_sweep_with_registers`, `Fused_interchanged_loop_matches_pass_by_pass_semantics` |
| SoA pass reads another row (`qs[0].a`) | not fused | `Lowerer.SoAFusion` | `SoAFusionTests.Cross_row_reads_prevent_fusion` |
| `for w in s.Split(' ')` + `Dictionary(String, _)` | `AsSpan().Split(...)` + `GetAlternateLookup<ReadOnlySpan<char>>` | `Lowerer.Idioms` | `PatternOptTests.Word_count_uses_spans_and_one_probe` |
| word-count loop (single counter update), text length >= threshold, >1 worker | parallel map-reduce: text cut at separators, one span-lookup dictionary per chunk, partials merged in chunk order (same iteration order as serial) | `Lowerer.Idioms` (`ParallelWordCount`) | serial path: `PatternOptTests`; parallel path: benchmark `wordfreq` only |
| `d[k] = d.GetValueOrDefault(k) + 1`, `d[k] += x` | `CollectionsMarshal.GetValueRefOrAddDefault` (one probe); missing key with `+=` still throws | `Lowerer.Idioms` | `PatternOptTests.Dictionary_increment_single_probe`, `Missing_key_compound_assignment_still_throws` |
| untyped params | one typed specialization per call-site type | Semantics | `LanguageTests.Duck_typing_emits_typed_specializations_not_dynamic` |
| class without Alice subclass | `sealed`; module `[SkipLocalsInit]` | Backend | `LanguageTests.Leaf_classes_are_sealed_and_locals_skip_init` |
| capture-free lambda | `static` lambda | Backend | `AllocationTests.Capture_free_lambdas_are_static` |
| `.alproj` builds | workstation + concurrent GC by default; `[runtime] gc = "server"` / `"low-memory"` | `src/Alice.Sdk/Alice.props`, `Projects/Manifest` | `ManifestTests.Runtime_gc_mode_maps_to_msbuild` |
| loop nested in a serial loop | auto-parallel only for long trip counts (never the threshold-2 "heavy" rule); constant short trip counts: no parallel path | `Lowerer.LoopPar` | `LoopParallelizeTests.Tiny_inner_stencil_loops_stay_serial` |
| `x / 2^k`, `x % 2^k` (floored) | `x >> k`, `x & (2^k - 1)` | `Optimizer/Passes/StrengthReduction` | `OptimizerTests.Power_of_two_division_compiles_to_a_shift` |
| integer literal arithmetic | folded (overflow left for Roslyn to report) | `Optimizer/Passes/ConstantFolding` | `OptimizerTests.Constant_folding` |
| `[a, b, c].min` / `.max` | `Math.Min(Math.Min(a, b), c)`, no array | `Optimizer/Passes/LiteralMinMax` | `AllocationTests` "literal array min" |
| typed `for (T i...; i < end; ...)`, body never writes `i` | unchecked step (no `add.ovf`); all other integer ops checked | `Optimizer/Passes/InductionVariables` | `OptimizerTests.Induction_step_is_unchecked_only_when_provable` |
| small non-escaping local array (`Array(T).new(3, v)`, `[x, y]`) | one `stackalloc` buffer at method entry, re-initialized at the declaration | `Lowerer.StackArrays` | `AllocationTests.Non_escaping_small_arrays_live_in_one_stack_buffer`, `Escaping_or_unbounded_arrays_stay_on_the_heap` |
| counter offsets `i + k`, `i - k` inside a proven range (`.Length` bound <= Array.MaxLength) | unchecked | `Optimizer/Passes/InductionVariables` | `OptimizerTests.Counter_offsets_are_unchecked_only_inside_their_proven_range` |
| array of fixed-width literal rows filled by a full loop, rows never escape | one flat `T[n * k]`, `a[i * k + j]`, column checked | `Lowerer.FlatRows` | `FlatRowsTests` |
| `a, b = f(x), g(y)` (pure, independent, recursive) | `Par.Fork` like the two-declaration form | `Lowerer.Fork` | `LoopParallelizeTests.Parallel_multi_assign_forks_like_bends_parallel_call` |
| integer `+ - *` with operand ranges proven in-type (masks, shifts, widened Int32, literals) | unchecked (no `add.ovf`/`mul.ovf`, no throw path) | `Lowerer.Ranges` | `RangeTests.Masked_hash_arithmetic_is_unchecked`, `Possibly_overflowing_arithmetic_stays_checked` |
| `x >= 2^31 ? x - 2^32 : x` with x proven in [0, 2^32) (and 8/16-bit forms) | `(long)(int)x`: one sign extension, no branch | `Lowerer.Ranges.TrySignExtend` | `RangeTests.Proven_sign_reinterpretation_is_one_sign_extension` |
| `xs.sum` / `min` / `max` on a large primitive array | fork/join over SIMD chunks | `Runtime/Parallel/ParReduce` | `ParReduceTests` |
| `2.0 ** n` (integer `n`), `x ** 2` (float `x`) | `Math.ScaleB(1.0, n)`, `x * x`: bit-identical to `Math.Pow` | `Lowerer.Binary` | `PrimitiveTests.Exact_power_forms_match_math_pow` |
| `x.to_i64` on Int32, any number `.to_f` (widening) | plain cast; narrowing keeps the checked, inlined helper | `Lowerer.Calls.WideningCast` | `PrimitiveTests.Widening_conversions_are_plain_casts` |
| `xs.sort` on a large primitive array | fork/join merge sort, pooled buffer | `Runtime/Parallel/ParSort` | `ParSortTests` |
| `a = f(x)` / `b = f(y)`: pure, independent, recursive | `Par.Fork` in a helper when `Par.Forkable` (top log2(cores)+3 levels), plain calls below | `Lowerer.Fork`, `Par.Fork` | `LoopParallelizeTests.Independent_pure_recursive_calls_fork_with_lazy_task_creation`, `Forks_need_purity_independence_and_recursion` |
| writes into arrays allocated in the method | still pure (fresh locals cannot alias caller data) | `Lowerer.LoopPar.FreshArrays` | `LoopParallelizeTests.Writes_into_fresh_local_arrays_keep_a_method_pure`, `Writes_through_shared_arrays_stay_impure` |
| `q, r = a.divmod(b)` | two floored helpers on temps, no array | `Lowerer.Statements` | `AllocationTests` "divmod destructure" |
| `a.step(limit, k)` with literal `k` | inlined `for` (`+= k` / `-= k`) | `Lowerer.Iterators` | `AllocationTests` "step" |

## Semantics notes

- Parallelization requires purity; the race checker (AL0202) and purity gate share the same model.
- Parallel floating-point sums reassociate: results may differ from serial in the last bits. Integer results are exact.
- Allocation tests must stay at 0 bytes.
- Integer overflow is checked (ADR 0013); passes remove a check only where it is provably dead (ADR 0014).
- `ALICE_DISABLE_PASSES=const-fold,strength-reduce,literal-minmax,induction` disables MIR passes for A/B runs.

## Results

[benchmarks/README.md](../benchmarks/README.md), "Run 5" (vs idiomatic and expert hand-optimized C#):

| benchmark | Alice / hand-opt | Alice / idiomatic | main technique |
|---|---:|---:|---|
| particles | 1.00 | 0.02 | SoA fusion + interchange |
| pipeline | 1.05 | 0.07 | fused + vector + parallel |
| wordfreq | 0.81 | 0.06 | parallel span map-reduce |
| mandelbrot | 1.06 | 0.09 | parallel rows + SIMT |
| binarytrees | 0.98 | 0.56 | parallel pure calls, server GC |
| parallel | 1.02 | 0.88 | - |
| expr | 1.10 | 0.98 | sum-type dispatch |
| nbody | 1.06 | 0.90 | sequential, same IL |

Alice beats idiomatic C# everywhere and is within ~10% of hand-written C# (noise ±10-20%).

## Known limit: nbody vs C

Measured on the same machine (median ms, identical checksums):

| variant | ms |
|---|---:|
| clang -O3 -march=native (C, serial) | 29.9 |
| Alice (JIT, before FP contraction) | 47.2 |
| Alice (JIT, with FP contraction / FMA) | 43.6 |
| C, clang with `-ffp-contract=off` (no FMA) | 41.5 |
| C, no FMA, no vectorization | 45.0 |
| Alice NativeAOT (`alice build --native`) | 45.7 |
| expert C# (SoA spans, unrolled pairs) | ~50 |
| C#: x/y/z in `Vector256<double>` (SLP by hand) | 43.9 |
| C#: `Vector256` + FMA | 42.4 |
| C#: fully unrolled, all fields in scalar locals | 65.6 (register spills) |

The step is a serial chain of div/sqrt over 10 pairs; clang interleaves the pairs with better register allocation.
RyuJIT cannot (unrolling makes it spill), so no Alice lowering produces C speed here. Every other benchmark beats C (benchmarks/README.md run 6).

### Floating-point contraction (FMA)

Like clang/gcc's default `-ffp-contract=on`, Alice fuses `a*b + c`, `c + a*b`, `a*b - c`, `c - a*b` and `x += a*b` /
`x -= a*b` on `Float64`/`Float32` into one fused multiply-add (`Prelude.fma`, hardware FMA3/ARM64; plain math
otherwise). Results are the single-rounded value, as in C. `@[StrictMath]` on a method keeps separate rounding.
Test: `FmaTests`. Measured: C's own nbody is 30 ms with FMA and 41.5 ms without; Alice went 47.2 -> 43.6 ms.

### `@[Native]` functions (opt-in)

`@[Native] def f(xs : Array(Float64), n : Int32) : Float64` compiles the function to C (`Backend/CEmitter.cs`) and,
when clang/cc/gcc exists, to `lib<asm>_native.so` (`-O3 -march=native`); the C# side calls it through a function
pointer and falls back to the managed body when the library is missing. Subset: numbers, Bool, `Array(number)`
params (pointer + length), locals, if/while/for, `Math.*`, calls between native functions. Tests: `NativeTests`.

nbody (`benchmarks/alice/nbody_native.al`, separate field arrays): 42.5 ms native vs 43.6 ms JIT vs 30 ms C.
Measured why the C benchmark is faster: its 5 bodies are a stack-local array of structs (no aliasing, x/y/z adjacent
for SLP). Experiments on the generated C: `restrict` 49 ms (worse), length versioning 44 ms, stack copies +
versioning 39 ms. Native pays off for loops whose data layout clang can exploit; it is not a blanket speedup.

**Small-data specialization (now automatic in `CEmitter`):** each native function with array parameters gets a
worker compiled per small length (0..64) of its first array, called on stack copies of the arrays (written ones are
copied back); larger inputs use the generic worker; the C# wrapper uses the managed body if the same array is passed
twice (so copies never change semantics). With bodies packed as one array (x, y, z, vx, vy, vz, mass per body),
`benchmarks/alice/nbody_native.al` runs at **31.8-32.9 ms vs 33.4-34.5 ms for clang C** in the same session
(identical checksum): C speed. Test: `NativeSpecializationTests`.

### Native call overhead

Measured (i5-1235U, trivial C `int add1(int)`): DllImport 2.36 ns/call, function pointer 2.28, either with
`SuppressGCTransition` ~1.9, managed 0.28. What Alice does:

| Case | Emitted |
|---|---|
| `@[Native]` straight-line function (no loops, no native calls) | `delegate* unmanaged[SuppressGCTransition]` (bounded, leaf, no callbacks) |
| `@[Native]` with loops | normal transition (a long call must not stall GC); `@[Native(fast)]` opts in |
| `lib` `fun` with `@[Fast]` | `[SuppressGCTransition]` |
| `Bool` in `lib` `fun` | `MarshalAs(U1)`: 1-byte C `bool`, no 4-byte BOOL conversion |
| array parameters | pinned with `fixed` (no copy), pointer + length |

The biggest lever stays batching: one call over an array (`@[Native] def f(xs : Array(Float64))`) instead of one call per element.
