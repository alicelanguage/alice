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
