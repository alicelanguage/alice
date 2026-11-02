# Arena micro-benchmark (ADR 0037, task P1.1)

`tree(d)`: build a perfect binary tree of depth `d` with leaves `2^d .. 2^(d+1)-1` and sum it (checksum mod 2^32;
`d = 23` gives 4290772992). Rows: sealed records (what `enum Tree` lowers to today), `Arena<Pair>` with a consuming
sum (Take frees each node), with a borrowed sum, and the parallel arena version that forks the top 5 levels
(`Par.Fork`, each half on its own lane). Every arena run is one `ArenaRegion`.

```bash
dotnet run -c Release --project benchmarks/arena -- 23 7     # depth, repetitions
```

Exit code 1 when a checksum is wrong or the best serial arena run exceeds 220 ms at depth 23.
