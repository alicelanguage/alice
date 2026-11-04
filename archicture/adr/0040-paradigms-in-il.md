# ADR 0040: The paradigms in IL: how AirToIl lowers fork/join, arenas, CM primitives and dataflow

Status: Accepted, implemented in 1.0.0 (arena, CM and dataflow nodes in AIR and `AirToIl`; no `AirVerifier` class). Lead direction 2026-11-02. Builds on ADR 0032 (AIR), ADR 0033 (IL
backend) and the paradigm ADRs 0015 (fork/join), 0037 (affine arenas), 0038 (dataflow), 0039 (CM primitives); each of
those carries a short "IL lowering" paragraph that points here.

The IL backend exists to make Alice's paradigms real and as fast as the hardware allows, not to reproduce C# idioms in
IL. ADR 0033 lists the generic emission targets (peepholes, `tail.`, unchecked indexing under proofs, `SkipLocalsInit`,
`constrained.`). This ADR fixes, per paradigm node, the IL that `Backend/Il/AirToIl` (backend plan D1.4, not written
yet) emits, the fact the node must carry so that the IL is legal, what the C# backend emits for comparison, the test
that pins it and the benchmark that must move.

Where things stand (2026-11-02, main 9173371):

- `AirForkJoin`, `AirForkable`, `AirPar`, `AirRent`, `AirRelease` exist (`src/Alice.Compiler/Mir/MExpr.Parallel.cs`)
  and are printed as C#. The arena (ADR 0037), CM (ADR 0039) and dataflow (ADR 0038) AIR nodes do not exist yet, nor do
  `Binder.Affine.cs`, `Lowerer.Affine.cs`, `Lowerer.Cm.cs`, `Lowerer.Dataflow.cs`: today `enum` lowers to sealed
  records and the CM/dataflow surface reaches only the runtime (`src/Alice.Runtime/{Arena,Cm,Dataflow}/`).
- `IlBuilder` (`src/Alice.Compiler/Backend/Il/IlBuilder.cs`) offers what this ADR needs: `EmitTailPrefix`,
  `EmitConstrainedPrefix`, `EmitReadonlyPrefix`, `EmitVolatilePrefix`, `EmitUnalignedPrefix`, `EmitCallIndirect`
  (`calli` with a `MethodSig`), `EmitLoadFunction` (`ldftn`), `localloc` (the body gets `InitLocals` handling),
  pinned locals, EH regions, and the `IlPeephole` set (`Temps`, `Branches`, `Constants`).
- No `AirVerifier` class exists yet (ADR 0032 defines it; backend plan D1.12 builds v2). The rules below are added to
  it.

Listings below are readable IL pseudo-code: `V_x` are locals, `L_x` labels, type names shortened.

## 1. Fork/join (`AirForkJoin`, `AirForkable`; ADR 0015)

