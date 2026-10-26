# ADR 0025: Polyhedral-lite loop interchange and tiling

Status: Accepted (0.13.2)

Nested loops over flat row-major arrays (`a[i * n + j]`) ran in source order. `i-j-k` matrix multiply walks `b`
down a column in its inner loop, and a transpose writes one element per cache line. Both are memory-bound.

Decision:

- A MIR pass `loop-tile` (`Optimizer/Passes/LoopTiling.cs`), backed by an affine dependence analysis
  (`Optimizer/Analysis/Affine.cs`, `Dependence.cs`). It runs before `induction`.
- Scope: a perfect nest of 2 or 3 counted `int` loops (`<`, step 1) with loop-invariant rectangular bounds. The
  innermost body may only contain element assignments `x[affine] = e`. `e` may use array reads, invariant names,
  literals, `Prelude.fma` and floating-point `+ - *` (an operand must be a float literal or an fma). Checked integer
  arithmetic could throw at a different iteration once the loops are reordered, so it is rejected.
- Subscripts are affine in the counters, with coefficients affine in at most one symbol. `i * n + j` is
  delinearized into (i, j) when the column stays in [0, n). Between two accesses to the same array where at least one
  is a write, a dimension `v + a` / `v + b` gives the exact distance `a - b` on loop v. Two constant dimensions that
  differ prove the accesses independent. Any other dimension leaves the distance unknown (any sign). The result is
  a set of lexicographically positive direction vectors.
- Interchange picks a legal order (every direction vector stays lexicographically positive) that minimizes the
  non-unit strides of the innermost loop, then of the middle loop. Ties keep source order. Matmul becomes `i-k-j`.
- Tiling needs every direction to be non-negative in every loop (a fully permutable band). It applies to 3-deep
  nests (reuse across the third loop) and to 2-deep nests that still stride across rows in the innermost loop
  (transpose). 2-deep stencils that are already stride-1 are left alone.
- Tile size comes from a cache model: 8-byte elements, L1 = 32 KiB, L2 = 256 KiB. It is the largest power of two
  whose per-array T x T tiles fill at most half of L1 (2-deep) or half of L2 (3-deep). That gives 32 for transpose
  and 64 for matmul. `ALICE_TILE=n` at compile time overrides it. There is no tiling attribute in the language.
- Versioning: the new nest runs only behind a guard, evaluated once in Int64. The guard checks that symbols and
  bounds are within ±2^30, that every loop is non-empty, that every subscript at every corner of the iteration box is
  inside its array, that every delinearized column is in [0, N), and that no written array is `ReferenceEquals` to
  another array of the nest. When the guard holds, nothing in the nest can throw, and subscripts are emitted
  `unchecked`. When it fails, the original nest runs unchanged, so its exceptions and aliasing behavior are the
  same as before.
- Reductions keep their order. For each `c[i, j]`, k still runs upwards across tiles. Results are bit-identical to
  the untiled code, and the tests compare bit patterns.

Consequences: this is not a general polyhedral scheduler. There is no skewing, no non-rectangular bounds, no
imperfect nests, and no element-type knowledge beyond the syntactic float proof. A multiply written over
`Array(Int32)` is left untouched. `ALICE_DISABLE_PASSES=loop-tile` gives the baseline, and
`benchmarks/tiling.sh` measures before and after.
