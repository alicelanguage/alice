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
| `@[SoA]` store `each/sum/count` | per-column loop, no struct gather; vectorized (`StoreUnsafe`) + `Par.Range` | `Lowerer.SoA`, `Backend/SoAStore` | `SoATests.Column_loops_touch_columns_and_vectorize`, `ParallelizeTests.Soa_each_update_runs_in_parallel_and_is_correct` |
| `for w in s.Split(' ')` + `Dictionary(String, _)` | `AsSpan().Split(...)` + `GetAlternateLookup<ReadOnlySpan<char>>` | `Lowerer.Idioms` | `PatternOptTests.Word_count_uses_spans_and_one_probe` |
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

See [benchmarks/README.md](../benchmarks/README.md), "Run 3": Alice/C# idiomatic ratio 0.07 (pipeline), 0.16 (particles), 0.18 (mandelbrot), 0.22 (wordfreq), 0.57 (binarytrees); nbody/parallel tie.