**C# backend today** (`Lowering/Lowerer.Fork.cs`): `if (Par.Forkable) (a, b) = __fork_f_N(x, y); else { a =
f__serial(x); b = f__serial(y); }`, where the helper is `return Par.Fork(() => f(x), () => f(y));`. Per fork that
allocates a closure display class, two `Func<T>` delegates and one `ForkJob<A>` (`Parallel/ForkJob.cs`): four objects,
two delegate calls, one property call for `Par.Forkable` (a thread-static read, a static read and
`Environment.ProcessorCount`).

**IL lowering.**

- **Struct fork frame, boxed once.** For each fork site `AirToIl` generates a private nested struct
  `__Fork_f_N : IThreadPoolWorkItem` with fields `state`, `waiting`, `depth`, the captured arguments, `result` and
  `error` (an `ExceptionDispatchInfo`). `Execute()` is the `ForkJob.Execute`/`Run` protocol inlined with a *direct*
  `call f(x)` instead of a delegate. The frame is boxed once; the box is the work item. The joiner keeps the box and
  reaches its fields through `unbox` (a managed pointer into the box, no copy), so `Interlocked` operations and the
  result read work on the same storage the pool thread wrote. C# cannot spell an interior pointer into a box without
  `Unsafe.Unbox`; IL has `unbox` for exactly this.
- **The right branch is a plain call** on the forking thread, at depth + 1; three-way forks box two frames.
- **Pooled frames** (phase 2, only if the allocation shows in a profile): a per-thread free list of boxes per fork
  site. Hazard: a pool thread reads `waiting` after it stores `Done`, so a box may only be reused after a second
  "released" flag; until that is measured, one box per fork is the rule.
- **`Forkable` inlined as two loads and a branch.** It reads the thread-static fork depth and a `static readonly` limit
  that the runtime sets to 0 when `ProcessorCount == 1`, so tier-1 JIT folds the limit to a constant and the core-count
  test disappears. Requires two public, `[EditorBrowsable(Never)]` runtime fields on `Par` (today `forkDepth` is a
  private `[ThreadStatic]` and `MaxForkDepth` is private).
- **The serial clone.** `f__serial` contains no fork. A self-call in tail position becomes `starg` of the new
  arguments followed by `br L_start` (a loop: cheaper than `tail.`, which may need the tail-call helper on some
  signatures). `tail.` is emitted on the remaining tail calls (mutual recursion, calls to another clone) where legal:
  no byref to a caller local or argument is passed, the call is not inside a protected region, and it is immediately
  followed by `ret`. Divide-and-conquer self-calls (`a = f(l); b = f(r); a + b`) are not tail calls and stay `call`.

```
// a, b = f(x), f(y) inside f
    ldsfld   int32 Par::ForkDepth          // [ThreadStatic]
    ldsfld   int32 Par::ForkLimit          // static readonly; 0 on one core
    bge      L_serial
    ldloca   V_fr                          // V_fr : __Fork_f_1 (struct local)
    initobj  __Fork_f_1
    ldloca   V_fr ; ldarg x ; stfld  __Fork_f_1::x
    ldloca   V_fr ; ldsfld Par::ForkDepth ; ldc.i4.1 ; add ; stfld __Fork_f_1::depth
    ldloc    V_fr ; box __Fork_f_1 ; dup ; stloc V_box      // the single allocation
    ldc.i4.1 ; call  ThreadPool::UnsafeQueueUserWorkItem(IThreadPoolWorkItem, bool)
    pop
    // depth++ ; try { V_b = f(y) } catch { join V_box; rethrow } ; depth--
    ...
    call     f(y) ; stloc V_b
    ldloc    V_box ; unbox __Fork_f_1 ; call instance !A __Fork_f_1::Join()   // CAS Pending->Running, spin, Monitor
    stloc    V_a
    br       L_done
L_serial:
    ldarg x ; call f__serial(int32) ; stloc V_a
    ldarg y ; call f__serial(int32) ; stloc V_b
L_done:
```

**Facts the node carries.** `AirForkJoin.Branches` are `AirCall`s to `Pure` methods with value arguments (the ADR
0015 gate); the frame's field list (captured locals and their `AirType`); `SerialTarget` (the clone's
`AirMethodRef`); per call in the clone, `TailPosition` and `SelfTail`.

**Pinned by.** ILVerify clean on the corpus programs with forks; behaviour: `tests/parity` and the bend2 checksums
unchanged; exceptions in either branch surface unwrapped, left first (the `Par.Fork` contract); differential: the C#
and IL builds of every fork sample print the same output (`DifferentialTests`); allocation test: exactly one object per
fork that spawns, zero on the serial path (`GC.GetTotalAllocatedBytes(precise: true)` delta).

**Benchmark that must move.** bend2 parallel column: `queens`, `bfs`, `editdist`, `merkle`, `tree-radix`,
`tree-bitonic`, `tree-matmul` (fork-heavy roots). Serial column (`DOTNET_PROCESSOR_COUNT=1`) must not regress; it
gains only where the clone has tail self-calls.

## 2. Affine arenas (`AirArenaAlloc`, `AirArenaTake`, `AirArenaBorrow`, `AirTermPack`, `AirTermTag`, `AirArenaRegion`, `AirLane`; ADR 0037)

**C# backend today.** Nothing: `enum` lowers to sealed records (`Backend/Emitter.cs`). The planned C# printer form is
runtime calls: `Arena<TSlot>.Alloc(tag, in slot)`, `Arena<TSlot>.Take(term, out slot)`, `Arena<TSlot>.Borrow(term)`,
`Term.Pack`, `term.Tag` (`src/Alice.Runtime/Arena/{Arena,Term}.cs`). Each call reads the `[ThreadStatic]` slab, and
`Take`/`Borrow` re-check `i < Len` and go through the chunked `Locate` (a `Log2` and two bounds-checked array loads).

**IL lowering.**

- **Terms are `uint64` in IL.** `Term` is a one-field struct over `ulong`; since arena values never cross a public
  boundary (ADR 0037), locals, parameters and results of arena types are plain `uint64` in IL signatures.
- **Packed terms in registers.** `AirTermPack(Leaf, v)` is `ldc.i8 (1<<63 | tag<<56) ; ldloc v ; conv.u8 ; or`.
  `case` over a type is one `switch` on the top byte (`ldloc t ; ldc.i4 56 ; shr.un ; conv.u4`): the packed bit and the
  7-bit tag form one 8-bit key, so packed and slot variants dispatch through one jump table.
- **Lane slab hoisted per method.** `AirLane()` and the per-type slab are read once at method entry (or at the first
  dominating use) into locals `V_slab`, `V_laneBits`. Legal because a method body runs on one thread; not allowed across
  `AirAwait`/`AirYield` (verifier rule) or into a closure; a fork's frame method hoists its own.
- **Slot access by the lane cursor.** Recent slots live in the current chunk `Cur` from `CurBase`; the fast path is one
  unsigned compare `(uint)(i - CurBase) < (uint)Cur.Length` and then `Unsafe.Add(ref MemoryMarshal
  .GetArrayDataReference(Cur), i - CurBase)` (no bounds check, no `Locate`); the slow path calls `Ref(i)`. When the node
  carries `SlotProof.Live` (the term is affine-live in an open region: no Take before this use, no region exit between
  definition and use), the stale check `i < Len` is dropped; without the proof it stays in front of the fast path.
- **`Take` = read fields, then push the free list** in three operations after the field reads: load `Free`, store it
  into the slot's first `uint`, store `i` into `Free`. The lane test `(t & LaneMask) == V_laneBits` stays: it chooses between a
  lane-local free and a foreign read (ADR 0037 rule 3). `Borrow` is the read without the two stores, by `ref readonly`.
- **Alloc** inlines the free-list pop and the bump into `Cur`; `Grow` stays an out-of-line call.
- **`AirArenaRegion`** is `call ArenaRegion::Enter ; stloc V_r ; .try { body } finally { ldloca V_r ; call
  ArenaRegion::Dispose }`, no boxing of the struct.

```
// case t when Node(l, r) ... (Take, slot variant, lane-local fast path)
    ldarg    t ; ldc.i4 56 ; shr.un ; conv.u4 ; switch (L_leaf, L_node, ...)
