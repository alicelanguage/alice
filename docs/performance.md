# Performance model

Alice rewrites common source patterns into the IL shape a hand-optimizing C# author would write (ADR 0007). Inspect with `alice emit file.al`. Code: `src/Alice.Compiler/Lowering/`. Tests: `tests/Alice.Tests/Compiler/`.

## Pipeline

| Pattern | Emitted shape | Lowering | Evidence test |
|---|---|---|---|
| literal block on `each/map/select/reject/sum/count/any?/all?/find/reduce/times/upto/downto/each_with_index` | inlined `for` loop, no delegate | `Lowerer.Iterators` | `AllocationTests.Inlined_chains_emit_no_lambdas` |
| `map/select/reject` chain + sink | one fused loop, 0 bytes allocated | `Lowerer.Iterators` | `AllocationTests.Iterator_kernels_allocate_nothing` |
| `map` over array | exact-size result array | `Lowerer.Iterators` | `AllocationTests.Map_over_array_preallocates_exact_size` |
| numeric array pipeline (`+ - *`, compare, captured scalars) into `sum/count/max/min/map` | `Vector<T>` loop, `ConditionalSelect` for filters, scalar tail | `Lowerer.Vectorize` | `VectorizeTests.Numeric_pipelines_emit_vector_code` |
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
| `.alproj` builds | Server + concurrent GC by default (overridable) | `src/Alice.Sdk/Alice.props` | - |

## Semantics notes

- Parallelization requires purity; the race checker (AL0202) and purity gate share the same model.
- Parallel floating-point sums reassociate: results may differ from serial in the last bits. Integer results are exact.
- Allocation tests must stay at 0 bytes.

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
