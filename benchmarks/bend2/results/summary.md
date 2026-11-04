# Bend 2 runtime suite: results

## Re-run on 1.0.1 (2026-11-04, commit b2e41c8 (v1.0.1))

Machine: the forge, AWS c7i.8xlarge, Intel Xeon Platinum 8488C (AVX-512), 32 vCPU, 61 GiB, Arch Linux (kernel 7.2.11), .NET SDK 10.0.114 (runtime 10.0.14), clang 23.1.4 with libomp. Shared box: a fuzz job on cpus 29-31 for the whole run and other agents' builds; load average 8.0-18.3 (run 1: C, C# images, idiomatic) and 8.0-8.8 (run 2: IL vs C# images).

Method: `run.sh`'s commands, split in two runs. Serial = `taskset -c 11,27` (one physical core, both hyperthreads), `ALICE_PAR=0`, hyperfine `-N -w 1 -r 5`, median ms (max), peak RSS MB (median). Run 1 measured `alice build` images (`alice/out`), the same sources built with `--backend csharp` (`alice/out-cs`), the C twins and the idiomatic shapes, interleaved per workload. Run 2 measured the IL backend images (`alice emit --il`, `alice/out-il`) interleaved with the `alice build` images.

**Backend note.** In 1.0.0 and 1.0.1 neither `alice build <file.al>` nor `alice run <file.al>` used the IL backend: the CLI resolved `--backend` / `ALICE_BACKEND` and dropped the result (src/Alice.Cli/Commands/CompileCommands.cs), so both compiled through the C# backend (Roslyn) and only `alice emit --il` reached the IL backend, the 1.0 default. So `alice/out` and `alice/out-cs` are the same backend (their serial medians agree: geomean 0.995), and every earlier table of this file built by `run.sh` before 1.0.2 measured the C# backend. The **Alice (IL)** column below is the `alice emit --il` image; the C# column is `alice build` in 1.0.1. From 1.0.2 `alice build` and `alice run` use the IL backend by default (`--backend csharp` for C#), and `run.sh` measures IL. Confirmed on 2026-11-04 (1.0.2 candidate, same pair 11,27, hyperfine `-N -w 1 -r 5`, load average 8.9-9.6): the 1.0.2 `alice build` images are byte-identical to the `alice emit --il` ones and run editdist 8221 ms, nbody 8073 ms, tree-matmul 10407 ms (IL column: 8204, 8066, 10424); `alice build --backend csharp` runs 11237, 8058, 9644 ms (C# column: 11210, 8062, 9750). Checksums match C.

Alice startup floor (`bfs 0`, IL image, serial): 27 ms. Raw files: [1.0.1/](1.0.1/).

### Serial, 1 core

