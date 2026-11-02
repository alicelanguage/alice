# ADR 0039: Connection-Machine data-parallel primitives: scan, segmented scan, combine, compaction, permutation

Status: Accepted (direction; not yet implemented). Extends the admission rules of ADR 0015 (purity gate), ADR 0021
(`gpu for`) and ADR 0025 (affine subscripts). Design detail: `/work/reports/paradigms-plan.md` section 3.

`par for` (`Lowerer.LoopPar.cs`) proves only "iteration i writes `a[i]` or `a[i * w + j]` and may accumulate one
`+=`". It rejects the loop-carried `acc += a[i]; out[i] = acc` (scan), the indirect write `h[k[i]] += 1` (combine),
the compaction `if p then out[m] = a[i]; m += 1 end`, and `out[perm[i]] = a[i]` (permute): the common data-parallel
kernels after maps and reductions. Hillis's Connection Machine and Blelloch's scan model reduce every data-parallel
algorithm to elementwise operations plus exactly these communication primitives, each work-efficient on p << n
processors by chunking (local pass, scan of chunk totals, local fix-up).

Decision: add the CM primitives as runtime kernels, a Prelude surface, four inferred loop shapes and a `cm for`
contract, all deterministic.

## Surface

Prelude methods (no syntax): `xs.prefix_sum`, `xs.scan { |a, b| ... }`, `xs.segmented_sum(flags)`,
`keys.histogram(k)`, `xs.compact { |x| pred }`, `xs.scatter_add(idx, vals)`, `xs.permute(perm)`,
`keys.sort_by_key(vals)`.

Inferred (silent) shapes over a counted `for i in 0...n`, tried after `TryParallelFor` fails:

| Shape in the body | Kernel | Determinism |
|---|---|---|
| `acc += e(i); out[i] = acc` (or `acc = acc op e(i)`, `op` in `+ * & \| ^ min max`) | scan (inclusive / exclusive by statement order) | integers exact; floats fixed chunking by `n` |
| `h[k(i)] += v(i)`, `h` not otherwise read in the loop | combine (histogram / scatter-add) | private per-worker tables merged in index order; atomics for large tables, integers only |
| `if c(i) then out[m] = e(i); m += 1 end`, `m` written only there | compaction (flag scan + scatter) | exact |
| a `seg[i]` boundary + any of the above | segmented scan / reduce | exact for integers |

Explicit (ADR 0017 pattern):

```crystal
cm for i in 0...n                 # the body must be CM-admissible, or AL0247
  acc += xs[i]
  pre[i] = acc
end

cm for j in 0...keys.size         # histogram: private tables merged in order
  row[keys[j].clamp(0, 255)] += 1_u32
end
```

| Error | Meaning |
|---|---|
| AL0247 | `cm for` body is not one of the admissible shapes (names the offending statement) |
| AL0248 | combine or scan with a non-associative operator, or a float combine without `ordered` |
| AL0249 | `permute`/`scatter` target index not provably in range and distinct (under inference the runtime checks and raises; under `cm` the compiler demands a proof: `perm` from `sort_by_key`/`compact`, or an `@[Permutation]` contract) |