L_node:
    ldarg t ; ldc.i8 LaneMask ; and ; ldloc V_laneBits ; bne.un L_foreign
    ldarg t ; conv.u4 ; stloc V_i
    ldloc V_i ; ldloc V_slab ; ldfld uint32 Slab::CurBase ; sub ; stloc V_off
    ldloc V_off ; ldloc V_cur ; ldlen ; conv.u4 ; bge.un L_locate                 // lane cursor test
    ldloc V_cur ; call ref !0 MemoryMarshal::GetArrayDataReference<Slot>(!!0[])
    ldloc V_off ; conv.u ; call ref !!0 Unsafe::Add<Slot>(ref !!0, native uint) ; stloc V_r   // V_r : Slot&
    ldloc V_r ; ldfld uint64 Slot::l ; stloc V_l                                 // fields into locals
    ldloc V_r ; ldfld uint64 Slot::r ; stloc V_rr
    ldloc V_r ; ldloc V_slab ; ldfld uint32 Slab::Free ; stind.i4                // push: load Free, store
    ldloc V_slab ; ldloc V_i ; stfld uint32 Slab::Free
```

**Runtime surface needed.** The IL reads slab internals (`Cur`, `CurBase`, `Free`, `Len`, `LaneBits`, the
thread-static slab). They need a public, `[EditorBrowsable(Never)]` raw-access surface in `Alice.Runtime.Arena`
(the backend does not rely on `IgnoresAccessChecksToAttribute`, which NativeAOT does not guarantee). The debug-build
`MarkLive`/`MarkFreed` checks stay calls under `--debug`.

**Facts the node carries.** `AirArenaAlloc`: slot type, tag, packed flag. `AirArenaTake`: `Frees`, `SlotProof`
(`Live` or `Unknown`). `AirTermPack`: payload width at most 32 bits. Per method: `HoistLane` (no await/yield, the slab
local's live range).

**Pinned by.** ILVerify (the `Unsafe.Add` path is verifiable; `GetArrayDataReference` is a safe API); behaviour:
`ArenaTests` and `AffineTests` run under both backends; differential: the bend2 arena ports print the same checksum
under C# and IL; allocation: 0 managed allocations in steady state under IL; a test that a term used after a
freeing Take is rejected by `AirVerifier`, not miscompiled.

**Benchmark that must move.** bend2 serial and parallel columns: `tree-radix`, `tree-bitonic`, `tree-matmul`, `merkle`,
`symreg`, `hashmap` (the ADR 0037 table). Target: the IL path beats the C# runtime-call path of the same arena program;
the ADR 0037 gates (within 1.5x of the C twin serial) are measured on IL.

## 3. CM primitives (`AirScan`, `AirCombine`, `AirCompact`, `AirPermute`; ADR 0039)

**C# backend today.** The nodes do not exist yet; ADR 0039's planned C# form is runtime calls (`Cm.InclusiveScan(src,
dst, CmOp.Add)`, `Cm.Histogram`, `Cm.Compact(src, x => pred, dst)`, `Cm.Permute`). A shape whose element is an
expression `e(i)` (not a plain array read) needs either a materialized temporary array or a `Func<T, bool>`
invocation per element (the `Compact` overload in `Cm/Compact.cs`).

**IL lowering.**

- **Library shapes** (the element is `xs[i]`, the op is one of `CmOp`) are a direct `call` to the public runtime entry
  point. No inlining gain is worth the code size; the runtime kernels are already vectorized and allocation-free.
- **Fused shapes** (`acc += e(i); out[i] = acc`, `if c(i) ... m += 1`, `h[k(i)] += v(i)`): `AirToIl` emits a private
  static kernel `__cm_f_N(void* ctx, int32 lo, int32 hi)` per site and passes `ldftn __cm_f_N` to
  `KernelPool.Run(n, grain, delegate*<void*, int, int, void>, void*)` (`Tensors/KernelPool.cs`), the same managed
  function-pointer protocol the runtime kernels use; the pool calls it with `calli`. No delegate, no
  `[UnmanagedCallersOnly]` (managed calling convention). The context is a struct local on the caller's stack (`ldloca`
  then `conv.u`), alive until `Run` returns.
- **Chunk loops with explicit vector intrinsics.** Inside the kernel, `e(i)` is evaluated a tile at a time with
  `Vector512.LoadUnsafe`/`Vector256.LoadUnsafe` and the op's intrinsics into a `localloc` tile buffer (constant size, at
  most 16 KiB, outside any loop), which a public per-tile runtime primitive then scans with the carry. All tiers are
  emitted, each guarded by `Vector512.IsHardwareAccelerated` / `Vector256.IsHardwareAccelerated`: those are JIT-time
  constants, so the JIT drops the dead tiers and the module stays portable. `[SkipLocalsInit]` (module-wide, section 5)
  keeps `localloc` from zeroing the tile.
- **Determinism.** Chunking comes from `Cm.ChunkSize(n)` as in the runtime, so the IL kernel and the library kernel
  produce the same bit patterns for floats (ADR 0039 float contract).

```
// cm for i in 0...n : acc += f(xs[i]) ; pre[i] = acc     (Int32)
caller:
    ldloca V_ctx ; ldarg xs ; stfld Ctx::src ; ...           // V_ctx : __CmCtx_N (stack struct)
    ldarg n ; ldarg n ; call Cm::ChunkSize(int32)
    ldftn void __cm_f_N(void*, int32, int32)
    ldloca V_ctx ; conv.u
    call KernelPool::Run(int32, int32, method void *(void*, int32, int32), void*)
