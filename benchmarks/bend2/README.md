# Bend 2 runtime suite: Alice vs C (and Bend's pinned numbers)

The workloads of Bend 2's runtime benchmark (`bend/bench/runtime/<name>/main.{bend,c}`, the suite behind the
numbers on bend-lang.com), ported 1:1 to Alice: same algorithm, same fork shape as `main.bend`, same `UInt32`
wrapping arithmetic and the same checksum as Bend's C twin `main.c`, at Bend's **big** sizes. Every Alice checksum
equals the C checksum (see Checksums). Stream A of [docs/ROADMAP-1.0.md](../../docs/ROADMAP-1.0.md).

```bash
./run.sh build        # build the compiler, every alice/<name>.al and c/<name>.c, then measure; RUNS=5 by default
./run.sh              # measure only
./run.sh build bfs    # one workload
python3 report.py     # rebuild results/summary.md from results/*.json
```

`run.sh` discovers the workloads from `alice/*.al`; each needs `c/<name>.c`. A `.al` program takes Bend's big
size by default (and the small size as arguments, for checking); the C program has it compiled in.

`alice/<name>.al` is the 1:1 port, the Workstream A gate (ADR 0039): same algorithm, same data shape (sum types
where Bend has them), same fork tree. `idiomatic/<name>.al`, where it exists, is the fastest Alice shape found
(symreg, tree-bitonic, tree-matmul, tree-radix: flat arrays instead of GC nodes, same compare order, fork tree and
checksum). `run.sh` builds and measures it as two extra configurations, `idiom/1` and `idiom/N`, and `report.py`
adds an "Idiomatic shape" table. Details and the shape comparison: [NOTES-bench2.md](NOTES-bench2.md).

| Workload | Size (big) | What it stresses | Fork tree (as in `main.bend`) |
|---|---|---|---|
| `bfs` | 2^19 mazes of 32x32 | flat `UInt32` queue and distance arrays, branchy neighbour checks | `batch` over the mazes |
| `editdist` | 2^15 pairs of 256 symbols | Levenshtein DP with two rolling rows: array reads and writes | `batch` over the pairs |
| `gameoflife` | 64 x 2^18 4x4 soups, 32 generations | bit-packed board step: shifts, masks, popcount | `batch` over 64-soup chunks |
| `hashmap` | 2^11 tables x 16384 keys | separate chaining in index-linked node arrays: dependent loads | `batch` over the tables |
| `kmeans` | 64 restarts x 20 rounds x 2^19 points, K = 8 | 24-scalar accumulator kernel plus a stats tree (enum) per 64 points | `rbatch` over restarts, `pfold` over chunks |
| `lexer` | 2^23 generated lines | byte buffer generation and a tokenizer loop | `batch` over the lines |
| `mandelbrot` | 4096x4096, 51 iterations | 8.8 fixed-point escape time, two passes, histogram LUT | `hfold` over chunks, `rcol` over every pixel |
| `merkle` | 2^22 leaves x 30 Speck32/64 blocks | ARX rounds, an 8M-node enum tree, audit and proof | `build`, `audit`, `htree`, `pgen` |

## How each language forks

| | Serial (column "1") | Parallel (all cores) |
|---|---|---|
| Alice | no annotations; `DOTNET_PROCESSOR_COUNT=1`, pinned with `taskset -c $CORE` (default 0), so `Par.Forkable` is false and calls run the `__serial` clone | the compiler forks the pure recursive pairs itself (ADR 0015); `alice explain` shows every `batch`-like function as "fork/join" |
| C | Bend's `main.c`, unchanged: clang `-O3 -march=native`, pinned like Alice | the same file with clang `-O3 -march=native -fopenmp` (libomp): `omp task` over the top 10 levels of the same recursion (`lexer`: `omp parallel for` over its line loop) |
| Bend | not run here (not installed); the pinned `apple_m4_max.txt` numbers, copied to `bend_pin_apple_m4_max.txt` | same file, PAR-CPU column (16 cores) |

Bend's `main.c` files are all serial. `c/<name>.c` is Bend's file with the OpenMP path added inside `#ifdef _OPENMP`
blocks by [omp_patch.py](omp_patch.py); with those blocks removed the file is byte-identical to Bend's
(`diff <(sed '/#ifdef _OPENMP/,/#endif/d' c/bfs.c) bend/bench/runtime/bfs/main.c`), so the serial C column is Bend's
own C twin. Two OpenMP paths change data layout, not work: `kmeans` gives each forked restart its own stats arena,
and `merkle` builds into a preallocated arena at the post-order slots the serial build would use.

