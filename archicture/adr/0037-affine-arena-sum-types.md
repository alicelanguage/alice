# ADR 0037: Affine arena memory for sum types

Status: Accepted (direction; not yet implemented). Amends ADR 0006 ("Alice never implements allocation or freeing")
and extends ADR 0016 (ownership) and ADR 0017 (`scope`, `borrow`) to recursive sum types. Design detail:
`/work/reports/paradigms-plan.md` section 1.

Alice lowers `enum Tree` to a closed hierarchy of sealed records (`Backend/Emitter.cs`, "Rust-style sum type"): one GC
object with a header per node, a pointer chase per `case`, and an entirely live tree promoted gen0 -> gen2. That is
the `tree` gap in `benchmarks/bend-vs` (serial Bend 123 ms vs Alice 1542 ms, 134 MB vs 486 MB). ADR 0016 owns flat
arrays of unmanaged elements only, so no existing rule reaches it.

Bend 2 is not an interaction-net runtime (BendRT: "there are none"); it keeps the nets' *memory discipline*: every
value has one consumer, a `match` takes ownership and frees the node onto a free list, a constructor with one
word-sized field is packed into the 64-bit term, and nothing is traced. Alice's own HVM2 port
(`benchmarks/inet/README.md`) was 17-30x slower than serial code and stays parked. A scratch experiment
(`/work/reports/paradigms-scratch/arena/Program.cs`, tree depth 23, 32 cores, same checksum 4290772992 in every cell)
measured sealed records at 1042-1264 ms serial / 578-626 ms forked, against a struct arena with packed `Leaf(UInt32)`
at 143-220 ms serial and 18-39 ms forked with per-lane arenas.

Decision: adopt the affine memory discipline of interaction nets as a **lowering of sum types to per-type struct
arenas**. No net reducer runs at run time; the "interaction" compiles to a struct read and a free-list push at each
`case`, and parallelism still comes from ADR 0015 forks.

## Surface

Inferred (silent). An `enum` with fields is **arena-eligible** when every payload field is unmanaged or another
arena-eligible enum (self and mutual recursion allowed), and whole-program every local, parameter and result of that
type is used **affinely**: consumed by exactly one `case`, passed once to an Alice method, returned, or built into
another arena value. A second use of a live local makes that `case` a borrow (fields read, node not freed).

Explicit (ADR 0017 pattern: the keyword makes a failed proof a compile error):

```crystal
affine enum Tree               # contract: arena form, or a compile error
  Leaf(v : UInt32)
  Node(l : Tree, r : Tree)
end

def total(t : Tree) : UInt32
  case t                       # consumes t: the node is freed after its fields are read
  when Tree::Leaf(v) then v
  when Tree::Node(l, r)
    a, b = total(l), total(r)  # ADR 0015 fork; each half owns its subtree
    a &+ b
  end
end
```

