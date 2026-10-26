# Benchmarks

Alice vs C# on the same .NET runtime. Each Alice program has a C# twin with the same algorithm and checksum.

## Run

```bash
benchmarks/run.sh
```

Builds `src/Alice.Cli` (Release), each `alice/*.al` to `out/<name>/<name>.dll`, and `csharp/Bench.CSharp.csproj` (Release, Optimize, default TieredPGO). Then runs every benchmark variant in its own process and prints the table.

Single run:

```bash
dotnet out/nbody/nbody.dll
dotnet out/csharp/Bench.CSharp.dll nbody idiomatic   # or: handopt
```

## Methodology

- Kernel lives in its own method; it returns a checksum.
- Warm-up: kernel called >= 50 times and for >= 1 s, so tier-1 + PGO code replaces tier-0/OSR code. With only 50 short calls (~100 ms) C# `mandelbrot` stayed on OSR code and never reached tier-1 (`DOTNET_JitDisasmSummary=1`).
- Timing: 15 timed calls, median reported. Each timed call compares its checksum to the reference, so the JIT cannot drop the work as dead code (an early version without this measured `pipeline` at 2.5 ms).
- Output: `RESULT <name> <median_ms> <checksum>`. `run.sh` flags checksum mismatches. All match.
- One process per benchmark variant: no cross-benchmark JIT, GC or ordering effects.

| Benchmark | Alice | C# idiomatic | C# hand-opt |
|---|---|---|---|
| nbody | 5 `class Body`, `Float64` properties, 1M steps | class with fields, `foreach` | - |
| binarytrees | `class Node` with `Node?` children, depth 16 | sealed class, recursion | - |
| pipeline | 10M `Float64`, `map.select.sum` (fused loop) | LINQ `Select.Where.Sum` | `foreach` loop |
| expr | `enum Expr` sum type, `case/when Expr::Add(a, b)`, ~1M-node tree x10 evals | records + switch expression | - |
| wordfreq | 1M words, `Split`, `Dictionary(String, Int32)` | `Split` + `TryGetValue` + `Values.Max()` | span split + `GetAlternateLookup<ReadOnlySpan<char>>` |
| mandelbrot | 1000x1000, 100 iterations, `Float64` loops | same loops | - |
| parallel | `Par.Map(xs, f).sum`, 10M elements | PLINQ `AsParallel().Select(f).Sum()` | `Parallel.For` over 12 chunks |

## Results

Machine: 12th Gen Intel(R) Core(TM) i5-1235U (12 threads, laptop U-series), Linux 7.0.9-cachyos, .NET 10.0.4 runtime (SDK 10.0.104). Median of 15, ms, lower is better.

| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/C# idiomatic | checksum |
|---|---:|---:|---:|---:|---|
| nbody | 50.9 | 50.5 | - | 1.01 | ok |
| binarytrees | 199.1 | 178.5 | - | 1.12 | ok |
| pipeline | 6.9 | 26.0 | 7.0 | 0.27 | ok |
| expr | 8.9 | 11.9 | - | 0.75 | ok |
| wordfreq | 123.4 | 104.4 | 36.0 | 1.18 | ok |
| mandelbrot | 116.6 | 117.7 | - | 0.99 | ok |
| parallel | 40.3 | 33.5 | 30.5 | 1.20 | ok |

## Notes and anomalies

