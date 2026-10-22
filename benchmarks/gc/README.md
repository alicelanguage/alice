# GC benchmark: Alice vs C# on allocation-heavy code

Each workload is written three times: idiomatic Alice where ownership is inferred (`alice/`), the same Alice
with explicit `own` declarations (`alice-own/`), and idiomatic C# (`csharp/`). All three use the same algorithm
and allocate the same arrays and objects per iteration. Every program reports its median time over 7 runs and,
across those runs, the bytes it allocated, gen0/1/2 collections, total GC pause and peak working set.

```sh
benchmarks/gc/run.sh        # 3 rounds, alternating the variants; writes results/
benchmarks/gc/run.sh 5      # more rounds
```

Each round runs everything under workstation GC (`DOTNET_gcServer=0`) and under server GC (`DOTNET_gcServer=1`),
with the same setting for Alice and C#. `results/` holds `results.csv`, `results.md` and one SVG chart per
metric: `time`, `allocated`, `collections`, `pause` and `peak`.

| workload | what it allocates | what Alice does |
|---|---|---|
| `rows` | edit distance, a fresh `Int32` row per outer iteration (4 × 2000 rows) | `previous = row` is a move; the old row returns to the pool |
| `scratch` | a 512-element temporary per call, 300,000 calls | released at return and reused on the next call |
| `frames` | a double-buffered stencil over 4 MiB frames (large object heap), 120 frames | `cur = nxt` recycles the frame |
| `trees` | binary trees (objects) | none: objects stay with the GC on both sides |

`trees` is the control: neither ownership nor `own` applies to objects, so Alice and C# allocate the same and
should run at the same speed. It has no `alice-own/` version.

## Notes

- Outer driver loops are `while` loops, so that Alice does not parallelize them. The comparison measures
  allocation, not cores.
- `scratch` wraps its hash arithmetic in `unchecked`. Alice raises on integer overflow and C# wraps by default, so
  without it the Alice version would also be paying for overflow checks.
- `own` compiles to the same code as inferred ownership. What it adds is the compile-time guarantee: a change
  that makes an array escape becomes error AL0207 instead of a silent return to GC allocation.
- `own` cannot yet take an array returned from another function (`own src = source(n)` is AL0207). Such
  arrays stay with the GC; this is the 3.8 MiB that remains in `scratch`.
