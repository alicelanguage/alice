# Bend 2 runtime suite, bench2 ports: findings

Worker bench2. Eight workloads from `bend/bench/runtime` (Bend 2 repo, pinned results in
`_pin_/apple_m4_max.txt`) ported to Alice: nbody, queens, raytrace, symreg, terrain, tree-bitonic, tree-matmul,
tree-radix. `c/<name>.c` is an unmodified copy of Bend's `main.c`. `alice/<name>.al` is the 1:1 port (the Workstream A
gate, ADR 0039). `idiomatic/<name>.al` is the fastest Alice shape found, for symreg and the three tree workloads.
Below, "<name>-adt" means the 1:1 port `alice/<name>.al` and plain "<name>" means the shape in `idiomatic/`; the
shape comparison is the reason for the split.

## Setup

- Box: Arch Linux, 32 cores, 61 GB, .NET 10.0.114, clang 23. Other agents were using it too (load average 4-19
  during the runs), so parallel numbers are noisy, about +-30%. Serial numbers repeat within about 5%.
- C: `clang -O3 -march=native -ffp-contract=off -lm`. nbody and raytrace need `-ffp-contract=off` to print Bend's
  checksum (their headers say so). It does not change the other six.
- Alice: the worktree compiler at d92b40b. `alice build`, then `dotnet <name>.dll`.
- Serial: `taskset -c 31 env DOTNET_PROCESSOR_COUNT=1`. I used core 31 because bench1 pins to core 0. C serial
  also runs on `taskset -c 31`. Parallel: all 32 cores, no pinning.
- Time: hyperfine 2.0 `-N -w 1`, median of 5 runs (ADT variants: 2 runs), cold process. That includes ~40 ms of
  .NET start-up and JIT. RSS is hyperfine's peak resident memory, the maximum over the runs.
- Sizes: Bend's big size for every workload. Every Alice output equals both the C output and the `expect` value in
  `main.bend`.

## Results

| workload | Alice serial ms | Alice 32 cores ms | C serial ms | Alice RSS serial / par MB | C RSS MB | checksum |
|---|---:|---:|---:|---:|---:|---|
| nbody (17, 300) | 8985 | 673 | 9461 | 37 / 39 | 3.3 | 3516450380 ok |
| queens (17, 17, 11730) | 8085 | 874 | 5713 | 36 / 39 | 3.3 | 2063750025 ok |
| raytrace (12, 6000) | 10801 | 1072 (818 in an earlier run) | 7769 | 37 / 38 | 3.2 | 1924309504 ok |
| symreg (18, 110) | 3982 | 480 | 4949 | 37 / 47 | 3.3 | 2383953211 ok |
| terrain (16, 5) | 6065 | 512 | 2749 | 38 / 39 | 3.2 | 2572468224 ok |
| tree-bitonic (23) | 4605 | 545 | 11588 | 454 / 570 | 130 | 3787129428 ok |
| tree-matmul (7, 9, 384) | 2334 | 303 | 5648 | 55 / 132 | 6.2 | 3797651056 ok |
| tree-radix (22) | 4274 | 772 | 7617 | 328 / 1131 | 214 | 1998173798 ok |

ADT shapes (same checksums, 2 runs each):

| workload | Alice serial ms | Alice 32 cores ms | RSS serial / par MB |
|---|---:|---:|---:|
| symreg-adt | 7387 | 1007 | 53 / 45 |
| tree-bitonic-adt | 192514 | 40606 | 2930 / 19771 |
| tree-matmul-adt | 21265 | 5627 | 67 / 844 |
| tree-radix-adt | 28045 | 2828 | 2161 / 3608 |

### Compared with Bend (estimate only)

Bend is not installed on the box. This table scales Bend's M4 Max pins by each workload's ratio of C on this box to
C on the M4. Treat it as a rough guess: the M4 Max has 16 cores, the box has 32, and the memory systems differ.

| workload | C box / C M4 | Bend SEQ est. ms | Alice serial | Bend PAR est. ms | Alice 32 cores |
|---|---:|---:|---:|---:|---:|
| nbody | 1.85 | 10630 | 8985 (wins) | 950 | 673 (wins) |
| queens | 1.59 | 8580 | 8085 (wins, barely) | 720 | 874 (loses) |
| raytrace | 2.13 | 9840 | 10801 (loses) | 840 | 818-1072 (tie) |
| symreg | 2.24 | 6750 | 3982 (wins) | 600 | 480 (wins) |
| terrain | 1.35 | 3010 | 6065 (loses, 2x) | 270 | 512 (loses, 1.9x) |
| tree-bitonic | 2.03 | 12240 | 4605 (wins) | 1620 | 545 (wins) |
| tree-matmul | 1.91 | 3930 | 2334 (wins) | 430 | 303 (wins) |
| tree-radix | 2.73 | 11140 | 4274 (wins) | 1210 | 772 (wins) |

## Which shape wins, and why