__cm_f_N(void* ctx, int32 lo, int32 hi):                    // per chunk: map tile, scan tile, carry
    ldc.i4 1024*4 ; conv.u ; localloc ; stloc V_tile         // SkipLocalsInit: not zeroed
    call bool Vector512::get_IsHardwareAccelerated ; brfalse L_v256
L_v512:  ... call Vector512<int32> Vector512::LoadUnsafe<int32>(ref !!0, native uint) ; <op> ; Store ...
L_v256:  ...
```

**Facts the node carries.** Op, `Inclusive`, `Ordered`, element `AirType`; `Fused` (the element expression is not a
plain read, so a kernel method is generated); for `AirPermute` the `PermutationProof` source (AL0249); the tile size.

**Pinned by.** ILVerify: the fused kernels are unverifiable by design (unmanaged pointers, `localloc`, `calli` in the
pool); the test asserts that the only ILVerify findings are in generated `__cm_*` kernel methods (an exact allowlist by
method name pattern and error kind), and everything else is clean. Behaviour: `CmTests` (runtime and compiler) under
both backends, bit-identical results across `DOTNET_PROCESSOR_COUNT=1` and 32; differential: same output as the C#
backend's runtime-call form for every inferred shape; 0 allocations per call.

**Benchmark that must move.** bend2 `kmeans` (combine), `mandelbrot` (histogram + prefix_sum), `terrain` (per-tile
histogram), and the idiomatic column of `tree-radix` (`sort_by_key`); runtime gate `prefix_sum` of 2^26 Int32 >= 8
GB/s (ADR 0039) also met by a fused `cm for` scan.

## 4. Dataflow (`AirDataflow`, `AirStage`, `AirRing`; ADR 0038)

**C# backend today.** The nodes do not exist; the runtime already offers function-pointer overloads
(`Graph.Source`, `Pipe.Stage`, `Pipe.Replicate`, `Pipe.Sink` taking `delegate*<ref TState, ReadOnlySpan<TIn>, ref
RingWriter<TOut>, void>` in `Dataflow/Graph.cs`) next to delegate overloads (`StageBody`). ADR 0038's planned C# form
is one `Dataflow.Run(graph)` with static stage methods; C# can only reach the pointer overloads with `unsafe` and
`&Method`.

**IL lowering.**

- **Stage bodies are static methods** `__stage_N(ref TState st, ReadOnlySpan<TIn> batch, ref RingWriter<TOut> w)`
  registered with `ldftn`. No per-item delegate; a fused chain of static pure stages (ADR 0029) is one body.
- **`in`/`ref readonly` items.** The batch loop reads elements through the span's `ref readonly` indexer and accesses
  fields through the reference (no copy of large structs); state is mutated in place through `ref TState`. The loop is
  `for i < batch.Length` so the JIT removes the span bounds check.
- **`w.Write(x)` inlined.** The fast path of `RingWriter.Write` (`buf[pos & mask] = x; if (++pos == publishAt)
  Publish`) is emitted inline in the stage body; `Publish` is `volatile. stfld` of the ring tail (a release store,
  what `Volatile.Write` compiles to) and the reader side's `Volatile.Read` is `volatile. ldfld`. The slow path stays a
  call. This needs the writer's `buf`, `mask`, `pos`, `limit`, `publishAt` reachable from IL (same raw-access surface
  as section 2); without it the backend calls `Write`, which the JIT inlines (`AggressiveInlining`).
- **No `Interlocked` in steady state** (the ADR 0038 memory model is unchanged; the IL only spells the same release
  stores and acquire loads).

```
__stage_2(ref Lexer st, ReadOnlySpan<string> batch, ref RingWriter<Token> w):
    ldc.i4.0 ; stloc V_i ; br L_cond
