# Alice.Runtime
Path: `src/Alice.Runtime`. Pure C#. Depends on nothing in-repo.

| Folder | Content |
|---|---|
| `Simd/` | `Vector<T>` kernels: Sum, Dot, Add, Multiply, Scale, Min, Max |
| `Sparse/` | `CsrMatrix<T>`, `SparseVector<T>` |
| `Parallel/` | `Par`: chunked For, Map, Sum, Reduce, Sort; `ForkJob`/`ParJob` for fork/join (ADR 0015) |
| `Math/` | `AMath`: MatMul (blocked, parallel, SIMD), Mean, Variance, Norm, Axpy |
| `Concurrency/` | `Channel<T>`, `Actor<T>`, `Mutex<T>`, `Atomic` |
| `Memory/` | ownership pools: `Own`, `OwnList`, `OwnDict`, `ArrayGrowth` (ADR 0016) |
| `Arena/` | affine arena memory for sum types (ADR 0037): `Term` (64-bit word: bit 63 packed, 56-62 tag, 32-55 lane, 0-31 payload or slot; `Pack`/`Node`/`Unpack`), `Arena<TSlot>` (per-type, per-lane chunked slabs, 2^20 doubling, uint free list in the slot's first 4 bytes; `Alloc(tag, in slot)`, `Take(term, out slot)` frees only own-lane nodes, `Borrow(term)`), `ArenaLane` (lazy thread-static lane id, max 2^24), `ArenaRegion` (`using var r = ArenaRegion.Enter()`: O(1) per-lane reset, nested); 0 allocations steady state, fail-stop at 2^32 slots per lane, Debug double-free detection |
| `Native/` | `@[Native]`/`lib` support: library loading, callbacks, UTF-8 arguments (ADR 0018) |
| `Testing/` | `alice test` runner, `Spec`/`Expect` (RSpec style) |
| `Digest/` | hashing helpers |
| `Concurrency/` | `Channel<T>`, `Mutex<T>`, `Atomic` |
| `Actors/` | `ProcessCore` (mailbox scheduling, links, monitors, restart), `Actor<T>`, `Actor<S, T>`, `Supervisor`, `ExitReason` (ADR 0028) |
| `Arena/` (planned, ADR 0037) | `Arena` per-type per-lane slabs with free lists, `ArenaRegion` (`scope` resets), `Term` layout helpers |
| `Cm/` (planned, ADR 0039) | `Scan`, `Segmented`, `Combine`, `Compact`, `Permute`, `SortByKey` over `KernelPool`; fixed chunking by `n` |
| `Dataflow/` (planned, ADR 0038) | `Ring` (bounded SPSC), `Stage`, `Graph`, `Replicate` (ordered sink) |
| `Prelude/` | Ruby helpers (`name?` -> `name_q`), formatting, IO, `Dynamic` (`send`, `respond_to?`) |

Invariants: no native code of its own (only user-opted `@[Native]`/`lib` calls), no compiler dependency.