Time is **cold-process wall time** (hyperfine `-N`, median of 5, one warm-up): startup, JIT and the work together,
as in [../bend-vs](../bend-vs/README.md). Peak RSS is hyperfine 2's `memory_peak_resident` of the same runs.
The Bend numbers are from **a different machine** (Apple M4 Max) and appear only as a reference column; the
machine-independent comparison is each language divided by the serial C twin on its own machine.

## Results

Machine: x86-64 build box, 32 cores, 61 GB, Arch Linux, .NET 10.0.114, clang 23 with libomp. The box was shared
with other jobs during the run (load average 8-10 on 32 cores), so read ratios within ~1.2x as equal. Median ms,
lower is better. Full tables with max, peak RSS and every ratio: [results/summary.md](results/summary.md).

### Serial, 1 core

| workload | Alice | C | Alice / C | Bend / C on M4 (reference) |
|---|---:|---:|---:|---:|
| bfs | 10843 | 6441 | 1.68 | 1.09 |
| editdist | 11348 | 8774 | 1.29 | 1.28 |
| gameoflife | 14296 | 15807 | **0.90** | 1.15 |
| hashmap | 1464 | 1426 | **1.03** | 4.41 |
| kmeans | 32904 | 2809 | 11.71 | 1.00 |
| lexer | 2097 | 1513 | **1.39** | 2.07 |
| mandelbrot | 3712 | 2462 | 1.51 | 1.19 |
| merkle | 11963 | 6880 | 1.74 | 1.23 |

### Parallel, 32 cores

| workload | Alice | C (OpenMP) | Alice speedup | C speedup | Bend speedup on M4 (16 cores) | Alice / C serial | Bend PAR / C on M4 |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 839 | 301 | 12.9x | 21.4x | 11.4x | 0.130 | 0.096 |
| editdist | 726 | 507 | 15.6x | 17.3x | 10.8x | **0.083** | 0.118 |
| gameoflife | 1123 | 637 | 12.7x | 24.8x | 12.1x | **0.071** | 0.095 |
| hashmap | 162 | 87 | 9.0x | 16.4x | 11.5x | **0.114** | 0.383 |
| kmeans | 2774 | 269 | 11.9x | 10.4x | 7.7x | 0.987 | 0.130 |
| lexer | 201 | 164 | 10.4x | 9.2x | 10.8x | **0.133** | 0.191 |
| mandelbrot | 349 | 183 | 10.6x | 13.5x | 11.3x | 0.142 | 0.105 |
| merkle | 1120 | 510 | 10.7x | 13.5x | 10.0x | 0.163 | 0.123 |

Both C builds are clang. The C 32-core column was measured again after libomp was installed (the first run used
gcc); the Alice columns are from the first run.

**Against Bend** (machine-independent: each divided by the serial C twin on its own machine; bold = Alice ahead).
Serial: Alice ahead on `gameoflife`, `hashmap`, `lexer`, equal on `editdist` (1.29 vs 1.28). Parallel: Alice ahead
on `editdist`, `gameoflife`, `hashmap`, `lexer`. Bend ahead on `bfs`, `kmeans`, `mandelbrot`, `merkle` in both modes.
Every Alice workload scales 9-16x on 32 cores; none is below 8x.

## Findings

The workloads where Alice is more than 1.3x behind serial C (none scales worse than 8x). Causes come from
`alice explain`, `alice emit`, `perf` (`DOTNET_PerfMapEnabled=1`) and `DOTNET_JitDisasm`; "probable" where not
isolated by an experiment.

1. **kmeans, 11.7x behind C, the largest gap.** The leaf `chunk` (64 points, 8 distances, a branchless argmin, 24
   scalar accumulators) is 51% of the profile, 16.5 points of it in the tier-0 (`QuickJitted`) body; `dst` and `min2`
   are not inlined into it (12% and 6.6% as their own frames). clang vectorizes the 8 distances: the same C without
   vectorization (`-fno-vectorize -fno-slp-vectorize`) runs 12.4 s instead of 2.7 s, so ~4.6x of the gap is SIMD that
   RyuJIT does not do. The rest is probably spills of 24 live accumulators, tier-0 time, and the 15 enum objects per
   chunk plus the `szip` trees (C uses an arena). `inline def` on `dst`/`min2`/`prng` made it slower (4.7 s vs 3.3 s at
   size 16): the bigger `chunk` stays longer in tier 0. Bend is at C speed here (1.00).
