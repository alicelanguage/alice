# Bend 2 runtime suite: results

Median of cold-process wall time in ms (max in parentheses), peak RSS in MB (median). Serial = 1 core (`taskset -c 14,30`: one physical core with both hyperthreads; `ALICE_PAR=0` for Alice), parallel = all 32 cores. Bend columns are the pinned numbers of bend/bench/runtime/_pin_/apple_m4_max.txt: **a different machine (Apple M4 Max, 16 cores)**, for reference only; compare them through the X/C ratios, never directly.

Alice startup floor (`bfs 0`, smallest input): serial 26 ms, parallel 39 ms.

## Serial, 1 core

| workload | Alice ms | C ms | Alice / C | Alice RSS | C RSS | Bend M4 ms (different machine) | Bend / C on M4 |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 9722 (9973) | 5549 (5580) | 1.75 | 39 | 3 | 3918 | 1.09 |
| editdist | 11156 (11211) | 8684 (8712) | 1.28 | 38 | 3 | 2559 | 1.28 |
| gameoflife | 13680 (13694) | 13797 (14158) | 0.99 | 40 | 3 | 7803 | 1.15 |
| hashmap | 1393 (1420) | 1206 (1206) | 1.15 | 39 | 3 | 2741 | 4.41 |
| kmeans | 29707 (30451) | 2688 (2713) | 11.05 | 41 | 6 | 2069 | 1.00 |
| lexer | 1924 (1955) | 1395 (1420) | 1.38 | 39 | 3 | 2144 | 2.07 |
| mandelbrot | 3419 (3430) | 2228 (2228) | 1.53 | 37 | 3 | 4473 | 1.19 |
| merkle | 8731 (8891) | 6551 (7869) | 1.33 | 303 | 130 | 4222 | 1.23 |
| nbody | 8119 (8357) | 8508 (8833) | 0.95 | 38 | 3 | 5758 | 1.12 |
| queens | 6345 (6481) | 5486 (5582) | 1.16 | 37 | 3 | 5406 | 1.50 |
| raytrace | 9794 (10227) | 8044 (9269) | 1.22 | 37 | 3 | 4616 | 1.27 |
| symreg | 6115 (6217) | 4631 (5216) | 1.32 | 40 | 3 | 3014 | 1.37 |
| terrain | 5821 (5847) | 2653 (2660) | 2.19 | 38 | 3 | 2225 | 1.01 |
| tree-bitonic | 135175 (170773) | 11378 (12129) | 11.88 | 2699 | 130 | 6043 | 1.06 |
| tree-matmul | 33256 (34142) | 5481 (5504) | 6.07 | 79 | 6 | 2064 | 0.70 |
| tree-radix | 25229 (26022) | 7809 (8572) | 3.23 | 1502 | 214 | 4083 | 1.46 |

## Parallel, 32 cores

| workload | Alice ms | C ms | Alice / C | Alice RSS | C RSS | Bend M4 ms (different machine) | Bend / C on M4 |
|---|---:|---:|---:|---:|---:|---:|---:|
| bfs | 806 (808) | 265 (268) | 3.04 | 40 | 5 | 343 | 0.10 |
| editdist | 693 (726) | 436 (442) | 1.59 | 39 | 5 | 236 | 0.12 |
| gameoflife | 956 (1016) | 588 (605) | 1.63 | 41 | 5 | 647 | 0.10 |
| hashmap | 126 (133) | 73 (77) | 1.73 | 43 | 10 | 238 | 0.38 |
| kmeans | 2386 (2576) | 249 (256) | 9.59 | 85 | 194 | 268 | 0.13 |
| lexer | 181 (187) | 127 (148) | 1.43 | 40 | 5 | 198 | 0.19 |
| mandelbrot | 294 (303) | 159 (161) | 1.85 | 38 | 5 | 395 | 0.11 |
| merkle | 1096 (1194) | 393 (395) | 2.79 | 321 | 134 | 422 | 0.12 |
| nbody | 607 (651) | 8406 (8463) | 0.07 | 39 | 3 | 516 | 0.10 |
| queens | 628 (768) | 6064 (6974) | 0.10 | 38 | 3 | 455 | 0.13 |
| raytrace | 739 (823) | 7624 (7836) | 0.10 | 38 | 3 | 396 | 0.11 |
| symreg | 820 (1002) | 4638 (4878) | 0.18 | 45 | 3 | 266 | 0.12 |
| terrain | 449 (456) | 2627 (2637) | 0.17 | 39 | 3 | 197 | 0.09 |
| tree-bitonic | 28850 (38037) | 11649 (12312) | 2.48 | 13681 | 130 | 799 | 0.14 |
| tree-matmul | 11243 (13098) | 5635 (5697) | 2.00 | 806 | 6 | 226 | 0.08 |
| tree-radix | 3200 (3579) | 7094 (7833) | 0.45 | 3601 | 214 | 445 | 0.16 |

