# bend2 serial: IL backend vs C# backend (interim, il-opt)

Method: `taskset -c 13,29 env ALICE_PAR=0`, cold process runs, tags interleaved and rotated per round
(`.ilopt/rot2.sh`), medians of 3 runs; `perf stat -e cycles:u,instructions:u`. Same runtime and runtimeconfig.
The box was heavily loaded (load average 12-23, wall times 1.5-3x the earlier bend2 numbers), so wall ratios carry
~5 % noise; cycles and retired instructions are the steadier columns. Ratio = IL / C# (< 1 is IL faster).

| workload | C# ms | IL ms | wall | cycles | instr | what changed (IL side) |
|---|---:|---:|---:|---:|---:|---|
| bfs | 12751 | 10596 | 0.831 | 0.832 | 0.973 | R9 inlining, proven-in-bounds (R2) |
| editdist | 12393 | 12515 | 1.010 | 0.986 | 0.741 | R2: 4 DP row accesses unchecked, overflow checks dropped |
| gameoflife | 24121 | 23680 | 0.982 | 0.985 | 1.003 | R2 |
| hashmap | 2918 | 2759 | 0.945 | 0.995 | 0.986 | R9 (`prng` into 2 sites) |
| kmeans | 70162 | 68469 | 0.976 | 0.993 | 1.000 | parity: `chunk` Tier1 machine code identical to C# (2193 B) |
| lexer | 3314 | 3096 | 0.934 | 0.936 | 0.981 | peepholes, R9 |
| mandelbrot | 6105 | 6502 | 1.065 | 1.057 | 0.998 | OPEN: same instructions; `rcol` is AO/FullOpts in IL, tiered `rcol__serial` in C# (NoAoForks under test) |
| merkle | 11360 | 11349 | 0.999 | 0.996 | 0.991 | R2 |
| nbody | 9188 | 9051 | 0.985 | 0.995 | 0.998 | parity |
| queens | 8218 | 8258 | 1.005 | 0.981 | 1.000 | parity (noise) |
| raytrace | 15270 | 15158 | 0.993 | 0.976 | 0.965 | R9 forced inlining of `sphere_isect` (earlier run: 0.86) |
| symreg | 7644 | 6808 | 0.891 | 0.953 | 0.998 | new type-test peephole: `expr_eval` Tier1 503 -> 356 B (C# size) |
| terrain | 8680 | 10055 | 1.158 | 1.020 | 0.803 | OPEN: 20 % fewer instructions; wall outlier run (r1: 0.830) |
| tree-bitonic | 244168 | - | - | - | - | not finished in this run (r1: 1.033 wall, 1.058 cycles) |
| tree-matmul | 27643 | 26937 | 0.974 | 0.991 | 0.994 | type-test peephole |
| tree-radix | 25029 | 25543 | 1.021 | 1.021 | 0.973 | OPEN |

## Rules measured

- Type tests: `isinst T; stloc p; ldloc p; brfalse` (Roslyn's `e is T p`) instead of `isinst; brfalse; castclass`:
  symreg 5 runs 0.945 wall / 0.934 cycles, queens 0.959, others neutral. Test:
  `IlOptTests.Type_tests_branch_on_the_reference_and_constant_arms_vanish`.
- AO per shape (R10): adding AO to every loop body the hot policy leaves tiered (`+aoloops`) is not a win
  (queens 1.098 wall, 1.24x instructions: PGO lost; kmeans/nbody within 2 %). Steady state on benchmarks/alice
  (`.ilopt/suiterun.sh`): the hot policy (AO on kernels, nests, owned-array loops) vs no AO: matmul 160.5 vs 226.3 ms,
  transpose 14.7 vs 33.4, mandelbrot 56.6 vs 79.1, pipeline 6.1 vs 9.6; dropping AO on loop-free leaves only is
  neutral (expr 12.4 vs 12.2, nbody 47.7 vs 44.0). Rule: keep main's policy; leaves, loops-calling-helpers: unchanged.
  Tests: `AoLoops_adds_AggressiveOptimization_to_loop_bodies_only`, `NoAoForks_drops_AggressiveOptimization_on_tree_recursion_only`.
