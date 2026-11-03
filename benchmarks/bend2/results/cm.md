# CM loop shapes (ADR 0039, P2.3): before / after

Wall time, median of 5 cold runs after 1 warm-up (`hyperfine -N -w 1 -r 5`), on the forge box (32 logical cpus).
**serial** = `taskset -c 5,21 env ALICE_PAR=0` (one physical core, both hyperthreads); **32** = all cores. The box
was shared with other workers during both sessions (load average 10-33 in session 2, 3-23 in session 1), so
differences under ~8% are within noise. Every configuration printed the reference checksum.

Programs (`benchmarks/bend2/idiomatic/cm/`, same checksum as the 1:1 port, *not the same algorithm*):

- **1:1**: `alice/<name>.al` (for tree-radix: the existing flat-trie `idiomatic/tree-radix.al`). Its emitted C# is
  byte-identical with and without this change (no 1:1 port contains a CM shape).
- **CM baseline**: `idiomatic/cm/<name>.al` built by main e99118c (no CM lowering; for terrain `cm for` replaced by
  `for`, which the baseline parses).
- **CM P2.3**: the same file built by this branch (CM lowering).

## Session 2 (final code: inferred shapes also require `Par.Workers > 1`)

| workload | shapes matched (P2.3) | 1:1 serial | 1:1 32 | CM baseline serial | CM baseline 32 | CM P2.3 serial | CM P2.3 32 |
|---|---|---:|---:|---:|---:|---:|---:|
| kmeans (2^19 points, 64 restarts x 20 rounds) | inferred combine, 3 targets on one key: `Histogram<uint>` (counts) + 2 `ScatterAdd<uint>` (sum x, sum y), fused (a map pass materializes b, x, y) | 23.912 | 1.815 | 16.883 | 1.705 | 15.725 | 2.076 |
| mandelbrot (4096^2, 51 iters) | inferred combine: `Histogram<uint>` over 65536 bins + 2 `ScatterAdd<uint>` (fused, one pass over the pixels); inferred scan `InclusiveScan<uint>` (65536-bin CDF, library form) | 3.780 | 0.387 | 1.800 | 1.781 | 1.950 | 0.244 |
| terrain (2^16 tiles, 5 passes) | `cm for` combine per tile: `Histogram<uint>` (64 bins, n = 4096) | 6.117 | 0.541 | 6.325 | 0.535 | 6.782 | 0.569 |
| tree-radix (2^22 keys) | `sort_by_key` (Prelude, `Cm.SortByKey`, unchanged) + inferred compaction `Compact<uint>` (distinct keys) | 3.842 | 0.705 | 0.190 | 0.118 | 0.188 | 0.126 |

## Session 1 (inferred shapes without the `Par.Workers > 1` guard)

| workload | 1:1 serial | 1:1 32 | CM baseline serial | CM baseline 32 | CM P2.3 serial | CM P2.3 32 |
|---|---:|---:|---:|---:|---:|---:|
| kmeans | 22.269 | 2.059 | 15.627 | 1.535 | 16.949 | 1.654 |
| mandelbrot | 3.597 | 0.316 | 1.726 | 1.727 | 1.846 | 0.214 |
| terrain | 6.119 | 0.499 | 6.196 | 0.513 | 6.400 | 0.550 |
| tree-radix | 3.787 | 0.764 | 0.201 | 0.114 | 0.221 | 0.120 |

## Reading

- **mandelbrot** is the shape the lowering is for. The pixel loop is not a `par for` (it writes tables at computed
  indices), so the baseline runs it serially at 32 cores (1.78 s). The CM combine runs it on all cores (0.24 s,
  7.3x), 1.6x faster than the 1:1 port with its two fork trees. Serially the CM program is 2.0x faster than the 1:1
  port because it computes each pixel once: that part comes from the program, not from the lowering.
- **kmeans**: the CM program is faster than the 1:1 port serially (one combine loop instead of 24 masked
  accumulators), but the lowering itself does not pay at 32 cores. The 64 restarts already fork across the cores,
  so the kernels find the pool busy and run inline after the extra map pass (+8% in session 1, +22% in session 2).
- **terrain**: a 4096-element combine per tile is below the kernel parallel cutoff (16K elements); `cm for` then
  costs the map pass and the scratch (+5-7%). The tiles already fork.
- **tree-radix**: the CM counting sort (`sort_by_key`, runtime from P2.2) is 20x (serial) and 6x (32) faster than
  the trie program; the inferred compaction is neutral (one pass over 4M keys).
- Serial pair, session 2: inferred shapes take the loop as written when `Par.Workers == 1` (`ALICE_PAR=0`), so the
  CM P2.3 serial column runs the same loops as the baseline (terrain's explicit `cm for` still runs the kernel).
  `KernelPool` (the CM kernels' dispatcher) sizes itself from `Environment.ProcessorCount`, not from `ALICE_PAR`:
  under `taskset -c 5,21` an explicit `cm for` kernel uses both hyperthreads.
