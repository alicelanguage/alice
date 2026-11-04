# bend2: IL backend vs C# backend (il-opt)

## 1.0.1 re-run (2026-11-04, commit b2e41c8 (v1.0.1))

Same machine (the forge, .NET SDK 10.0.114). Serial: `taskset -c 11,27 env ALICE_PAR=0`, cold process, hyperfine `-N -w 1 -r 5`, median ms; the two images of each workload interleaved (run 2 of summary.md, load average 8.0-8.8). IL = `alice emit --il`; C# = `alice build` (which uses the C# backend for loose files in 1.0.1, see summary.md). Checksums identical for every image and run. Ratios IL / C# (< 1: IL faster). Raw: [1.0.1/il/](1.0.1/il/).

| workload | C# ms | IL ms | IL / C# | R2 (2026-11-03) |
|---|---:|---:|---:|---:|
| bfs | 9573 | 7526 | 0.786 | 0.735 |
| editdist | 11210 | 8204 | 0.732 | 0.754 |
| gameoflife | 14022 | 13818 | 0.985 | 0.922 |
| hashmap | 1426 | 1406 | 0.986 | 0.988 |
| kmeans | 22125 | 22126 | 1.000 | 0.989 |
| lexer | 1970 | 1850 | 0.939 | 0.891 |
| mandelbrot | 3589 | 3577 | 0.997 | 0.977 |
| merkle | 8420 | 8414 | 0.999 | 0.999 |
| nbody | 8062 | 8066 | 1.001 | 1.003 |
| queens | 6431 | 6456 | 1.004 | 1.006 |
| raytrace | 9999 | 9917 | 0.992 | 0.991 |
| symreg | 8331 | 7926 | 0.951 | 1.007 |
| terrain | 6050 | 5909 | 0.977 | 0.980 |
| tree-bitonic | 120122 | 127188 | 1.059 | 1.106 |
| tree-matmul | 9750 | 10424 | 1.069 | 1.093 |
| tree-radix | 14962 | 14831 | 0.991 | 1.013 |

Geomean IL / C#, serial cold: 0.963 over 16 workloads; 0.946 without the three tree ports. tree-bitonic here is the full size (R2 used size 21).

## Previous runs

The sections below are the earlier rounds, kept as published.

## Round 2 (2026-11-03, main f0f7614 + il-opt round 2)

Same machine. Serial: `taskset -c 12,28 env ALICE_PAR=0` (cores 13,29 were shared with another worker's timing
runs during part of round 1 and round 2, so round 2 re-measures every image on 12,28), cold process runs, medians
of 3 after a checksum run. `R1` = round 1 il-opt, `R2` = round 2 (store forwarding, inline expansion bound).
Warm: in-process repeats of the entry point (`.ilopt/warm`, scratch harness: 5 runs in one process, median of runs
3-5, checksum checked every run). Parallel: all cores, default `ALICE_PAR`, medians of 5 (10 for the rerun rows);
the box had load average 19-28 from other jobs, so parallel walls carry ~5-10 % noise. Checksums identical for
every image, mode and run. Ratios IL / C# (< 1: IL faster).

| workload | C# ms | R1 cold | R2 cold | R2 cycles | R2 warm | R2 parallel | note |
|---|---:|---:|---:|---:|---:|---:|---|
| bfs | 10513 | 0.759 | 0.735 | 0.752 | | 0.844 | R2 inlining bound |
| editdist | 11579 | 1.001 | **0.754** | 0.734 | **0.733** | 0.770 | store forwarding: the DP row carry stays in a register |
| gameoflife | 15388 | 0.957 | **0.922** | 0.946 | | 0.992 | inline expansion bound: `classify` no longer forced into `chunk` |
| hashmap | 1438 | 0.985 | 0.988 | 0.985 | | 0.924 | |
| kmeans | 22430 | 0.999 | 0.989 | 0.996 | 0.998 | 0.960 | parity (same machine code) |
| lexer | 2188 | 0.889 | 0.891 | 0.887 | | 0.997 | |
| mandelbrot | 3779 | 0.985 | 0.977 | 0.983 | | 0.920 | |
| merkle | 8516 | 0.998 | 0.999 | 1.000 | 0.998 | 0.983 | parity |
| nbody | 8142 | 1.000 | 1.003 | 1.002 | 1.002 | 0.998 | parity (same machine code) |
| queens | 6447 | 1.009 | 1.006 | 1.006 | 1.001 | 0.930 | parity: IL equals Roslyn's but for one dead struct temp |
| raytrace | 10015 | 0.990 | 0.991 | 0.993 | | 0.980 | |
| symreg | 8376 | 0.981 | 1.007 | 0.993 | **0.916** | 1.06-1.10 | arena; parallel: 1.18 cycles (arena path, fix-runtime) |
| terrain | 6063 | 0.974 | 0.980 | 0.979 | | 0.886 | |
| tree-bitonic (21) | 16626 | 1.170 | 1.106 | 1.087 | 1.21 | | OPEN: arena Alloc/Take (fix-runtime worker, AirToIl.Arena.cs) |
| tree-matmul | 9980 | 1.089 | 1.093 | 1.089 | 1.110 | 1.15-1.17 | OPEN: arena |
| tree-radix | 12534 | 1.018 | 1.013 | 1.015 | **0.951** | 1.023 | arena |