2. **merkle, 1.74x.** `leafh` with the Speck round loop is 74% of the profile. main.c asks clang to fully unroll the
   22 rounds (`#pragma clang loop unroll(full)`), which turns the key table into immediates; RyuJIT keeps the loop
   with a bounds-checked `rk[i]` load per round (probable; `inline def speck` changed nothing, 11.9 vs 11.6 s). The
   tree is 8M GC objects: 296 MB peak RSS against C's 130 MB arena (Bend 130 MB).
3. **bfs, 1.68x.** 81% of the profile is the tier-1 `maze`. Probable: every grid, queue and distance access
   (`cell - 1`, `cell + 32`, `q[tail]`) is bounds-checked, since the pooled arrays (`Own<uint>.Rent(1024)`) have no
   length the JIT can prove; C's are fixed `uint32_t[1024]` struct members. `to_i32`/`to_u32` are checked conversions
   (`int.CreateChecked`), visible as `TryConvertFromChecked` frames.
4. **mandelbrot, 1.51x.** `mit` is 76% of the profile. The sources write the escape freeze as a branchless select
   (`sel`); for `e2 == 0 ? nzr : zr` in the loop RyuJIT emits `test`/`jne` branches (disassembly), clang `cmov`, so
   Alice pays branch mispredictions near the set boundary.
5. **lexer, 1.39x.** `line` is 85% of the profile. Probable: bounds checks on every `buf[p]` (the loop bound is the
   generated length `n`, not `buf.size`) and the byte class compares; pooling the 128-byte buffer costs ~3%
   (`Own.Take`, `Own.Release` still in tier 0).

What made Alice fast where it is (applied in the ports, all plain Alice):

- **`inline def` on small helpers that contain a loop.** RyuJIT does not inline a method with a loop. `hashmap`'s
  chain walk `has`: 2326 -> 1410 ms serial with `inline def` (C 1213 ms in that session). `gameoflife`: 52.5 -> 29.0 s
  CPU with `inline def` on `cell_get`/`step_cell`/`board_step` (main.c marks `board_step` `always_inline`).
- **Int32 shift counts.** Alice shifts take an `Int32` count (`UInt32 >> UInt32` is AL0301), so a UInt32 position
  needs `.to_i32`, a checked conversion that kept the JIT from folding the 16 constant `step_cell` calls:
  `gameoflife` 29.1 -> 13.7 s serial with the positions written as Int32 (same bits: `-1 & 3` is 3).
- **Int32 node links** in `hashmap` (`-1` for nil) instead of UInt32 links converted with `.to_i32` per hop:
  2789 -> 2326 ms.

## Compiler work queued

Tick an item when a compiler or runtime change fixes it and the workload is re-measured.

Language and compiler issues (repros in the next section):

- [x] 1. compound wrapping assignment `x &+= y` (AL0101) (fixed, not re-measured)
- [x] 2. a UInt32 literal first assigned inside an `if` body is typed Int32 (AL0304) (fixed, not re-measured)
- [x] 3. an enum constructor call as the argument of another does not parse (AL0101 "expected type") (fixed, not re-measured)
- [x] 4. a top-level `[..._u32]` constant is emitted as `int[]`; errors land in the generated `.g.cs` (fixed, not re-measured)
- [ ] 5. swapping two fresh array locals makes a fork leaf impure, so the caller's fork disappears
- [x] 6. `String#to_u32` reports a leaked .NET constraint error (AL0300) instead of "no such method" (fixed, not re-measured)
- [x] 7. `UInt32 >> UInt32` is undefined (AL0301); shift counts must be Int32 (fixed, not re-measured)
- [ ] 8. a sum-type constructor whose first argument is a qualified path (`T::N(T::L(2), a)`, `M::Node(M::Free, r)`) does not parse (AL0101; same root as 3)
- [ ] 9. a local initialised with a nullary variant (`r = M::Free`) is typed as the variant, so `r = M::Node(..)` fails (AL0304)
- [ ] 10. reading a top-level constant (`CUT = 3`, or `val cut = 3`) in a recursive method silently turns off fork/join
- [ ] 11. `val CUT = 3` (constant-style name) does not parse (AL0101); `val k = 3` does
- [ ] 12. `String#to_u32` leaks a .NET constraint error (AL0300; same as 6)
- [ ] 13. a local named `nil` produces raw C# errors instead of a reserved-word message
- [ ] 14. lending a fresh local array to a helper that writes it makes the caller impure, so every fork above it disappears (blocks arena-style trees)
- [ ] 15. no pure `Float32` square root under its full name: `x.to_f64.sqrt.to_f32` is 1.9x slower on nbody; `System.MathF.Sqrt` is not trusted (forks disappear); only unqualified `MathF.sqrt` works
- [ ] 16. `alice build` emits Server GC with `Gen0MaxBudget` = 16 MB: ADT trees on 32 cores spend most of their time in GC (tree-bitonic-adt 40.6 s / 19.8 GB parallel)

