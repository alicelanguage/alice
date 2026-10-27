# Bend vs Alice (and C, Rust, C#)

Six fork/join workloads in Bend 2.0.39, Alice (dev compiler, 0.13.x), C, Rust and C#. Every language uses the same
algorithm and the same binary fork shape (`a b = f(x) g(y)` in Bend, `a, b = f(x), g(y)` in Alice), with the same
`UInt32` wrapping arithmetic and `Float32` mandelbrot. Every result matches across languages (see Checksums).

```bash
./run.sh build     # build all five, then measure; RUNS=7 by default
./run.sh           # measure only
python3 report.py  # rebuild results/summary.md from results/*.json
```

| Workload | Arg | What it stresses |
|---|---|---|
| `pow2` | 28 | `pow2(n) = pow2(n-1) + pow2(n-1)`: 2^28 calls, pure call overhead, perfectly balanced |
| `fib` | 38 | naive `fib`: ~126M calls, unbalanced fork (n-1 vs n-2) |
| `tree` | 23 | build a complete binary tree (8M leaves, 16M nodes), then sum it: allocation and memory |
| `nqueens` | 14 | bitmask N-queens count: divergent, irregular fork tree |
| `mandel` | 11 | 2048x2048 `Float32` escape-time sum, 256 iterations, fork over rows: numeric loops |
| `collatz` | 22 | Collatz step count for 1..2^22 (capped at 1000 steps): branchy integer loops |

## How each language forks

| | Serial (column "1") | Parallel (column "12") |
|---|---|---|
| Bend | native binary, `+RTS --threads 1`, pinned to one core | native binary, default 12 threads; every parallel let is a fork, the runtime decides |
| Alice | no annotations; pinned to one core with `DOTNET_PROCESSOR_COUNT=1`, so `Par.Forkable` is false and calls run the `__serial` clone | the compiler forks the pure recursive pairs itself (ADR 0015) |
| C | clang `-O3 -march=native -ffp-contract=off` | gcc `-fopenmp`, `omp task` above depth 10 |
| Rust | `-C target-cpu=native`, LTO, plain recursion | `rayon::join` above depth 10 |
| C# | .NET 10 Release, TieredPGO, plain recursion | `Parallel.Invoke` above depth 10, fork lambdas in separate helpers |

Time is **cold-process wall time** (hyperfine `-N`, median of 5, one warm-up): startup, JIT and the work together, as
in the polyglot suite's FAIRNESS.md. .NET pays ~22-27 ms startup and JIT. Bend, C and Rust pay ~1 ms.

## Results

Machine: i5-1235U (2P+8E cores, 12 threads, laptop), CachyOS Linux 7.0.9, .NET 10.0.4, clang/gcc system, rustc
system, Bend 2.0.39. Median ms, lower is better. Full tables with max and peak RSS: [results/summary.md](results/summary.md).

### Serial, 1 core

| workload | Bend | Alice | C | Rust | C# | Bend / Alice |
|---|---:|---:|---:|---:|---:|---:|
| pow2 | 1541 | 1218 | 1* | 1206 | 1414 | 1.26 |
| fib | 280 | 625 | 146 | 245 | 381 | 0.45 |
| tree | **123** | 1542 | 472 | 774 | 1430 | 0.08 |
| nqueens | 331 | 620 | 240 | 357 | 430 | 0.53 |
| mandel | 672 | 749 | 673 | 672 | 1117 | 0.90 |
| collatz | 1159 | 2787 | 653 | 695 | 2537 | 0.42 |

\* clang turns `pow2` into a closed form; the C `pow2` number measures nothing.

### Parallel, 12 threads

| workload | Bend | Alice | C | Rust | C# | Bend / Alice |
|---|---:|---:|---:|---:|---:|---:|
| pow2 | 210 | 193 | 31* | 158 | 247 | 1.09 |
| fib | 43 | 140 | 27 | 42 | 94 | 0.31 |
| tree | **90** | 681 | 162 | 565 | 1019 | 0.13 |
| nqueens | 144 | 282 | 78 | 111 | 187 | 0.51 |
| mandel | 100 | 152 | 121 | 92 | 218 | 0.66 |
| collatz | 159 | 356 | 186 | 83 | 326 | 0.45 |

### Speedup on 12 threads (serial / parallel)

| workload | Bend | Alice | C | Rust | C# |
|---|---:|---:|---:|---:|---:|
| pow2 | 7.4x | 6.3x | - | 7.6x | 5.7x |
| fib | 6.5x | 4.5x | 5.5x | 5.8x | 4.1x |
| tree | 1.4x | 2.3x | 2.9x | 1.4x | 1.4x |
| nqueens | 2.3x | 2.2x | 3.1x | 3.2x | 2.3x |
| mandel | 6.7x | 4.9x | 5.5x | 7.3x | 5.1x |
| collatz | 7.3x | 7.8x | 3.5x | 8.4x | 7.8x |