`borrow p` (ADR 0017) keeps its meaning: the callee only reads, no node is freed. `scope ... end` is the arena
**region**: at `end` every lane arena allocated inside is reset in O(1) (the C twins' `expr_arena_len = 0`).

| Error | Meaning |
|---|---|
| AL0240 | `affine enum T`: a payload field is a reference type that is not an affine enum |
| AL0241 | affine value `x` escapes: stored in a field or collection, captured by a block or lambda, passed to .NET, compared (`==`, `hash`, `to_s`), or reaches `rescue` |
| AL0242 | affine value `x` read after it was consumed by `case` or moved into a call (the AL0208 analogue) |
| AL0243 | `borrow p` on a parameter of affine type that the body consumes |

**Whole-program demotion.** Any escaping use of an inferred-eligible type (the AL0241 list) demotes that *type*
to today's sealed records everywhere in the program: one CLR type per Alice enum, no conversion code at any boundary.
`alice explain` reports per enum `arena` or `record (demoted: escapes at file:line)`, naming the site. Under
`affine enum` the same site is AL0241 instead of a demotion.

## Pipeline

1. HIR analysis, new `Semantics/Binder/Binder.Affine.cs` (beside `Binder.Borrow.cs`, `Binder.Races.cs`,
   `Binder.Actors.cs`, which hold whole-program facts of the same kind):
   - eligibility per enum (payload rule);
   - per method, a move-state flow analysis over locals of eligible type with the `OwnState` lattice of
     `Lowerer.Ownership.cs` (live / moved, joined over branches and loops). A `case` on a definitely-live local that
     is never read again is a **Take**, otherwise a **Borrow**. A call argument moves unless the parameter is
     borrow-inferred (ADR 0016 interprocedural borrow). A read after a move is AL0242 under `affine`, a demotion
     otherwise;
   - whole-program fixpoint: a type demoted anywhere is a record everywhere. Facts land on `TypeSym` (`Arena`,
     `Packed` per variant) and on local uses (`Take` / `Borrow`).
2. Lowering, new `Lowering/Lowerer.Affine.cs`. The `case` lowering (`Lowerer.Statements.cs`) and variant construction
   (`Lowerer.Expressions.cs`) call into it through two one-line dispatches, landed by the owner of the AIR batch that
   retypes those files (ADR 0032 migration):
   - term layout (`ulong`): bit 63 packed flag; bits 56-62 variant tag (up to 128 variants); bits 32-55 lane id;
     bits 0-31 payload (packed) or slot index. A variant with no fields or one field of at most 32 bits is packed
     (`Leaf(UInt32)`, `Emp`, `Free`, `Busy`). Any other variant occupies a slot in its enum's slab; the slot is a
     generated `record struct` of the widest variant's fields, and the tag lives in the term, not the slot;
   - `Type::Variant(args)` -> `AirArenaAlloc`; `case x` -> `AirArenaTake` (fields into locals, then the slot is
     dropped) or `AirArenaBorrow`; packed variants are tag tests on the word. An allocation in the arm after a Take
     reuses the slot through the free list (tree-radix `merge`, tree-bitonic `warp_zip`);
   - forks (`Lowerer.Fork.cs`) are unchanged: a forked helper runs on a pool thread whose lane arena is
     thread-static.
3. **Lane-local frees only.** A node is pushed onto a free list only by the thread that owns the node's lane;
   a free of a foreign-lane node is skipped and the memory returns at the region reset. No cross-thread free list and
   no atomics on the free path (BendRT: "neither crosses a thread"). A cross-lane remote-free ring was rejected for 1.0.

AIR nodes added to the ADR 0032 set (next to `AirPar`, `AirRent`, `AirRelease`, `AirStackAlloc`):
`AirTermPack(variant, payload)`, `AirTermTag(term)`, `AirArenaAlloc(type, fields...)`, `AirArenaTake(term) -> fields`
(with a `Frees` flag), `AirArenaBorrow(term) -> fields`, `AirArenaRegion(body)` (reset at every exit), `AirLane()`.
The C# printer prints runtime calls; the IL printer (ADR 0033) inlines the word arithmetic. `AirVerifier` checks that a
term is never used after an `AirArenaTake` that frees it.

## Runtime

New folder `src/Alice.Runtime/Arena/` (deliberately not `Memory/`, which holds ADR 0016's flat-array pools):

- `Arena.cs`: per-type, per-lane slab: chunked `TSlot[]` of 2^20 elements doubling, a `uint` free list threaded
  through the first field, `Push` / `Drop` / `Reset`, `[ThreadStatic]` lane.
- `ArenaRegion.cs`: per-lane reset marks for `scope`.
- `Term.cs`: layout helpers, `[Pure]`.

Zero managed allocations in steady state after warm-up. Limits: 2^32 slots per lane per type and 2^24 lanes; overflow
is a numbered fail-stop, as in BendRT. GPU (ADR 0021): a slab is a flat `TSlot[]`, so a later phase may let a
`gpu for` body borrow arena values; device-side allocation is out of scope for 1.0.

## Targets (the 16 Bend workloads)

| Workload | Alice shape | What wins |
|---|---|---|
| tree-radix | `affine enum Arr/Trie`, `case a, b`; forks in `gen`/`to_map`/`to_arr`/`chk` | arena + packed `Single`/`Free`/`Busy`; slot reuse in `merge`; the pool steals work on the skewed `merge` |
| tree-bitonic | same; `scope` around the sort | arena with free list, in-place `warp_zip` |
| tree-matmul | `affine enum Quad/Vec`; 8 forked products, 4 adds | arena, 4-word slots |
| merkle | `enum Mt` borrowed by `audit` | `Leaf(UInt32)` packed, `Node(h, l, r)` one slot |
| symreg | `affine enum Expr` in a `scope` per candidate | region reset; `eval` borrows |
| kmeans | `enum St` in a `scope` per step | arena (the 8-way sums are ADR 0039) |
| hashmap | `affine enum Chain` + owned `Array(UInt64)` of bucket terms | arena + ADR 0016 owned buckets |

Expected: Alice serial at C-twin speed on the tree trio (5-8x over records; 7x measured on the plain tree), and ADR
0015 parallel speedups without GC contention on a live heap. The other nine workloads are the `batch_run` fork shape
over independent leaves and are not affected.

## Consequences

- Silent cliff: one escaping use (`puts t`, `==`) demotes a type and the program runs at record speed with no error.
  Guards: `alice explain` names the demotion site, `affine enum` turns it into AL0241, and the Bend ports use
  `affine enum`.
- Parallel peak memory: a tree built on lane 3 and consumed on lane 7 is not freed until the region resets, so the
  peak can reach the full tree. Bounded, and the same as Bend's lane-local frees.
- Arena values never cross a public boundary: a type that reaches .NET is demoted, so every public API still sees
  plain CLR records (ADR 0019).
- Recursion depth is unchanged versus records.
- Later amendment: Bend's counted redirects (Keep) for types used more than once, instead of borrow-only.
- Rejected: doing nothing (fork/join and ADR 0016 do not touch the GC object graph); resuming the HVM2 port
  (17-30x slower than serial); compile-time net reduction (ADR 0014 folding and ADR 0026 specialization cover it);
  dual representation with materialization at boundaries (doubles the type surface, a copy per crossing).

Tests: `tests/Alice.Tests/Runtime/ArenaTests.cs` (0 allocations steady state; tree(23) gen + sum <= 220 ms serial on
the forge); `tests/Alice.Tests/Compiler/AffineTests.cs` (every escape kind demotes or raises AL0241; AL0240, AL0242,
AL0243; `alice explain` names the site; Take vs Borrow selection); `tests/parity` unchanged; `benchmarks/bend-vs`
`tree` serial within 1.3x of Bend with checksum 4290772992; the seven `benchmarks/bend2` ports above within 1.5x of
the C twin serial and above Bend PAR-CPU on 32 cores.

IL lowering (ADR 0040, section 2): terms are `uint64` in IL; packing and `case` dispatch are bit operations and one
`switch` on the top byte; the lane slab is read once per method; slot access uses the lane cursor and `Unsafe.Add`
without bounds checks, and drops the stale check when the node carries `SlotProof.Live`; a `Take` reads the fields and
pushes the free list in three operations.

## Amendment 2026-11-03: regions are per thread

**Problem.** `ArenaRegion.Enter`/`Dispose` marked and reset every lane of every arena type in the process. A `scope`
on one thread therefore hid the free list of every other thread's lane while it was open and rolled their cursors
back when it ended, under nodes those threads still held: the next Alloc there returned a live node's slot (two
Allocs aliased), and the unsynchronized push/pop raced with their Alloc/Take (lost free-list entries, terms past the
cursor). Invariant 9 does not allow this. Found as the `ArenaTests.Tree_23_consuming_sum_has_the_bend_checksum`
flake: `AffineLowerTests` ran `scope` programs in-process in parallel (7 freed slots lost out of 2^23).

**Decision.**

1. `Enter` pushes marks only on the calling thread's lane, in every arena type, and records the region in a
   thread-static stack (frames reused by depth: 0 allocations steady state). `Dispose` pops only what that region
   pushed. No global lock or global depth covers other lanes' cursors; another thread's lane is never touched.
2. Fork children (`Par.Fork`, `ForkJob`, `ForkKernelJob`, the `ParJob`/`KernelJob` chunks) capture the forking
   thread's innermost region when they are created. A child that runs on another thread inside that region binds
   (every arena type's thread-static slab) to a lane the region leases for it: a spare lane of the same region, or a
   fresh lane id whose marks are pushed when it is leased. The child returns to its previous lane before it signals
   its join; chunks lease per claimed chunk, so a late helper never touches an ended region. Nodes built by children
   stay valid after the join (they live on the leased lane), and `Dispose` pops every leased lane and returns its id
   to the lane pool, so fork-built and foreign-consumed nodes are reclaimed with the region as before. A child run
   inline by the forking thread, or by a thread already under the same region, keeps its lane.
3. `Dispose` on a thread other than the one that called `Enter` throws `InvalidOperationException`; out-of-order and
   double `Dispose` still throw. `ArenaRegion.Depth`/`CurrentDepth` count the regions the calling thread allocates
   under (a fork child counts its parent's).
4. Threads that are not fork children of the region (plain threads, `spawn`, actors, dataflow stages) are outside it:
   their nodes outlive it. This matches invariant 9's scope (arena values cross threads only through forks).

**Cost.** Outside regions a fork job reads one thread-static more; inside a region a child on another thread pays a
lock and one slab store per arena type on entry and exit. Measured on the bend2 tree ports and symreg (serial and
parallel, results/arena.md "Per-thread regions"): new / old 0.95-1.02, within noise.

**Rejected.** Keeping process-wide regions as a documented limit (a user program with `scope` and an unrelated thread
using arenas could alias live nodes); popping a child's marks at its join (the parent still reads the child's nodes
until the region ends); marking the pool thread's own lane for a child (unrelated work that thread runs before the
region ends would be reset with it).

Tests: `ArenaTests.A_region_on_another_thread_does_not_move_this_threads_lane`,
`Regions_on_another_thread_race_with_this_lane_without_effect`,
`A_fork_inside_a_region_allocates_in_a_leased_lane_reclaimed_with_the_region`,
`A_thread_outside_the_region_keeps_its_nodes`, `A_region_ends_on_the_thread_that_entered_it`.