Codegen causes (Findings 1-5):

- [ ] kmeans: no SIMD for the 8-distance kernel, `dst`/`min2` not inlined into `chunk`, tier-0 time, GC stats trees
- [ ] merkle: the 22-round Speck loop is not unrolled; a bounds-checked `rk[i]` load per round
- [ ] bfs: bounds checks on pooled fixed-size arrays; checked `to_i32`/`to_u32` conversions
- [ ] mandelbrot: the branchless select `c ? a : b` in a loop becomes branches, not `cmov`
- [ ] lexer: bounds checks on `buf[p]` under a loop bound that is not `buf.size`
- [ ] terrain: clang vectorizes the noise fill (C with `-fno-vectorize` is 1.5x slower); the Gauss-Seidel sweep has bounds-checked neighbour loads and checked `to_u32` (Alice serial 6.1 s, C 2.7 s)
- [ ] queens: the two-UInt32 `Stats` record struct returned through every recursion level probably goes through memory (serial 1.4x C; not profiled)
- [ ] raytrace: the sphere table is a bounds-checked `Float32` array parameter where C folds it into immediates; 2^26 colf fork-tree leaves (serial 1.4x C)

## Compiler issues found (minimal repros; the compiler is unchanged)

| # | Repro | Result | Workaround in the ports |
|---|---|---|---|
| 1 | `x = 1_u32` then `x &+= 1_u32` | AL0101 unexpected token `=`: the wrapping operators have no compound form | `x = x &+ 1_u32` |
| 2 | `def f(m : Int32) : UInt32` / `if m > 1` / `w = 7_u32` / `return w` / `end` / `0_u32` / `end` | AL0304 "expected Int32, got UInt32" at `w = 7_u32`: a local first assigned a UInt32 **literal** inside an `if` body is typed Int32 (Int64 and Float64 literals work, `w = 1_u32 &+ t` works, the same line in a `while` body works) | `w : UInt32 = 7_u32` (lexer) |
| 3 | `enum T` / `L(v : UInt32)` / `N(l : T, r : T)` / `end`, then `T::N(T::L(a), T::L(a))` | AL0101 "expected type (found 'a')": an enum constructor call as the argument of another does not parse | bind the inner values to locals first (kmeans) |
| 4 | top-level `RK = [256_u32, 5394_u32]` | emitted as `public static readonly int[] RK = new int[] { 256U, 5394U };`, the build fails with AL0304 errors located in the generated `.g.cs` | build the table in main and pass it down (merkle) |
| 5 | in a fork leaf: `a = Array(Int32).new(4)`, `b = Array(Int32).new(4)`, `t = a`, `a = b`, `b = t`, `a[0] = n` | the caller's `x, y = g(d - 1), g(d - 1)` stops forking (`alice explain` shows no fork/join): a local assigned from another fresh array no longer counts as "an array it allocated itself", so the write makes the leaf impure. Without the swap it forks | both DP rows in one array with swapped offsets (editdist: 10.9 s on 32 cores with the swap, 0.68 s without) |
| 6 | `"7".to_u32` | AL0300 "The type 'String' cannot be used as type parameter 'T' ... INumber(String)": a .NET constraint error instead of "String has no to_u32" | `ARGV[1].to_i.to_u32` |
| 7 | `x >> s` with `s : UInt32` | AL0301 `>>` is not defined for UInt32 and UInt32 | Int32 shift counts (see above) |
| 8 | `enum T` / `L(v : Int32)` / `N(l : T, r : T)` / `end`, `a = T::L(1)`, `T::N(T::L(2), a)` | AL0101 "expected type (found '2')"; `M::Node(M::Free, r)` gives "found 'r'" | bind the inner constructor to a local (tree-bitonic `warp_leaf`, tree-radix `radix`) |
| 9 | `r = M::Free`, `f : M = M::Free`, `r = M::Node(r, f)` | AL0304 "expected Free, got Node" | `r : Map = Map::Busy` (tree-radix) |
| 10 | `CUT = 3`, `def tree(d)` with `return 1 if d == CUT` and `x, y = tree(d - 1), tree(d - 1)` | `alice explain`: no fork/join (with the literal `3` it forks); `CUT` is emitted as `static readonly int` | inline the literal (idiomatic/tree-bitonic: 6.2 s vs 0.83 s on 32 cores) |
| 11 | top-level `val CUT = 3` | AL0101 unexpected keyword `val` | plain literal |
| 12 | `"17".to_u32` | AL0300 generic constraint error (as 6) | `.to_i.to_u32` |
| 13 | `nil = x > 0` in a method | AL0300 "The left-hand side of an assignment must be a variable" and "Cannot convert null to 'Bool'" | rename the local |
| 14 | `def fill(a, i)` writes `a[i]`; `leaf` allocates `a = Array(Int32).new(n)` and calls `fill(a, i)`; `tree` forks over `leaf` | no fork for `tree`: `leaf` is impure | whole kernel in one method body with explicit loops/stacks (`bsort_small`, `flow_small`, `to_map_local`, terrain `tile_run`) |
| 15 | `def s1(x : Float32)` = `System.MathF.Sqrt(x)` under a forking `tree` | no fork; `MathF.sqrt(x)` (unqualified) forks, `x.to_f64.sqrt.to_f32` forks but nbody serial is 17.6 s vs 9.1 s | `MathF.sqrt(x)` (nbody, raytrace) |
| 16 | emitted runtimeconfig: `System.GC.Server=true`, `System.GC.Gen0MaxBudget=16777216` | tree-bitonic-adt 2^23: 192.5 s / 2.9 GB serial, 40.6 s / 19.8 GB parallel; perf (2^19 serial): 67% of samples in libcoreclr. `DOTNET_GCgen0size`/`GCgen0MaxBudget` env: no change; `DOTNET_gcServer=0`: 2^20 parallel 8x slower | flat shapes in idiomatic/ |