Ideal is ~7-8x on this CPU (2 P-cores with HT plus 8 slower E-cores).

### Peak RSS, MB (serial)

| workload | Bend | Alice | C | Rust | C# |
|---|---:|---:|---:|---:|---:|
| tree | **134** | 486 | 514 | 514 | 484 |
| others | 6 | 31-32 | 4 | 4 | 24-25 |

## Findings

1. **Bend wins allocation-heavy trees by a wide margin.** `tree` serial: Bend 123 ms, C (malloc) 472 ms, Rust (Box)
   774 ms, Alice 1542 ms; Bend also uses a quarter of the memory (134 MB vs ~500 MB). A Bend leaf holding a `U32`
   lives inside its parent's 64-bit word, nodes come from a bump heap with no headers, and `match` frees each node as
   it is consumed, so the sum walks memory once and releases it. Alice and C# allocate 16M GC objects with headers
   and pay gen0 to gen2 promotion for a tree that is entirely live. This is Alice's largest gap to Bend (12.5x serial,
   7.6x parallel).
2. **Calls: Bend ≈ Rust ≈ Alice on balanced recursion.** `pow2` serial: Alice 1218, Rust 1206, C# 1414, Bend 1541.
   On 12 threads Alice (193 ms) beats Bend (210) and C# (247). Alice's serial clone (ADR 0015) puts forked recursion
   at plain-call cost.
3. **Numeric loops: a tie.** `mandel` serial: Bend, C and Rust 672-673 ms, Alice 749, C# 1117. Alice beats C# by
   1.5x because its emitter contracts `2*zr*zi + ci` into an FMA (C# leaves it as mul+add). Same FMA changes the
   checksum (see below).
4. **Branchy integer loops favour native compilers.** `collatz`: C 653, Rust 695, Bend 1159, C# 2537, Alice 2787.
   Alice and C# are equal, so this is RyuJIT codegen for the branchy `x odd ? 3x+1 : x/2` loop, not Alice's lowering.
   Alice is 2.4x behind Bend here.
5. **Bend's parallel scaling is the best of the group on balanced work** (`pow2` 7.4x, `collatz` 7.3x, `mandel`
   6.7x, `fib` 6.5x) with zero tuning: no depth cutoff, no thread-pool knobs. Alice scales 4.5-7.8x with no
   annotations at all. Every language gets only ~2-3x on `nqueens` (irregular tree) and `tree` (memory-bound).
6. **Startup.** Bend ~1 ms, Alice ~27 ms, C# ~22 ms. On `fib` (12 threads, 140 ms) Alice's startup is a fifth of
   the total; Bend has no runtime to start.

### Gaps worth looking at in Alice

- **Serial clone misses mutual recursion.** In `nqueens`, `place__serial` calls `solve`, and `solve` calls the
  forking `place` (not `place__serial`), so every level of the recursion goes back through the `Par.Forkable`
  check and the forking body. Alice is 1.44x slower than C# on `nqueens` serial. Not yet confirmed as the cause.
- **`fib` serial vs C#** (625 vs 381 ms in the suite run) is mostly noise: an isolated rerun gave Alice 460 ms.
  Replacing the checked `n.to_u32()` leaf with literals changed nothing (460 vs 463 ms).
- **Tree allocation** (finding 1) is the structural gap. Bend's affine heap frees as it reads; .NET cannot do that
  for a GC object graph. Struct/arena-backed trees would be the comparable Alice design.

## Checksums

| workload | value |
|---|---|
| pow2 28 | 268435456 |
| fib 38 | 39088169 |
| tree 23 | 4290772992 (leaves 2^23 .. 2^24-1, mod 2^32) |
| nqueens 14 | 365596 |
| mandel 11 | Bend, C, Rust, C# 294133811; Alice 294131609 (FMA contraction, as `clang` without `-ffp-contract=off`) |
| collatz 22 | 613347078 |

Mandelbrot at size 8: Bend/C/Rust/C# 4608642, Alice and C with FMA contraction 4608070.

## Caveats

- Laptop CPU: P/E cores, turbo, thermals. A first full run (before the C# fix) measured all languages 1.5-2x slower;
  ratios held. Read ratios within ~1.25x as equal.
- Cold process. Steady-state (warm JIT) numbers would favour .NET more; Bend and the native languages have no JIT.
- Bend's loops (`nqueens`, `mandel`, `collatz`) use `@unsafe` defs (`def f?`), since they recurse without a
  shrinking argument. The trees (`pow2`, `tree`, `range`/`rows` splitting) pass Bend's termination checker.
- The C# serial path originally captured its fork lambdas in the recursive method, which allocated a closure on every
  call (5.2 s on `pow2`). The fork lambdas now live in separate helpers, as Alice's emitter does.
- No GPU: this machine has no CUDA, so Bend's `!` GPU path is not measured.