| workload | Alice (IL) ms | Alice (C# backend) ms | C ms | Alice / C | Alice RSS | C RSS | Bend / C on M4 (reference) |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 7526 (7555) | 9573 (9590) | 5681 (5713) | 1.32 | 39 | 3 | 1.09 |
| editdist | 8204 (8238) | 11210 (11252) | 9150 (9201) | 0.90 | 40 | 3 | 1.28 |
| gameoflife | 13818 (13850) | 14022 (14048) | 15018 (15074) | 0.92 | 40 | 3 | 1.15 |
| hashmap | 1406 (1413) | 1426 (1427) | 1304 (1317) | 1.08 | 39 | 3 | 4.41 |
| kmeans | 22126 (22202) | 22125 (22163) | 2931 (3400) | 7.55 | 75 | 6 | 1.00 |
| lexer | 1850 (1859) | 1970 (1979) | 1440 (1442) | 1.28 | 40 | 3 | 2.07 |
| mandelbrot | 3577 (3608) | 3589 (3603) | 2288 (2298) | 1.56 | 37 | 3 | 1.19 |
| merkle | 8414 (8445) | 8420 (8453) | 6366 (6371) | 1.32 | 137 | 130 | 1.23 |
| nbody | 8066 (8098) | 8062 (8089) | 8642 (8970) | 0.93 | 39 | 3 | 1.12 |
| queens | 6456 (6500) | 6431 (6440) | 5610 (5706) | 1.15 | 37 | 3 | 1.50 |
| raytrace | 9917 (9939) | 9999 (10025) | 8048 (8074) | 1.23 | 38 | 3 | 1.27 |
| symreg | 7926 (8309) | 8331 (8361) | 4914 (4917) | 1.61 | 40 | 3 | 1.37 |
| terrain | 5909 (5933) | 6050 (6053) | 2829 (2832) | 2.09 | 39 | 3 | 1.01 |
| tree-bitonic | 127188 (129884) | 120122 (120396) | 11335 (11484) | 11.22 | 366 | 130 | 1.06 |
| tree-matmul | 10424 (10616) | 9750 (9856) | 5525 (5529) | 1.89 | 44 | 6 | 0.70 |
| tree-radix | 14831 (15181) | 14962 (15504) | 7014 (7527) | 2.11 | 411 | 214 | 1.46 |

Geomean Alice (IL) / C over the 16 workloads: 1.70. C and the IL image come from two runs on the same pair (run 1 and run 2); the C# column is from run 2.

### Parallel, 32 cores

Not re-run yet: the parallel runs need the box idle (load average below 6), and the fuzz job alone keeps it near 7 until 2026-11-05 02:00 UTC. The parallel tables under "Previous runs" are from 2026-11-03 (Alice 1.0.0 candidate, C# backend).

### Idiomatic shape (idiomatic/<name>.al, `alice build`, C# backend)

| workload | serial ms | serial / 1:1 port (same backend) |
|---|---:|---:|
| symreg | 3091 | 0.35 |
| tree-bitonic | 4375 | 0.04 |
| tree-matmul | 2317 | 0.24 |
| tree-radix | 3770 | 0.25 |

### Checksums

| workload | IL | C# (`alice build`) | C# (`--backend csharp`) | C | idiomatic | match |
|---|---|---|---|---|---|---|
| bfs | 651176970 | 651176970 | 651176970 | 651176970 | - | yes |
| editdist | 2229810577 | 2229810577 | 2229810577 | 2229810577 | - | yes |
| gameoflife | 2016151040 | 2016151040 | 2016151040 | 2016151040 | - | yes |
| hashmap | 1307803744 | 1307803744 | 1307803744 | 1307803744 | - | yes |
| kmeans | 1616398086 | 1616398086 | 1616398086 | 1616398086 | - | yes |
| lexer | 2401049475 | 2401049475 | 2401049475 | 2401049475 | - | yes |
| mandelbrot | 3101455856 | 3101455856 | 3101455856 | 3101455856 | - | yes |
| merkle | 3104235417 | 3104235417 | 3104235417 | 3104235417 | - | yes |
| nbody | 3516450380 | 3516450380 | 3516450380 | 3516450380 | - | yes |
| queens | 2063750025 | 2063750025 | 2063750025 | 2063750025 | - | yes |
| raytrace | 1924309504 | 1924309504 | 1924309504 | 1924309504 | - | yes |
| symreg | 2383953211 | 2383953211 | 2383953211 | 2383953211 | 2383953211 | yes |
| terrain | 2572468224 | 2572468224 | 2572468224 | 2572468224 | - | yes |
| tree-bitonic | 3787129428 | 3787129428 | 3787129428 | 3787129428 | 3787129428 | yes |
| tree-matmul | 3797651056 | 3797651056 | 3797651056 | 3797651056 | 3797651056 | yes |
| tree-radix | 1998173798 | 1998173798 | 1998173798 | 1998173798 | 1998173798 | yes |

Every configuration printed the checksum of Bend's C twin.

### Gaps vs C (serial, IL image)

Workloads where Alice/C > 1.2, with named causes from profiling and ablation studies (ROADMAP 1.0 criterion):

- **tree-bitonic (11.22)**: recursive DropDeep (arena.md: 3.2x vs shallow Drop), inlined Alloc/Take sites; the IL image is 6% slower than the C# one here. Tracked: #767 arena prefetch.
- **kmeans (7.55)**: no SIMD for the 8-distance kernel (clang SLP-vectorizes it, RyuJIT stays scalar); GC stats trees vs C's arena. Tracked: 1.1 kmeans SoA kernel.
- **tree-radix (2.11)**: recursive DropDeep, inlined Alloc/Take. Tracked: #767 arena prefetch.
- **terrain (2.09)**: clang vectorizes the noise fill (1.5x faster with `-fno-vectorize`); bounds-checked neighbour loads in the Gauss-Seidel sweep. Tracked: 1.1 vectorization.
- **tree-matmul (1.89)**: recursive DropDeep (arena.md: 1.45x vs shallow Drop), inlined Alloc/Take; the IL image is 7% slower than the C# one. Tracked: #767 arena prefetch.
- **symreg (1.61)**: arena overhead (records are faster: 6371 vs 8399 ms in arena.md, 1.0.1 run). Cause not identified; tracked for 1.1.
- **mandelbrot (1.56)**: clang unrolls `mit` by 3 and knows the 51 iterations; Alice passes `tn` as a parameter. Tracked: 1.1 unroller tuning.
- **bfs (1.32)**: bounds checks on pooled arrays (no provable length), checked to_i32/to_u32 conversions. Tracked: 1.1 pooled-array shape.
- **merkle (1.32)**: Speck loop unrolled (fixed), gap remains; 8M GC objects vs C's arena. Tracked: 1.1 arena shape.
- **lexer (1.28)**: bounds checks on `buf[p]` measured as no cost; pooling overhead ~3%. Cause not identified; tracked for 1.1.
- **raytrace (1.23)**: sphere table bounds-checked where C folds it into immediates. Tracked: 1.1 constant propagation.

## Previous runs

The sections below are the earlier runs (2026-11-02/03), kept as published.

Median of cold-process wall time in ms (max in parentheses), peak RSS in MB (median). Serial = 1 core (`taskset -c 14,30`: one physical core with both hyperthreads; `ALICE_PAR=0` for Alice), parallel = all 32 cores. Bend columns are the pinned numbers of bend/bench/runtime/_pin_/apple_m4_max.txt: **a different machine (Apple M4 Max, 16 cores)**, for reference only; compare them through the X/C ratios, never directly.

Alice startup floor (`bfs 0`, smallest input): serial 26 ms, parallel 29 ms.

## Serial, 1 core

| workload | Alice ms | C ms | Alice / C | Alice RSS | C RSS | Bend M4 ms (different machine) | Bend / C on M4 |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 9383 (9423) | 5559 (5587) | 1.69 | 39 | 3 | 3918 | 1.09 |
| editdist | 11842 (12609) | 9290 (9724) | 1.27 | 39 | 3 | 2559 | 1.28 |
| gameoflife | 15420 (15676) | 14542 (14940) | 1.06 | 40 | 3 | 7803 | 1.15 |
| hashmap | 1447 (1459) | 1270 (1285) | 1.14 | 39 | 3 | 2741 | 4.41 |
| kmeans | 21706 (22510) | 2696 (2749) | 8.05 | 75 | 6 | 2069 | 1.00 |
| lexer | 1925 (1935) | 1403 (1409) | 1.37 | 40 | 3 | 2144 | 2.07 |
| mandelbrot | 3517 (3520) | 2238 (2245) | 1.57 | 37 | 3 | 4473 | 1.19 |
| merkle | 8250 (8286) | 6225 (6252) | 1.33 | 137 | 130 | 4222 | 1.23 |
| nbody | 7910 (7945) | 8414 (8653) | 0.94 | 38 | 3 | 5758 | 1.12 |
| queens | 6343 (6388) | 5457 (5473) | 1.16 | 37 | 3 | 5406 | 1.50 |
| raytrace | 9792 (9829) | 7480 (7491) | 1.31 | 38 | 3 | 4616 | 1.27 |
| symreg | 8172 (8233) | 4576 (4597) | 1.79 | 39 | 3 | 3014 | 1.37 |
| terrain | 5932 (5946) | 2636 (2728) | 2.25 | 39 | 3 | 2225 | 1.01 |
| tree-bitonic | 120030 (124650) | 11062 (11549) | 10.85 | 364 | 130 | 6043 | 1.06 |
| tree-matmul | 9544 (9689) | 5427 (5507) | 1.76 | 43 | 6 | 2064 | 0.70 |
| tree-radix | 15039 (15365) | 7343 (7811) | 2.05 | 409 | 214 | 4083 | 1.46 |

## Parallel, 32 cores

| workload | Alice ms | C ms | Alice / C | Alice RSS | C RSS | Bend M4 ms (different machine) | Bend / C on M4 |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 583 (585) | 279 (283) | 2.09 | 39 | 5 | 343 | 0.10 |
| editdist | 639 (650) | 527 (535) | 1.21 | 39 | 5 | 236 | 0.12 |
| gameoflife | 1118 (1136) | 745 (852) | 1.50 | 41 | 5 | 647 | 0.10 |
| hashmap | 125 (130) | 97 (119) | 1.28 | 41 | 10 | 238 | 0.38 |
| kmeans | 2463 (2568) | 256 (279) | 9.63 | 199 | 197 | 268 | 0.13 |
| lexer | 176 (182) | 160 (293) | 1.10 | 39 | 5 | 198 | 0.19 |
| mandelbrot | 312 (321) | 168 (172) | 1.86 | 37 | 5 | 395 | 0.11 |
| merkle | 941 (975) | 424 (437) | 2.22 | 183 | 134 | 422 | 0.12 |
| nbody | 790 (860) | 8397 (8418) | 0.09 | 38 | 3 | 516 | 0.10 |
| queens | 701 (760) | 5463 (5608) | 0.13 | 38 | 3 | 455 | 0.13 |
| raytrace | 757 (779) | 7587 (8073) | 0.10 | 38 | 3 | 396 | 0.11 |
| symreg | 877 (880) | 4573 (4575) | 0.19 | 62 | 3 | 266 | 0.12 |
| terrain | 488 (496) | 2650 (2671) | 0.18 | 38 | 3 | 197 | 0.09 |
| tree-bitonic | 5723 (5899) | 11047 (11104) | 0.52 | 3457 | 130 | 799 | 0.14 |
| tree-matmul | 1252 (1389) | 5517 (5544) | 0.23 | 70 | 6 | 226 | 0.08 |
| tree-radix | 1174 (1371) | 7186 (7725) | 0.16 | 1116 | 214 | 445 | 0.16 |

## Parallel speedup (serial median / parallel median)

| workload | Alice | C (OpenMP) | Bend on M4 (16 cores, different machine) |
|---|---:|---:|---:|
| bfs | 16.1x | 19.9x | 11.4x |
| editdist | 18.5x | 17.6x | 10.8x |
| gameoflife | 13.8x | 19.5x | 12.1x |
| hashmap | 11.6x | 13.1x | 11.5x |
| kmeans | 8.8x | 10.5x | 7.7x |
| lexer | 10.9x | 8.8x | 10.8x |
| mandelbrot | 11.3x | 13.3x | 11.3x |
| merkle | 8.8x | 14.7x | 10.0x |
| nbody | 10.0x | 1.0x | 11.2x |
| queens | 9.1x | 1.0x | 11.9x |
| raytrace | 12.9x | 1.0x | 11.7x |
| symreg | 9.3x | 1.0x | 11.3x |
| terrain | 12.1x | 1.0x | 11.3x |
| tree-bitonic | 21.0x | 1.0x | 7.6x |
| tree-matmul | 7.6x | 1.0x | 9.1x |
| tree-radix | 12.8x | 1.0x | 9.2x |

## Alice against Bend, machine-independent

Each side divided by the serial C twin on its own machine (lower is better). `Alice ahead` means Alice/C here is below Bend/C on the M4.

| workload | serial: Alice / C | serial: Bend / C | parallel: Alice / C serial | parallel: Bend / C serial | serial | parallel |
|---|---:|---:|---:|---:|---|---|
| bfs | 1.69 | 1.09 | 0.105 | 0.096 | Bend ahead | Bend ahead |
| editdist | 1.27 | 1.28 | 0.069 | 0.118 | Alice ahead | Alice ahead |
| gameoflife | 1.06 | 1.15 | 0.077 | 0.095 | Alice ahead | Alice ahead |
| hashmap | 1.14 | 4.41 | 0.098 | 0.383 | Alice ahead | Alice ahead |
| kmeans | 8.05 | 1.00 | 0.913 | 0.130 | Bend ahead | Bend ahead |
| lexer | 1.37 | 2.07 | 0.125 | 0.191 | Alice ahead | Alice ahead |
| mandelbrot | 1.57 | 1.19 | 0.140 | 0.105 | Bend ahead | Bend ahead |
| merkle | 1.33 | 1.23 | 0.151 | 0.123 | Bend ahead | Bend ahead |
| nbody | 0.94 | 1.12 | 0.094 | 0.101 | Alice ahead | Alice ahead |
| queens | 1.16 | 1.50 | 0.128 | 0.126 | Alice ahead | Bend ahead |
| raytrace | 1.31 | 1.27 | 0.101 | 0.109 | Bend ahead | Alice ahead |
| symreg | 1.79 | 1.37 | 0.192 | 0.120 | Bend ahead | Bend ahead |
| terrain | 2.25 | 1.01 | 0.185 | 0.090 | Bend ahead | Bend ahead |
| tree-bitonic | 10.85 | 1.06 | 0.517 | 0.140 | Bend ahead | Bend ahead |
| tree-matmul | 1.76 | 0.70 | 0.231 | 0.076 | Bend ahead | Bend ahead |
| tree-radix | 2.05 | 1.46 | 0.160 | 0.159 | Bend ahead | Bend ahead |

## Idiomatic shape (idiomatic/<name>.al, ADR 0039)

| workload | serial ms | serial / 1:1 port | parallel ms | parallel / 1:1 port | RSS serial |
|---|---:|---:|---:|---:|---:|
| symreg | 2910 | 0.36 | 433 | 0.49 | 38 |
| tree-bitonic | 4288 | 0.04 | 458 | 0.08 | 283 |
| tree-matmul | 2265 | 0.24 | 338 | 0.27 | 76 |
| tree-radix | 3651 | 0.24 | 685 | 0.58 | 295 |

## Checksums

| workload | Alice serial | Alice parallel | C serial | C parallel | match |
|---|---|---|---|---|---|
| bfs | 651176970 | 651176970 | 651176970 | 651176970 | yes |
| editdist | 2229810577 | 2229810577 | 2229810577 | 2229810577 | yes |
| gameoflife | 2016151040 | 2016151040 | 2016151040 | 2016151040 | yes |
| hashmap | 1307803744 | 1307803744 | 1307803744 | 1307803744 | yes |
| kmeans | 1616398086 | 1616398086 | 1616398086 | 1616398086 | yes |
| lexer | 2401049475 | 2401049475 | 2401049475 | 2401049475 | yes |
| mandelbrot | 3101455856 | 3101455856 | 3101455856 | 3101455856 | yes |
| merkle | 3104235417 | 3104235417 | 3104235417 | 3104235417 | yes |
| nbody | 3516450380 | 3516450380 | 3516450380 | 3516450380 | yes |
| queens | 2063750025 | 2063750025 | 2063750025 | 2063750025 | yes |
| raytrace | 1924309504 | 1924309504 | 1924309504 | 1924309504 | yes |
| symreg | 2383953211 | 2383953211 | 2383953211 | 2383953211 | yes |
| terrain | 2572468224 | 2572468224 | 2572468224 | 2572468224 | yes |
| tree-bitonic | 3787129428 | 3787129428 | 3787129428 | 3787129428 | yes |
| tree-matmul | 3797651056 | 3797651056 | 3797651056 | 3797651056 | yes |
| tree-radix | 1998173798 | 1998173798 | 1998173798 | 1998173798 | yes |
## Gaps vs C (serial)

Workloads where Alice/C > 1.2, with named causes from profiling and ablation studies (ROADMAP 1.0 criterion):

- **kmeans (8.05)**: no SIMD for 8-distance kernel (clang SLP vectorization, RyuJIT scalar); GC stats trees vs C arena. Tracked: #1.1 kmeans SoA kernel.
- **tree-bitonic (10.85)**: recursive DropDeep (arena.md: 3.2x vs shallow Drop), inlined Alloc/Take sites. Tracked: #767 arena prefetch.
- **terrain (2.25)**: clang vectorizes noise fill (1.5x faster with `-fno-vectorize`); bounds-checked neighbour loads in Gauss-Seidel sweep. Tracked: 1.1 vectorization.
- **tree-radix (2.05)**: recursive DropDeep, inlined Alloc/Take. Tracked: #767 arena prefetch.
- **symreg (1.79)**: arena overhead (slower than records: 12298 vs 9778 ms in arena.md). Cause not yet identified; tracked for 1.1.
- **tree-matmul (1.76)**: recursive DropDeep (arena.md: 1.45x vs shallow Drop), inlined Alloc/Take. Tracked: #767 arena prefetch.
- **bfs (1.69)**: bounds checks on pooled arrays (no provable length), checked to_i32/to_u32 conversions. Tracked: 1.1 pooled-array shape.
- **mandelbrot (1.57)**: RyuJIT branches vs clang cmov for escape freeze; clang unrolls `mit` by 3. Tracked: 1.1 unroller tuning.
- **lexer (1.37)**: bounds checks on `buf[p]` measured as no cost; pooling overhead ~3%. Cause not yet identified; tracked for 1.1.
- **merkle (1.33)**: Speck loop unrolled (fixed), but gap remains; 8M GC objects (296 MB) vs C arena (130 MB). Tracked: 1.1 arena shape.
- **raytrace (1.31)**: sphere table bounds-checked vs C constant folding; 2^26 fork leaves. Tracked: 1.1 constant propagation.
- **editdist (1.27)**: loop-carried store forwarding opportunity (il-vs-csharp.md: IL 0.75 with forwarding). Tracked: #808 store-forwarding.