`"...".bytes` returns `Array(Int32)` (Ruby's `String#bytes`), not `Array(UInt8)`: not a bug, noted because passing it
to an `Array(UInt8)` parameter fails with AL0305.

## Checksums (big sizes)

All four configurations (Alice serial and parallel, C serial and parallel) print the same value, Bend's expected
value from `main.bend` ([results/checksums.tsv](results/checksums.tsv)). The small sizes were checked once by hand.

| workload | big | small (arguments: value) |
|---|---|---|
| bfs | 651176970 | `4`: 512822161 |
| editdist | 2229810577 | `2`: 2065873279 |
| gameoflife | 2016151040 | `1 2`: 2601177279 |
| hashmap | 1307803744 | `2 256`: 4206244792 |
| kmeans | 1616398086 | `7`: 4219097976 |
| lexer | 2401049475 | `8`: 1822208108 |
| mandelbrot | 3101455856 | `2 7`: 887240761 |
| merkle | 3104235417 | `4 13`: 524568222 |

## Caveats

- Shared machine: other jobs ran during the measurement. Serial runs are pinned to one core, but caches, memory
  bandwidth and turbo are shared. Single runs of the same binary varied by up to ~15% (C `gameoflife` 13.9-15.8 s).
- Bend's numbers are pinned results from an Apple M4 Max, not measured here (Bend is not installed on this box).
  Only the X/C ratios compare across machines, and they assume the C twin stands in the same relation to Bend on
  both CPUs.
- Cold process: Alice pays ~26 ms startup and JIT (the startup floor: the first workload in `alice/` at input 0) and runs its first calls in tier-0 code; on
  the short runs (`hashmap`, `lexer`, the parallel `mandelbrot`) that is a visible share.
- Alice structs and enums replace Bend's records and data types one to one; where main.c uses an arena (kmeans,
  merkle), Alice allocates GC objects, as Bend allocates nodes on its heap.
