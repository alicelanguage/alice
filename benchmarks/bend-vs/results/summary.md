# Results (median ms of cold-process wall time; max in parentheses)

## Startup floor (`pow2 1`)

| bend | alice | c | rust | csharp |
|---:|---:|---:|---:|---:|
| 0.9 | 26.7 | 0.3 | 0.6 | 21.7 |

## Serial, 1 core

| workload | bend | alice | c | rust | csharp | Bend/Alice |
|---|---:|---:|---:|---:|---:|---:|
| pow2 | 1541 (1572) | 1218 (1295) | 1 (1) | 1206 (1229) | 1414 (1452) | 1.26 |
| fib | 280 (281) | 625 (626) | 146 (148) | 245 (247) | 381 (384) | 0.45 |
| tree | 123 (127) | 1542 (1655) | 472 (473) | 774 (800) | 1430 (1451) | 0.08 |
| nqueens | 331 (335) | 620 (646) | 240 (240) | 357 (359) | 430 (436) | 0.53 |
| mandel | 672 (675) | 749 (754) | 673 (693) | 672 (676) | 1117 (1124) | 0.90 |
| collatz | 1159 (1172) | 2787 (2822) | 653 (656) | 695 (710) | 2537 (2546) | 0.42 |

## Parallel, 12 threads

| workload | bend | alice | c | rust | csharp | Bend/Alice |
|---|---:|---:|---:|---:|---:|---:|
| pow2 | 210 (226) | 193 (199) | 31 (33) | 158 (163) | 247 (262) | 1.09 |
| fib | 43 (45) | 140 (151) | 27 (29) | 42 (43) | 94 (104) | 0.31 |
| tree | 90 (101) | 681 (700) | 162 (176) | 565 (580) | 1019 (1156) | 0.13 |
| nqueens | 144 (150) | 282 (295) | 78 (78) | 111 (112) | 187 (193) | 0.51 |
| mandel | 100 (107) | 152 (160) | 121 (127) | 92 (98) | 218 (222) | 0.66 |
| collatz | 159 (162) | 356 (535) | 186 (200) | 83 (87) | 326 (339) | 0.45 |

## Parallel speedup (serial median / parallel median)

| workload | bend | alice | c | rust | csharp |
|---|---:|---:|---:|---:|---:|
| pow2 | 7.4x | 6.3x | 0.0x | 7.6x | 5.7x |
| fib | 6.5x | 4.5x | 5.5x | 5.8x | 4.1x |
| tree | 1.4x | 2.3x | 2.9x | 1.4x | 1.4x |
| nqueens | 2.3x | 2.2x | 3.1x | 3.2x | 2.3x |
| mandel | 6.7x | 4.9x | 5.5x | 7.3x | 5.1x |
| collatz | 7.3x | 7.8x | 3.5x | 8.4x | 7.8x |

## Startup-subtracted serial time (median - startup floor), ms

| workload | bend | alice | c | rust | csharp |
|---|---:|---:|---:|---:|---:|
| pow2 | 1540 | 1192 | 0 | 1206 | 1392 |
| fib | 279 | 599 | 146 | 244 | 359 |
| tree | 122 | 1515 | 471 | 773 | 1409 |
| nqueens | 330 | 594 | 239 | 356 | 408 |
| mandel | 671 | 722 | 673 | 671 | 1095 |
| collatz | 1158 | 2760 | 653 | 694 | 2515 |

## Peak RSS, MB (1 thread)

| workload | bend | alice | c | rust | csharp |
|---|---:|---:|---:|---:|---:|
| pow2 | 6 | 31 | 4 | 4 | 24 |
| fib | 6 | 32 | 4 | 4 | 24 |
| tree | 134 | 486 | 514 | 514 | 484 |
| nqueens | 6 | 31 | 4 | 4 | 24 |
| mandel | 6 | 32 | 4 | 4 | 25 |
| collatz | 6 | 32 | 4 | 4 | 24 |

## Peak RSS, MB (12 threads)

| workload | bend | alice | c | rust | csharp |
|---|---:|---:|---:|---:|---:|
| pow2 | 10 | 33 | 2 | 4 | 27 |
| fib | 10 | 33 | 2 | 4 | 26 |
| tree | 140 | 532 | 514 | 514 | 488 |
| nqueens | 10 | 33 | 2 | 4 | 27 |
| mandel | 10 | 33 | 2 | 4 | 26 |
| collatz | 10 | 33 | 2 | 4 | 27 |