## Parallel speedup (serial median / parallel median)

| workload | Alice | C (OpenMP) | Bend on M4 (16 cores, different machine) |
|---|---:|---:|---:|
| bfs | 12.1x | 21.0x | 11.4x |
| editdist | 16.1x | 19.9x | 10.8x |
| gameoflife | 14.3x | 23.4x | 12.1x |
| hashmap | 11.0x | 16.5x | 11.5x |
| kmeans | 12.4x | 10.8x | 7.7x |
| lexer | 10.6x | 11.0x | 10.8x |
| mandelbrot | 11.6x | 14.0x | 11.3x |
| merkle | 8.0x | 16.7x | 10.0x |
| nbody | 13.4x | 1.0x | 11.2x |
| queens | 10.1x | 0.9x | 11.9x |
| raytrace | 13.3x | 1.1x | 11.7x |
| symreg | 7.5x | 1.0x | 11.3x |
| terrain | 13.0x | 1.0x | 11.3x |
| tree-bitonic | 4.7x | 1.0x | 7.6x |
| tree-matmul | 3.0x | 1.0x | 9.1x |
| tree-radix | 7.9x | 1.1x | 9.2x |

## Alice against Bend, machine-independent

Each side divided by the serial C twin on its own machine (lower is better). `Alice ahead` means Alice/C here is below Bend/C on the M4.

| workload | serial: Alice / C | serial: Bend / C | parallel: Alice / C serial | parallel: Bend / C serial | serial | parallel |
|---|---:|---:|---:|---:|---|---|
| bfs | 1.75 | 1.09 | 0.145 | 0.096 | Bend ahead | Bend ahead |
| editdist | 1.28 | 1.28 | 0.080 | 0.118 | Bend ahead | Alice ahead |
| gameoflife | 0.99 | 1.15 | 0.069 | 0.095 | Alice ahead | Alice ahead |
| hashmap | 1.15 | 4.41 | 0.105 | 0.383 | Alice ahead | Alice ahead |
| kmeans | 11.05 | 1.00 | 0.888 | 0.130 | Bend ahead | Bend ahead |
| lexer | 1.38 | 2.07 | 0.130 | 0.191 | Alice ahead | Alice ahead |
| mandelbrot | 1.53 | 1.19 | 0.132 | 0.105 | Bend ahead | Bend ahead |
| merkle | 1.33 | 1.23 | 0.167 | 0.123 | Bend ahead | Bend ahead |
| nbody | 0.95 | 1.12 | 0.071 | 0.101 | Alice ahead | Alice ahead |
| queens | 1.16 | 1.50 | 0.115 | 0.126 | Alice ahead | Alice ahead |
| raytrace | 1.22 | 1.27 | 0.092 | 0.109 | Alice ahead | Alice ahead |
| symreg | 1.32 | 1.37 | 0.177 | 0.120 | Alice ahead | Bend ahead |
| terrain | 2.19 | 1.01 | 0.169 | 0.090 | Bend ahead | Bend ahead |
| tree-bitonic | 11.88 | 1.06 | 2.535 | 0.140 | Bend ahead | Bend ahead |
| tree-matmul | 6.07 | 0.70 | 2.051 | 0.076 | Bend ahead | Bend ahead |
| tree-radix | 3.23 | 1.46 | 0.410 | 0.159 | Bend ahead | Bend ahead |

## Idiomatic shape (idiomatic/<name>.al, ADR 0039)

| workload | serial ms | serial / 1:1 port | parallel ms | parallel / 1:1 port | RSS serial |
|---|---:|---:|---:|---:|---:|
| symreg | 2698 | 0.44 | 426 | 0.52 | 38 |
| tree-bitonic | 4433 | 0.03 | 489 | 0.02 | 255 |
| tree-matmul | 3844 | 0.12 | 317 | 0.03 | 42 |
| tree-radix | 4198 | 0.17 | 835 | 0.26 | 245 |

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