**Float contract.** Float scans and reductions use a chunking fixed by `n` (chunk size `max(4096, n / 1024)` rounded
to a vector multiple, independent of `Par.Workers` and of the core count). Results are bit-identical across core
counts and runs, and are documented as **not equal to the serial left fold**: `xs.prefix_sum` on `Float64` may differ
in the last bits from `for` + `acc +=`. Code that needs the serial bit pattern writes a plain `for`. This extends to
CPU scans what ADR 0021 accepted for GPU float sums, with a stronger guarantee (stable across machines' core counts).

## Pipeline

1. Shape matcher in new `Lowering/Lowerer.Cm.cs` over the `LoopPlan` built for `LoopPar`, plus the affine subscripts
   of `Optimizer/Analysis/Affine.cs` for `k(i)` and `seg[i]`. One dispatch line in `Lowerer.Statements.cs` `For`, after
   `TryParallelFor` and before the serial path (so proven-independent loops keep today's lowering). `PureExpr`
   (`Lowerer.Parallel.cs`) gates every `e(i)`, `k(i)`, `c(i)`. The matcher must classify every statement of the body,
   as `LoopBody` does, or it declines (AL0247 under `cm for`).
2. AIR nodes added to the ADR 0032 set: `AirScan(op, inclusive, src, dst, seg?)`, `AirCombine(op, target, idx, vals)`,
   `AirCompact(pred, src, dst, count)`, `AirPermute(src, perm, dst)`; each carries `Ordered` and the element
   `AirType`. Both printers emit runtime calls (library kernels, not inline code).
3. GPU (ADR 0021): `AirScan` / `AirCombine` inside `gpu for` lower to ILGPU kernels in `Lowerer.Gpu.cs`'s style:
   group scan with `Warp.ShuffleUp` + shared memory and a second launch for the grid fix-up; combine through
   shared-memory histograms + `Atomic.Add`. Integers exact; float scans require `ordered` (one-group) or AL0248.

## Runtime

New folder `src/Alice.Runtime/Cm/` (distinct from `Parallel/`, which holds ADR 0005's chunked map/reduce, and from
`Tensors/`):

- `Scan.cs`: chunk-local scan with vector lane shifts for 32/64-bit integers and floats, chunk-total scan, fix-up.
- `Segmented.cs`: flag-carrying scan over `(flag, value)` pairs.
- `Combine.cs`: per-worker private tables when `k * sizeof(T) <= 256 KiB`, merged column-wise in fixed order;
  `Interlocked.Add` for larger `k`, integers only.
- `Compact.cs`: flag scan + gather. `Permute.cs`: bounds and duplicate check through a bit set, then scatter.
- `SortByKey.cs`: LSD radix sort with per-chunk histograms (histogram + exclusive scan + scatter).

Dispatch through `KernelPool.Run(n, grain, fnptr, ctx)` (`Tensors/KernelPool.cs`), already 0-alloc; serial path when
`Par.Workers == 1` or below a measured threshold, with the same chunking so results do not change.

## Targets (the 16 Bend workloads)

| Workload | Alice shape | What wins |
|---|---|---|
| histogram | `cm for` per block inside a `par for` over blocks, then a column reduce | private tables + vectorized column reduce |
| kmeans | `cm for` combine into an 8 x 3 table per chunk (+ ADR 0037 arena `St`) | one pass per step replaces 24 masked accumulators |
| mandelbrot | `par for` rows + `counts.histogram` + `prefix_sum` + remap | the equalization passes stop being serial |
| terrain | `par for` tiles + `cm` histogram per tile | per-tile combine; sweeps stay serial per tile |
| tree-radix (idiomatic column) | flat `keys.sort_by_key` over 4M `UInt32` | CM counting sort instead of the trie |

gameoflife (several `+=` accumulators) and bfs (fork + stack arrays) are not CM; gameoflife needs a `LoopPar`
amendment for more than one accumulator.

**Idiomatic column.** `benchmarks/bend2` gains a second, non-1:1 column: an idiomatic Alice program per workload where
one exists (flat radix sort for tree-radix, `par for` + CM instead of tree folds), with the same checksum. The 1:1
port remains the Workstream A acceptance gate; the idiomatic column is reported beside it and labelled "not the same
algorithm".

## Consequences

- Float users must accept the fixed-chunking contract above; it is to be stated in `docs/language.md` (with the Prelude methods) and is shown by
  `alice explain` for each float scan.
- Large-`k` combines fall back to atomics and may lose to serial; `alice explain` shows the chosen path and threshold.
- A loop that looks like a scan but has another side effect is declined, because every statement must be classified.
- Permute without a proof races on duplicate targets: runtime duplicate check under inference, AL0249 under `cm for`.
- Order: runtime kernels and Prelude first (Wave 2), inferred shapes next, `cm for` once the shapes are stable.
- Rejected: doing nothing (`par for` + vectorize cover the `batch_run` family, not scan/combine/compaction/permute);
  a full CM dialect (xapping types, router semantics) up front; `ordered` required for every float scan (equal to
  serial but no parallelism).

Tests: `tests/Alice.Tests/Runtime/CmTests.cs` (each kernel against a serial reference; bit patterns identical across
`DOTNET_PROCESSOR_COUNT=1` and 32 for Int32/Int64/Float32/Float64; float results documented as differing from the
serial fold; 0 allocations; prefix_sum of 2^26 Int32 >= 8 GB/s on the forge); `tests/Alice.Tests/Compiler/CmTests.cs`
(the four inferred shapes, AL0247-AL0249, declined look-alike loops); `GpuLoopTests` exact Int32 scan and histogram on
the CPU accelerator; histogram port above Bend PAR-CPU, kmeans within 1.3x of C serial.

IL lowering (ADR 0040, section 3): library shapes are direct calls into `Alice.Runtime.Cm`; fused shapes get a static
kernel `void(void*, int, int)` passed by `ldftn` to `KernelPool.Run` (managed function pointer, no delegate), with
explicit `Vector512`/`Vector256` tiers guarded by JIT-constant checks and a `localloc` tile under `SkipLocalsInit`;
these kernels are the only allowlisted ILVerify findings.
