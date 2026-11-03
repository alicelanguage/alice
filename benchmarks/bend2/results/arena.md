# Affine arenas (ADR 0037 P1.3): bend2 1:1 ports

Serial only: pair `taskset -c 13,29`, `ALICE_PAR=0`, cold process, hyperfine `-N`, 1 warm-up + 3 runs (tree-bitonic: 1 run
in the second table). Median ms (max), peak RSS MB (median). The box was shared with other jobs (load 11-13), so C is
slower than in summary.md; compare ratios within a table only. Every measured run printed the C checksum.

Configurations: `arena` = sum types as arena terms with recursive Drop (the default); `shallow` = `ALICE_ARENA_DEEP=0`
(a Drop frees only the dead node); `records` = `ALICE_NO_ARENA=1` (the earlier class records). Bend / C is the M4 ratio
from summary.md (the target).

## Before the merge with main 39d1289 (default runtimeconfig)

| port | C | IL arena | IL shallow | IL records | C# arena | arena / C | Bend / C |
|---|---|---|---|---|---|---|---|
| tree 23 build+sum | 251 (253), 129 MB | 461 (531), 167 MB | 503 (520), 167 MB | 2733 (2834), 490 MB | 611 (618) | 1.84 | target 1.3 (C twin) |
| tree-radix | 12811 (13271), 214 MB | 25200 (26516), 401 MB | 21975 (23791), 407 MB | 31720 (35250), 1649 MB | 25126 (27124) | 1.97 | 1.46 |
| tree-matmul | 7088 (7163), 6 MB | 18984 (19082), 33 MB | 13077 (13207), 3708 MB | 40618 (40953), 92 MB | 18571 (55010) | 2.68 | 0.70 |
| merkle | 9246 (9568), 130 MB | 11814 (11986), 126 MB | 12117 (12333), 126 MB | 12432 (12529), 292 MB | 12244 (12319) | 1.28 | 1.23 |
| symreg | 6952 (6960), 3 MB | 12298 (12314), 35 MB | 12502 (12576), 152 MB | 9778 (9910), 85 MB | 12540 (12573) | 1.77 | 1.37 |
| tree-bitonic | 22347 (22457), 130 MB | 159061 (189453), 355 MB | 49948 (51788), 27184 MB | 386720 (n=1), 2950 MB | 220691 (250400) | 7.12 | 1.06 |

## Tiering: `System.Runtime.TieredCompilation.CallCountingDelayMs=0` in the runtimeconfig (IL, after the merge)

| port | C | arena | arena, delay 0 | shallow, delay 0 | records, delay 0 |
|---|---|---|---|---|---|
| tree 23 build+sum | 159 | 504 | 406 | 335 | 1773 |
| tree-radix | 7247 | 21435 | 21268 | 23001 | not run |
| tree-matmul | 10950 | 22181 | 21954 | 13242 | not run |
| merkle | 9816 | 12736 | 12209 | 11708 | 12642 |
| symreg | 7192 | 13917 | 13132 | 13442 | 9612 |
| tree-bitonic (n=1) | 19634 | 155704 | 138391 | 54720 (27190 MB) | 151064 |

Delay 0 helps the arena ports, but on pair 12,28 (5 runs, IL, records) kmeans went from 25106 to 26701 ms (+6.4%) and
hashmap from 1474 to 1502 ms; nbody went from 8711 to 8608. kmeans regresses, so the runtimeconfig is unchanged.

DropDeep with the lane slab read once (src/Alice.Runtime/Arena/Arena.cs DropRec): tree-bitonic 132431 to 127994 ms (n=1, delay 0).

## Reading

- Arenas beat records on every tree port (tree-radix 1.26x, tree-matmul 2.1x, tree-bitonic 2.4x, build+sum 5.9x) and
  cut peak RSS 2-8x. symreg is slower on arenas than on records (12298 vs 9778 ms): not explained yet.
- No port reaches its Bend / C target yet. merkle is closest (1.28 vs 1.23).
- A recursive Drop costs time where the program discards whole trees (tree-bitonic 3.2x, tree-matmul 1.45x vs shallow);
  shallow Drop leaks them instead (27 GB and 3.7 GB RSS).
- Not measured: 32 cores (script /tmp/al-arena/v3/par.sh on the forge, not run).