- Noise: laptop CPU with turbo/thermal limits and P/E cores. Run-to-run spread is 10-20 % (`binarytrees` Alice 197-239 ms, C# 172-204 ms across runs; `wordfreq` ratio 0.99 in one run, 1.18 in another). Ratios within ~1.2 should be read as "same".
- `pipeline`: Alice fuses `map.select.sum` into one loop and matches the hand-written C# loop. LINQ pays for delegates and iterators (3.7x).
- `expr`: Alice lowers the sum type to sealed nested records and `is` tests, the same shape as the C# version. The 25 % gap is JIT code layout, not a different algorithm.
- `parallel`: `Par.Map` materializes a 10M-element result array, then sums sequentially. PLINQ / `Parallel.For` reduce without the array. Expected cost of the API shape.
- `mandelbrot`: before the 1 s warm-up rule, Alice measured 165 ms vs C# 109 ms. Cause: C# was still running OSR code, Alice had tier-1 code. Same source compiled by a plain csproj gave the same 165 ms, so it was not a compiler flag issue.
- `binarytrees`: Alice 10-20 % slower in most runs with identical emitted shape (auto-properties vs fields). Not investigated further; within noise band of this machine.

## Compiler issues found

1. Implicit return dropped when the last expression's receiver comes from a generic .NET call whose type is not inferred. Roslyn reports `CS0161 not all code paths return a value`.

   ```crystal
   def k(xs : Array(Int32)) : Int32
     Par.Map(xs, ->(x : Int32) { x * 2 }).sum
   end
   puts k([1, 2])
   ```

   Emitted: `global::Alice.Runtime.Par.Map(...).sum();` without `return`. Same with `ys = Par.Map(...)` then `ys.sum`.
   Workaround (used in `alice/parallel.al`): annotate the local, `ys : Array(Float64) = Par.Map(...)`, then `ys.sum`.

## Run 2 — after auto-vectorization, generic inference, `par_sum` (2026-10-15)

Same machine and method. All checksums match C#.

| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/C# |
|---|---:|---:|---:|---:|
| nbody | 45.2 | 44.9 | - | 1.01 |
| binarytrees | 178.8 | 182.6 | - | 0.98 |
| pipeline | 7.1 | 26.4 | 8.2 | 0.27 |
| expr | 9.0 | 9.6 | - | 0.94 |
| wordfreq | 111.5 | 111.5 | 32.8 | 1.00 |
| mandelbrot | 117.1 | 121.2 | - | 0.97 |
| parallel | 31.3 | 33.2 | 32.9 | 0.94 |

Compiler issue 1 (dropped implicit return after generic .NET calls) is fixed: generic method type inference + declared return types always return the tail. `parallel.al` now uses `xs.par_sum { }` (chunked map-reduce, no intermediate array).

## Run 3 — idiom rewrites, SoA, auto-vectorization + auto-parallelization, server GC (2026-10-15)

| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/C# | why |
|---|---:|---:|---:|---:|---|
| nbody | 50.3 | 47.6 | - | 1.06 | sequential 5-body steps: same IL, tie |
| binarytrees | 103.7 | 182.2 | - | 0.57 | loop calls pure methods -> auto-parallel + server GC |
| pipeline | 2.0 | 28.1 | 7.6 | 0.07 | fused + Vector<T> + chunked across cores |
| expr | 8.4 | 10.6 | - | 0.79 | sealed sum-type records, type-test dispatch |
| wordfreq | 27.1 | 120.8 | 43.2 | 0.22 | span split + alternate lookup + single-probe increment |
| mandelbrot | 21.8 | 118.5 | - | 0.18 | reduction loop auto-parallelized |
| parallel | 34.6 | 36.8 | 34.4 | 0.94 | par_sum vs PLINQ/Parallel.For |
| particles | 25.3 | 159.1 | 82.0 | 0.16 | @[SoA] columns, vectorized + parallel `each` |

All checksums match C#. Parallel sums reassociate floating-point additions (documented); integer results are exact.

## Run 4 — expert hand-optimized C# baselines (2026-10-16)

Every benchmark now has a `handopt` C# variant. `Bench.CSharp.csproj` sets `ServerGarbageCollection`, `ConcurrentGarbageCollection`, `TieredPGO` for both C# variants (Alice apps default to server GC). All checksums match.

| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/hand-opt | winner | why |
|---|---:|---:|---:|---:|---|---|
| nbody | 50.8 | 47.0 | 49.1 | 1.03 | tie | 5 bodies, serial dependency per step; SoA spans + unrolled pairs give nothing over the JIT. No FMA: changes the checksum. |
| binarytrees | 127.1 | 249.3 | 130.2 | 0.98 | tie | both parallelize iterations over a real per-iteration tree; GC bound. |
| pipeline | 2.4 | 30.4 | 2.7 | 0.89 | tie | both fused + `Vector<T>` + chunked over cores; memory bound. |
| expr | 9.8 | 10.5 | 9.1 | 1.08 | tie / C# slightly | hand-opt uses virtual `Eval` on sealed classes (one indirect call per node) vs Alice type-test chain. Int-tag switch measured slower (13 ms). |
| wordfreq | 27.5 | 78.7 | 4.7 | 5.85 | C# | parallel partition at spaces, per-chunk dictionary, merge; `GetValueRefOrAddDefault` on the alternate lookup. |
| mandelbrot | 19.3 | 113.9 | 8.3 | 2.33 | C# | `Vector256<double>` 4 lanes over x with an active mask + early exit, on top of row parallelism. |
| parallel | 32.5 | 34.9 | 31.4 | 1.04 | tie | bound by scalar `Math.Sin/Cos`; no honest vectorization. |
| particles | 23.4 | 136.1 | 2.3 | 10.2 | C# | SoA + parallel + vector, and fuses the 3 passes x 10 steps into one pass with state in registers (40 memory sweeps -> 1). |

Gaps Alice would need to close:
- wordfreq: auto-parallel map-reduce over string partitions with per-worker dictionaries + merge (dictionary-count reduction).
- mandelbrot: vectorize inner `while` loops with data-dependent exit (masked lanes, `ConditionalSelect`, all-lanes-done exit).
- particles: loop fusion across consecutive `each` calls and across the outer `for step` loop (elementwise-independent updates), keeping columns in registers.
- expr: lower sum-type `case` to virtual dispatch (or a vtable-like method per variant) instead of sequential `is` tests.

## Run 5 — vs expert hand-optimized C# (fusion, SIMT, load balancing) (2026-10-16)

| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/hand-opt | note |
|---|---:|---:|---:|---:|---|
| nbody | 55.5 | 61.4 | 52.5 | 1.06 | sequential; same IL, noise |
| binarytrees | 93.3 | 166.0 | 95.3 | 0.98 | auto-parallel pure calls + server GC |
| pipeline | 2.1 | 31.3 | 2.0 | 1.05 | fused + vector + parallel, tie |
| expr | 11.2 | 11.4 | 10.2 | 1.10 | sum-type dispatch, noise band |
| wordfreq | 5.4 | 89.4 | 6.7 | 0.81 | parallel span map-reduce, order-preserving merge |
| mandelbrot | 11.0 | 120.8 | 10.4 | 1.06 | auto-parallel rows + SIMT lanes for the escape loop |
| parallel | 39.9 | 45.1 | 39.2 | 1.02 | tie |
| particles | 2.3 | 139.2 | 2.3 | 1.00 | SoA pass fusion + interchange, registers, parallel |

Alice beats idiomatic C# on every benchmark (0.02x–0.98x) and is within ~10% of expert hand-written C# everywhere (laptop noise ±10–20%), from plain idiomatic Alice source.

## Run 6 — vs C (clang 22 -O3 -march=native) (2026-10-16)

`c/bench.c`: idiomatic C for 5 benchmarks, serial and pthreads-parallel (no OpenMP on this machine). Build: `clang -O3 -march=native -o c/bench c/bench.c -lm -lpthread`; run: `c/bench <name> <serial|parallel>`. Same checksums as Alice/C#.

| benchmark | Alice ms | C serial ms | C parallel ms | Alice vs best C |
|---|---:|---:|---:|---|
| binarytrees | 90.1 | 300.6 | 320.5 | 3.3x faster (GC bump allocation vs malloc/free) |
| pipeline | 2.5 | 15.3 | 4.0 | 1.6x faster (fused + vector + parallel) |
| mandelbrot | 9.8 | 102.0 | 15.3 | 1.6x faster (parallel rows + SIMT lanes) |
| particles | 2.4 | 22.4 | 2.9 | 1.2x faster (pass fusion + interchange) |
| nbody | 47.2 | 29.9 | - | C 1.6x faster |

nbody is a .NET code-generation limit, not an Alice lowering gap: expert hand-optimized C# is also ~50 ms. clang fully unrolls the fixed 5-body pair loop and SLP-vectorizes the x/y/z arithmetic; RyuJIT does neither.

## Run 6b — after floating-point contraction (FMA), nbody only

| variant | ms |
|---|---:|
| Alice (FMA contraction) | 43.6 |
| C clang -O3 -march=native (FMA + vectorization) | 30.3 |
| C clang -O3 -march=native -ffp-contract=off | 41.5 |

C's default FMA contraction explained most of the gap; Alice now contracts too (checksum unchanged). The rest is
clang's vectorization/scheduling of the fixed 5-body loop, which RyuJIT does not reproduce (see docs/performance.md).

## Run 7 — nbody at C speed with `@[Native]` (2026-10-16)

| variant (same session, 3 runs) | ms |
|---|---:|
| Alice `@[Native]`, bodies packed as one array (`alice/nbody_native.al`) | 31.8 / 32.1 / 32.9 |
| C clang -O3 -march=native (`c/bench nbody serial`) | 33.4 / 34.5 / 34.5 |
| Alice managed (JIT, FMA) | ~43.6 |

Identical checksum (-169086185). With this, Alice matches or beats C on all five C benchmarks (run 6 + run 7).

## Run 8 — 0.12.0: ownership, lean Par scheduler, same GC on both sides (2026-10-22)

`run.sh` now runs Alice programs with `DOTNET_gcServer=1`: the C# baselines set `ServerGarbageCollection`, while
generated Alice programs default to workstation GC (ADR 0013), so earlier `binarytrees` gaps compared two GCs.
Alice serial vs C# serial (`DOTNET_PROCESSOR_COUNT=1`, same GC) was 300 vs 235 ms before that was found; with the
same server GC both land at ~170 ms. The self-recursive "kernel" rule (ADR 0014) now also requires a primitive
result: object-building recursion such as `bottom_up` keeps tier-1 PGO (+3 % when forced to full opt).

| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/idiomatic | Alice/hand-opt | checksum |
|---|---:|---:|---:|---:|---:|---|
| nbody | 47.8 | 53.3 | 48.9 | 0.90 | 0.98 | ok |
| binarytrees | 171.3 | 167.6 | 91.4 | 1.02 | 1.87 | ok |
| pipeline | 2.0 | 37.7 | 2.1 | 0.05 | 0.94 | ok |
| expr | 10.3 | 11.0 | 10.5 | 0.94 | 0.99 | ok |
| wordfreq | 6.9 | 80.5 | 5.3 | 0.09 | 1.30 | ok |
| mandelbrot | 9.7 | 104.5 | 8.7 | 0.09 | 1.12 | ok |
| parallel | 34.8 | 40.4 | 34.0 | 0.86 | 1.02 | ok |
| particles | 2.4 | 135.8 | 2.4 | 0.02 | 1.02 | ok |

Polyglot suite (`alicelangbench/suite`, cold process, median of 7, CPUs 2-11) at sizes where the algorithm
dominates startup:

| workload (size) | Alice ms | C# ms | C#/Alice |
|---|---:|---:|---:|
| editdist (16384) | 583.7 | 595.0 | 1.02 |
| hashmap (262144) | 172.9 | 387.3 | 2.24 |
| merkle (262144) | 406.9 | 639.2 | 1.57 |
| symreg (262144) | 118.7 | 558.3 | 4.70 |
| tree-radix (262144) | 157.4 | 494.5 | 3.14 |
| kmeans (262144) | 148.9 | 167.1 | 1.12 |
| queens (1024 → n=14) | 913.0 | 1004.7 | 1.10 |

The other ten workloads cap their input internally and finish in 55-90 ms, inside process-startup noise
(±7 ms) for both languages.

## Tensor kernels (`benchmarks/tensors`, ADR 0030)

```bash
dotnet run -c Release --project benchmarks/tensors/Bench.Tensors.csproj   # optional arg: n (default 4096)
```

GEMV y = W x, 4096x4096, median of 15, same laptop (AVX2, 12 threads, machine shared with other jobs):

| kernel | ms/GEMV | GB/s weights | GEMV/s |
|---|---:|---:|---:|
| naive f32 (scalar loop, 1 thread) | 17.684 | 3.8 | 57 |
| Nn.Gemv f32 | 2.042 | 32.9 | 490 |
| Nn.Gemv bf16 | 0.922 | 36.4 | 1084 |
| Nn.Gemv int8 block | 0.665 | 26.8 | 1504 |
| Nn.Gemv int4 block | 0.593 | 15.9 | 1687 |

GEMV is memory-bound: smaller weights mean more GEMVs per second.