- **ADT nodes lose badly on all four tree workloads.** tree-bitonic-adt allocates on the order of n log^2 n nodes,
  billions at 2^23. perf on serial 2^19: 67% of samples are in libcoreclr (the GC), 22% in JIT code. The live tree
  is hundreds of MB and stays alive across a whole flow level, so gen0 and gen1 promote it over and over. Bend frees
  each node as `match` consumes it, so its heap stays at about n nodes (146 MB).
- **Flat arrays win because these trees are perfect.** The tree-bitonic tree (2^23 leaves), the matmul quad-trees
  (4^7) and the symreg ASTs (63 nodes) are always complete. If each one is stored as its leaves in order, Bend's
  `warp_zip` result `Node{Node{a0,b0},Node{a1,b1}}` is exactly "firsts, then seconds" of the compare-swapped pairs.
  The recursion, the compare order and the fork tree stay as in Bend, and the checksums match.
  - tree-bitonic: recursive and forked above 2^18 leaves. Below that, one method sorts or merges in place in a fresh
    array, with the same compare-swaps done level by level. Cutoff sweep (serial / parallel): 2^12 6.7 s / 3.2 s,
    2^16 5.9 s / 1.4 s, 2^17 4.8 s / 1.0 s, 2^18 4.5 s / 0.78 s.
  - tree-matmul: quadrants in Z order, the same recursion of 8 products and 4 adds, and a 16x16 row-major leaf kernel
    (the u32 ring gives the same sums). An 8x8 kernel indexing through Z order took 7.45 s serial; the 16x16
    row-major kernel takes 2.48 s.
  - symreg: each candidate is a heap-ordered 63-slot array (an owned array, reused through the pool) instead of 63
    GC nodes: 7.4 s down to 4.0 s serial.
- **tree-radix is not a perfect tree, so it gets a hybrid.** The trie is sparse. A pure preorder token stream works
  (merge is one linear pass), but it copies the 24-node chain tails at every one of the 22 merge levels: 13.3 s
  serial. The kept shape:
  - to_map forks over the gen tree down to 2^16-key subtrees.
  - Each subtree runs C's arena algorithm inside one method: a UInt32 pair arena, the chains, a merge that reuses the
    left node, and an explicit stack. It then emits a preorder token stream.
  - The top 6 levels merge token streams.
  - to_arr and chk are single passes, so they do not fork here. chk's join is associative, so a left fold gives the
    same Chk.

  Leaf-size sweep (parallel): 2^12 1.25 s, 2^14 1.05 s, 2^16 0.99 s.
- **Why the arena cannot fork end to end.** A method that writes an array it did not allocate is impure (issue 7).
  Disjoint per-task regions of one shared arena would be race-free, but the compiler cannot prove it, and `par for`
  only accepts the `a[i]` and `a[i * w + j]` write patterns. Bend's affine heap gives this for free. For
  pointer-shaped trees that are not perfect, this is the structural gap.

## Hardest gaps and hypotheses

1. **terrain: Alice serial 6.1 s, C 2.7 s.** Built with `-fno-vectorize -fno-slp-vectorize`, C takes 3.96 s, so
   clang's vectorization of the noise fill (4096 independent cells, 4 hashes each) accounts for about 1.5x. The rest
   is scalar RyuJIT code for the Gauss-Seidel sweep (bounds checks on the neighbour loads, `to_u32` conversions).
   Rewriting the sweep as row/column loops took it from 7.8 s to 6.1 s. Next ideas: a SIMD fill (`@[Native]` would
   also work, but that would measure clang, not Alice) and neighbour loads without bounds checks.
2. **queens: parallel 874 ms, Bend estimate 720.** Serial is 1.4x C (8.1 s vs 5.7 s). solve returns `Stats` (a
   record struct of two UInt32) through every level of the recursion. C keeps it in registers; RyuJIT probably goes
   through memory. Not profiled.
3. **raytrace: serial 10.8 s, C 7.8 s.** The sphere table is a `Float32` array parameter, so its loads are
   bounds-checked. C unrolls the 9-sphere loop and turns the constant table into immediates. Bend's fork tree also
   has 2^26 colf leaves, of which 2/3 render. Switching to `MathF.sqrt` took serial from 11.9 s to 10.8 s.
4. **Memory.** Every Alice process starts at ~37 MB RSS (the runtime); Bend and C start at 2-3 MB. In parallel the
   flat trees also use more than C: tree-radix 1131 MB against C's 214 MB, because the fresh arrays of each fork
   level stay garbage until the next GC.

## Notes on fork shape

- Alice infers forks wherever two pure calls are independent and one of them is recursive. So it forks more than
  Bend does: symreg's `expr_eval` and `expr_size`, for example. Below the depth cutoff this costs one thread-static
  read per call.
- Bend's 4- and 8-way parallel lets (tree-matmul-adt `gen`, `mul`, `mvm`, `cksum`) are written as pairs of two-call
  forks. Only the 2^9 batch tree forks in practice, because the cutoff (log2(32) + 3 = 8 levels) is used up above
  the rounds.

## Compiler issues found (not fixed; workarounds are in the ports)

Every repro below was compiled with the worktree compiler (`dotnet src/Alice.Cli/bin/Release/net10.0/alice.dll`, Alice 0.13.2 dev at d92b40b).