L_body:
    ldarga batch ; ldloc V_i ; call ref readonly !0 ReadOnlySpan<string>::get_Item(int32) ; ldind.ref ; stloc V_line
    ldarg st ; ldind.ref ; ldloc V_line ; ldarg w ; callvirt instance void Lexer::lex(string, RingWriter<Token>&)  // w by ref
    ldloc V_i ; ldc.i4.1 ; add ; stloc V_i
L_cond:
    ldloc V_i ; ldarga batch ; call instance int32 ReadOnlySpan<string>::get_Length() ; blt L_body
    ret
```

**Facts the node carries.** `AirStage`: method, state type, `Parallel`, `Ordered`, input and output element types;
`AirRing`: element type, capacity (power of two); the graph's SPSC/acyclic proof (AL0246 passed).

**Pinned by.** ILVerify clean (`ldftn` + the runtime's pointer overload is verifiable at the call site; the pointer
call itself is inside the runtime); behaviour: `DataflowTests` (compiler) under both backends, output bit-identical
across `DOTNET_PROCESSOR_COUNT=1` and 32; differential vs the C# backend; 0 allocations per item; the stress tests
(`Category=Stress`) on arm64 as ADR 0038 requires, because the IL spells the volatile operations itself.

**Benchmark that must move.** No bend2 workload is a pipeline (ADR 0038). The gate is `benchmarks/dataflow` (3-stage
file -> tokens -> counts): the IL build at least matches the hand-written C# `System.Threading.Channels` pipeline and
beats the C# backend's form of the same program.

## 5. General emission rules (every method)

- **`[module: SkipLocalsInit]`** on every emitted module (the C# backend already prints it, `Backend/Emitter.cs`): Alice
  guarantees definite assignment, so zero-init is wasted; `localloc` tiles rely on it.
- **`readonly.` is only legal before `ldelema`** (ECMA-335 III.2.3). It is emitted on `ldelema` of arrays of generic or
  reference element type when the element is only read (skips the covariance type check). For struct *receivers*
  the IL backend instead passes the address (`ldloca`/`ldarga`/`in` parameter) to a callee the AIR effect facts prove
  non-mutating, with no defensive copy; C# copies unless the member is declared `readonly`.
- **`constrained.`** before every `callvirt` on a generic type parameter receiver (no boxing for structs).
- **`tail.`** as in section 1: legal positions only, never inside a protected region, never with byrefs to the frame.
- **`no.`** (`no.typecheck/rangecheck/nullcheck`, 0xFE 0x19) is not in System.Reflection.Metadata's `ILOpCode` enum
  and RyuJIT does not use it to drop checks; Alice does not emit it. Unchecked indexing comes from
  `Unsafe.Add`/`GetArrayDataReference` under proofs instead.
- **Peepholes** from ADR 0033 (`IlPeephole.All`: temps, branches, constants) run on every body.
- **Deterministic metadata**: definition-ordered tables and content-hashed MVID/PDB id (`IlModule`, `MetadataWriter`);
  generated frame, kernel and stage names are numbered per method in source order, so two builds are byte-identical.
- `AggressiveInlining` on generated frame `Join`/`Execute` helpers is not set (they are out-of-line by design);
  it is set on small leaf methods as in ADR 0033.

## 6. AirVerifier rules added

| Node | Rule |
|---|---|
| `AirForkJoin` | every branch is an `AirCall` to a `Pure` method; captured values are not byref-like and not byrefs (frames are boxed); frame field types equal the argument types; no `AirForkJoin` inside a serial clone |
| tail calls | `TailPosition` only on a call followed by return of its value, outside any try/catch/finally, with no byref argument into the caller's frame and a stack-compatible signature; `SelfTail` only when the target is the method itself |
| `AirArenaTake` (ADR 0037) | a term is not used after a `Take` with `Frees` |
| `SlotProof.Live` | the term's definition dominates the use with no intervening freeing `Take` and no region exit; otherwise `Unknown` |
| `AirLane` hoisting | the hoisted lane/slab local is not live across `AirAwait`/`AirYield` and is not captured by an `AirLambda` |
| `AirTermPack` | payload width at most 32 bits; tag below 128 |
| `AirArenaRegion` | the reset runs on every exit (lowered as finally) |
| `AirScan`/`AirCombine` | op associative for the element type (mirrors AL0248); `Ordered` set on float combines |
| `AirPermute` | carries a `PermutationProof` under `cm for` (mirrors AL0249) |
| CM kernels | kernel signature is exactly `void(void*, int32, int32)`; the context is a stack local not stored to the heap; `localloc` size constant, at most 16 KiB, not inside a loop |
| `AirDataflow` | each `AirRing` has exactly one producer and one consumer stage; capacity a power of two; stage signature `(ref TState, ReadOnlySpan<TIn>, ref RingWriter<TOut>)`; `RingWriter` never escapes its stage (byref-like rule of ADR 0032) |
| volatile ops | `volatile.` loads/stores only on ring index fields named by the runtime surface |

## Consequences

- The runtime grows a small public, `[EditorBrowsable(Never)]` raw-access surface (fork depth and limit on `Par`, slab
  fields in `Arena`, `RingWriter` fields, a per-tile scan primitive in `Cm`). It is an internal contract between the
  compiler and the runtime of the same version, not a user API; changing it is a compiler + runtime change in one
  commit.
- Fused CM kernels are unverifiable by design; ILVerify cleanliness (ADR 0033) holds everywhere else and the allowlist
  is exact.
- The IL backend gains performance the C# backend cannot express without `unsafe` (interior pointers into boxes, inline
  slab access, pointer kernels); the C# backend remains the behavioural oracle for every node (differential tests), not
  the performance reference.
- Rejected: mirroring C# (delegates and closure classes for forks, runtime calls for every arena operation): it would
  cap the paradigms at what the C# backend already does. Rejected: `IgnoresAccessChecksToAttribute` to reach runtime
  internals: not a guaranteed contract on NativeAOT. Rejected for now: pooled fork frames (a reuse hazard, see section
  1) until a profile shows the one allocation per fork.

Tests: a new `tests/Alice.Tests/Backends/IlParadigmTests.cs` (one ILVerify + behaviour case per node in sections 1-4,
plus the CM allowlist), `DifferentialTests` over the paradigm samples, `AllocationTests` per backend (one object per
spawning fork, 0 per arena operation and per dataflow item), and the bend2 / `benchmarks/dataflow` gates named above.
