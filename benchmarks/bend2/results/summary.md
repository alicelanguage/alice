# Bend 2 runtime suite: results

Median of cold-process wall time in ms (max in parentheses), peak RSS in MB (median). Serial = 1 core (`taskset`, `DOTNET_PROCESSOR_COUNT=1` for Alice), parallel = all 32 cores. Bend columns are the pinned numbers of bend/bench/runtime/_pin_/apple_m4_max.txt: **a different machine (Apple M4 Max, 16 cores)**, for reference only; compare them through the X/C ratios, never directly.

Alice startup floor (`bfs 0`, smallest input): serial 26 ms, parallel 43 ms.

## Serial, 1 core

| workload | Alice ms | C ms | Alice / C | Alice RSS | C RSS | Bend M4 ms (different machine) | Bend / C on M4 |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 10843 (12234) | 6441 (7254) | 1.68 | 38 | 3 | 3918 | 1.09 |
| editdist | 11348 (12154) | 8774 (9103) | 1.29 | 37 | 3 | 2559 | 1.28 |
| gameoflife | 14296 (15400) | 15807 (16337) | 0.90 | 39 | 3 | 7803 | 1.15 |
| hashmap | 1464 (1493) | 1426 (1538) | 1.03 | 37 | 3 | 2741 | 4.41 |
| kmeans | 32904 (34470) | 2809 (3478) | 11.71 | 54 | 5 | 2069 | 1.00 |
| lexer | 2097 (2295) | 1513 (1644) | 1.39 | 38 | 3 | 2144 | 2.07 |
| mandelbrot | 3712 (4073) | 2462 (2700) | 1.51 | 36 | 3 | 4473 | 1.19 |
| merkle | 11963 (12486) | 6880 (7494) | 1.74 | 296 | 130 | 4222 | 1.23 |

## Parallel, 32 cores

| workload | Alice ms | C ms | Alice / C | Alice RSS | C RSS | Bend M4 ms (different machine) | Bend / C on M4 |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 839 (872) | 301 (326) | 2.79 | 40 | 5 | 343 | 0.10 |
| editdist | 726 (753) | 507 (556) | 1.43 | 39 | 5 | 236 | 0.12 |
| gameoflife | 1123 (1257) | 637 (648) | 1.76 | 41 | 5 | 647 | 0.10 |
| hashmap | 162 (262) | 87 (139) | 1.87 | 45 | 10 | 238 | 0.38 |
| kmeans | 2774 (3167) | 269 (310) | 10.30 | 84 | 189 | 268 | 0.13 |
| lexer | 201 (203) | 164 (206) | 1.23 | 40 | 5 | 198 | 0.19 |
| mandelbrot | 349 (410) | 183 (187) | 1.91 | 38 | 5 | 395 | 0.11 |
| merkle | 1120 (1295) | 510 (543) | 2.19 | 319 | 134 | 422 | 0.12 |

## Parallel speedup (serial median / parallel median)

| workload | Alice | C (OpenMP) | Bend on M4 (16 cores, different machine) |
|---|---:|---:|---:|
| bfs | 12.9x | 21.4x | 11.4x |
| editdist | 15.6x | 17.3x | 10.8x |
| gameoflife | 12.7x | 24.8x | 12.1x |
| hashmap | 9.0x | 16.4x | 11.5x |
| kmeans | 11.9x | 10.4x | 7.7x |
| lexer | 10.4x | 9.2x | 10.8x |
| mandelbrot | 10.6x | 13.5x | 11.3x |
| merkle | 10.7x | 13.5x | 10.0x |

## Alice against Bend, machine-independent

Each side divided by the serial C twin on its own machine (lower is better). `Alice ahead` means Alice/C here is below Bend/C on the M4.

| workload | serial: Alice / C | serial: Bend / C | parallel: Alice / C serial | parallel: Bend / C serial | serial | parallel |
|---|---:|---:|---:|---:|---|---|
| bfs | 1.68 | 1.09 | 0.130 | 0.096 | Bend ahead | Bend ahead |
| editdist | 1.29 | 1.28 | 0.083 | 0.118 | Bend ahead | Alice ahead |
| gameoflife | 0.90 | 1.15 | 0.071 | 0.095 | Alice ahead | Alice ahead |
| hashmap | 1.03 | 4.41 | 0.114 | 0.383 | Alice ahead | Alice ahead |
| kmeans | 11.71 | 1.00 | 0.987 | 0.130 | Bend ahead | Bend ahead |
| lexer | 1.39 | 2.07 | 0.133 | 0.191 | Alice ahead | Alice ahead |
| mandelbrot | 1.51 | 1.19 | 0.142 | 0.105 | Bend ahead | Bend ahead |
| merkle | 1.74 | 1.23 | 0.163 | 0.123 | Bend ahead | Bend ahead |

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