Geomean IL / C#, serial cold: 0.96 (R1 0.98); without the three tree ports: 0.94. Warm (9 rows): 0.98.
editdist's R2 cold row is its own rerun (C# 11579 ms); the other R2 rows come from the same run as R1.

## Round 2 changes

- **Loop-carried store forwarding** (`Air/Passes/IlOpt/StoreForward.cs`, switch `forward`): a load that reads the
  element the previous iteration stored (`rows[co + j]` after `rows[co + j + 1] = v`) reads a local instead; only
  for fresh, non-escaping arrays with one unconditional store per iteration and proven-in-bounds loads (proof list
  in the type summary). editdist 11.6 s -> 8.7 s cold; the C# backend cannot express it without the source change.
- **Inline expansion bound** (`Backend/Il/Opt/MethodFlags.cs`): a method is forced inline only if its size with
  the `inline`/tiny callees the JIT will put in stays under the bound; gameoflife's `chunk` keeps its own
  `inline` step_cells instead of spending the budget on `classify` (FullOpts `chunk` 4152 B -> 1760 B, C# 1797 B).
- Looked at and left: `merkle.speck`'s 22 constant-index loads (the JIT already checks `rk.Length > 21` once),
  queens `solve` (Roslyn's IL but for a dead `initobj` temp), kmeans `chunk` (same machine code as C#).

## Round 1

Machine: forge, Intel Xeon Platinum 8488C (AVX-512), .NET SDK 10.0.114, 2026-11-03, main 21b5933 + il-opt.
Method: `taskset -c 13,29 env ALICE_PAR=0` (one core pair, serial), cold process runs, `hyperfine -N`, medians
of 3 runs after one checksum run per image (`.ilopt/run3.sh`, scratch); cycles and retired instructions from
hyperfine's perf counters. Same runtime and runtimeconfig for all images. Checksums identical for every row and
every image. Default arguments except tree-bitonic (`21`; the default 23 runs ~4 min per image).
Columns: `IL0` = IL backend with `ALICE_IL_OPT=0` (before), `IL` = IL backend with il-opt on (after); ratios are
IL / C# (< 1: IL faster). Load average 8-11 during the run (24h fuzz job on cores 3,19): wall noise ~2-3 %.

| workload | C# ms | IL0 wall | IL wall | IL cycles | IL instr | what changed (IL side) |
|---|---:|---:|---:|---:|---:|---|
| bfs | 9404 | 0.784 | 0.784 | 0.785 | 0.973 | R9 inlining, R2 (one access) |
| editdist | 10990 | 0.999 | 1.001 | 1.003 | 0.741 | R2: DP row accesses unchecked, overflow checks dropped (26 % fewer instructions, latency-bound) |
| gameoflife | 13750 | 0.987 | 0.992 | 0.992 | 1.003 | parity |
| hashmap | 1399 | 1.030 | 0.984 | 0.985 | 0.987 | R9 (`prng` into 2 sites), R2 |
| kmeans | 21788 | 0.995 | 1.017 | 1.002 | 1.000 | parity (cycles equal; wall noise) |
| lexer | 1982 | 0.956 | 0.941 | 0.939 | 0.984 | peepholes, R9 |
| mandelbrot | 3760 | 0.975 | 0.970 | 0.983 | 1.002 | parity: IL now forks like C# (`rcol` dispatcher + tiered `rcol__serial`), the earlier OPEN gap is gone |
| merkle | 8633 | 0.986 | 0.988 | 1.000 | 1.001 | R2 (44 accesses) |
| nbody | 7891 | 0.999 | 1.000 | 1.001 | 0.998 | parity |
| queens | 6340 | 1.016 | 1.021 | 1.004 | 1.000 | parity (cycles) |
| raytrace | 10137 | 1.184 | 0.979 | 0.992 | 0.972 | R9 forced inlining of `sphere_isect` (IL0: 39 % more instructions) |
| symreg | 9148 | 0.930 | 0.923 | 0.946 | 0.903 | type-test peephole (`isinst; stloc; ldloc; brfalse`) |
| terrain | 6088 | 0.990 | 0.987 | 0.977 | 0.803 | R2 + overflow checks (20 % fewer instructions) |
| tree-bitonic (21) | 20631 | 1.080 | 1.024 | 1.057 | 1.097 | OPEN: arena Alloc/Take sites (AirToIl.Arena.cs, ADR 0040), not an il-opt pass |
| tree-matmul | 9797 | 1.096 | 1.084* | 1.085* | 1.021 | OPEN: arena, as tree-bitonic. *rerun (cs 9941, IL 10772); the first run's 1.236 was an outlier: ablation `-inline`, `-peephole`, `-inbounds,-nooverflow` all within 1.5 % of IL0 |
| tree-radix | 15724 | 0.978 | 0.965 | 0.969 | 0.955 | arena + R9 |

Geometric mean of the IL wall ratios: 0.98 with il-opt (IL0: 1.00).

## Without tiering (`DOTNET_TieredCompilation=0`)

Every method compiled FullOpts at its first call, so the policy (AO, tier-0, OSR, PGO) drops out and only the IL
shape is compared. Same machine and date, medians of 2 runs after a checksum run (checksums identical); gameoflife
and tree-bitonic not run. IL / C# wall:

| workload | C# ms | IL0 | IL | | workload | C# ms | IL0 | IL |
|---|---:|---:|---:|---|---|---:|---:|---:|
| bfs | 9684 | 0.776 | 0.787 | | nbody | 8304 | 0.985 | 0.982 |
| editdist | 11231 | 0.998 | 1.001 | | queens | 6757 | 0.991 | 0.987 |
| hashmap | 1424 | 0.975 | 0.976 | | raytrace | 13866 | 0.870 | 0.723 |
| kmeans | 22128 | 1.000 | 1.000 | | symreg | 9811 | 0.990 | 0.979 |
| lexer | 2023 | 0.991 | 0.967 | | terrain | 6026 | 1.096 | 0.989 |
| mandelbrot | 6100 | 1.002 | 0.986 | | tree-matmul | 12051 | 1.059 | 1.057 |
| merkle | 8751 | 0.958 | 0.989 | | tree-radix | 16284 | 1.033 | 0.967 |

## Gaps measured and left open

- Arena tree benchmarks (tree-bitonic, tree-matmul): perf samples spread over every method with Alloc/Take sites
  (warp_zip +16 %, warp_leaf +20 % samples vs C#), runtime `DropRec` equal. IL's inlined Alloc calls `RawLive`
  (Locate + clear freed bit) on the bump path too, where the runtime skips it below `Dirty`: removing it measured
  ~2 % on tree-bitonic (prototype, not landed; needs a public raw field). Owner: affine-lower (ADR 0040).
- editdist: 26 % fewer instructions, same cycles: the DP loop is bound by the row dependency, not by checks.

## Rules measured

- Type tests: `isinst T; stloc p; ldloc p; brfalse` (Roslyn's `e is T p`) instead of `isinst; brfalse; castclass`:
  symreg 5 runs 0.945 wall / 0.934 cycles, queens 0.959, others neutral. Test:
  `IlOptTests.Type_tests_branch_on_the_reference_and_constant_arms_vanish`.
- AO per shape (R10): adding AO to every loop body the hot policy leaves tiered (`+aoloops`) is not a win
  (queens 1.098 wall, 1.24x instructions: PGO lost; kmeans/nbody within 2 %). Steady state on benchmarks/alice
  (`.ilopt/suiterun.sh`): the hot policy (AO on kernels, nests, owned-array loops) vs no AO: matmul 160.5 vs 226.3 ms,
  transpose 14.7 vs 33.4, mandelbrot 56.6 vs 79.1, pipeline 6.1 vs 9.6; dropping AO on loop-free leaves only is
  neutral (expr 12.4 vs 12.2, nbody 47.7 vs 44.0). Rule: keep main's policy; leaves, loops-calling-helpers: unchanged.
  Test: `AoLoops_adds_AggressiveOptimization_to_loop_bodies_only`.
- Fork kernels: since the IL backend lowers forks like the C# backend (dispatcher + tiered `__serial` clone), the
  earlier `+noaoforks` switch is gone; fork dispatchers and `__fork_*` helpers are never forced inline. Test:
  `Fork_kernels_take_the_parallel_form_and_are_not_forced_inline`.