1. **A sum-type constructor whose first argument is a qualified path does not parse.**
   ```alice
   enum T
     L(v : Int32)
     N(l : T, r : T)
   end
   a = T::L(1)
   t = T::N(T::L(2), a)    # error[AL0101]: expected type (found '2')
   ```
   `M::Node(M::Free, r)` fails the same way (`found 'r'`). It looks like `X::Y(A::B, ...)` gets parsed as a generic
   type application. Workaround: bind the inner constructor to a local first (alice/tree-bitonic.al `warp_leaf`,
   `warp_zip`; alice/tree-radix.al `radix`).

2. **A local initialised with a nullary variant gets the variant type, not the enum type.**
   ```alice
   enum M
     Free
     Node(l : M, r : M)
   end
   r = M::Free
   f : M = M::Free
   r = M::Node(r, f)       # error[AL0304]: type mismatch: expected Free, got Node
   ```
   Workaround: annotate (`r : Map = Map::Busy`, alice/tree-radix.al).

3. **Reading a top-level constant silently turns off fork/join.**
   ```alice
   CUT = 3
   def tree(d : Int32) : Int32
     return 1 if d == CUT          # emitted as `public static readonly int CUT = 3`
     x, y = tree(d - 1), tree(d - 1)
     x + y
   end
   ```
   `alice explain` shows no fork for `tree`. With the literal `3` it forks. This cost tree-bitonic 7x on 32 cores
   before I found it (6.2 s vs 0.83 s). Either a `static readonly` primitive should count as a pure read, or
   `alice explain` should say why the method did not fork. Workaround: inline the literal.

4. **`val` with a constant-style name does not parse, and a top-level `val` blocks forks too.** `val k = 3` works,
   but `val CUT = 3` gives `error[AL0101]: unexpected keyword (found 'val')`. Repro 3 with `val cut = 3` and
   `d == cut` still does not fork.

5. **`String#to_u32` leaks a Roslyn generic error.** `"17".to_u32` gives `AL0300: The type 'String' cannot be used
   as type parameter 'T' in the generic type or method 'to_u32(T)'`. `to_i` works. Workaround: `.to_i.to_u32`.

6. **A local called `nil` produces raw C# errors** (`The left-hand side of an assignment must be a variable`,
   `Cannot convert null to 'Bool'`) where a "reserved word" message would help. A record field called `nil` works.

7. **A helper that writes a fresh local array passed in by its caller makes the caller impure** (this is the
   documented rule, but it is the main thing that blocks arena-style trees):
   ```alice
   def fill(a : Array(Int32), i : Int32) : Int32
     a[i] = i
     i
   end
   def leaf(n : Int32) : Int32
     a = Array(Int32).new(n)   # fresh, never escapes
     for i in 0...n
       fill(a, i)
     end
     a[n - 1]
   end
   def tree(d : Int32) : Int32
     return leaf(10) if d == 0
     x, y = tree(d - 1), tree(d - 1)   # does not fork: leaf is impure
     x + y
   end
   ```
   Because of this, every flat port puts its whole in-place kernel (`bsort_small`, `flow_small`, `to_map_local`,
   `tile_run`) in one method body with explicit loops and stacks. A rule that lets a method lend an owned local
   to a `borrow`-style mutating helper and still stay pure would let these kernels be written as ordinary helpers.

8. **No pure `Float32` square root under its full name.** `x.sqrt` exists only for `Float64`
   (`Prelude.Numbers.cs:60`), so `x.to_f64.sqrt.to_f32` is the obvious spelling, and it made nbody serial 1.9x slower
   than C (17.6 s against 9.1 s with `MathF`). `System.MathF.Sqrt(x)` is fast but not trusted, so every fork above
   it disappears:
   ```alice
   def s1(x : Float32) : Float32
     System.MathF.Sqrt(x)        # `MathF.sqrt(x)` is trusted (Lowerer.Parallel.cs:183) and forks
   end
   def tree(d : Int32, x : Float32) : Float32
     return s1(x) if d == 0
     a, b = tree(d - 1, x), tree(d - 1, x + 1.0_f32)   # no fork with System.MathF
     a + b
   end
   ```
   The ports use the unqualified `MathF.sqrt(x)`. Suggestions: a `Float32#sqrt`, and accept `System.Math`/`System.MathF`
   wherever `Math`/`MathF` are accepted.

9. **`alice build` defaults to Server GC with `Gen0MaxBudget` = 16 MB** (in the emitted runtimeconfig). For the ADT
   tree ports on 32 cores this is the worst setting: tree-bitonic-adt runs 40.6 s and 19.8 GB parallel, against 192 s
   and 2.9 GB serial; tree-matmul-adt uses 71.5 s of CPU in parallel against 20.9 s serial. With a 16 MB gen0 a live
   tree of hundreds of MB gets promoted again and again, and every GC stops 32 threads. Raising the gen0 budget with
   `DOTNET_GCgen0size` / `DOTNET_GCgen0MaxBudget` changed nothing on tree-bitonic-adt 2^20 serial (13.2-13.9 s), and
   `DOTNET_gcServer=0` made 2^20 parallel 8x slower (19.6 s against 2.37 s). Not investigated further.
