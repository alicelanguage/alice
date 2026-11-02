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
| `Native/` | `@[Native]`/`lib` support: library loading, callbacks, UTF-8 arguments (ADR 0018) |
| `Testing/` | `alice test` runner, `Spec`/`Expect` (RSpec style) |
| `Digest/` | hashing helpers |
| `Concurrency/` | `Channel<T>`, `Mutex<T>`, `Atomic` |
| `Actors/` | `ProcessCore` (mailbox scheduling, links, monitors, restart), `Actor<T>`, `Actor<S, T>`, `Supervisor`, `ExitReason` (ADR 0028) |
| `Dataflow/` | static dataflow networks (ADR 0038, P3.1): `Ring<T>` (bounded SPSC ring, padded cached head/tail), `RingWriter<T>` (batch publish of 64), `Graph`/`Pipe<T>` (Source -> Stage* -> Sink, `Run` rethrows the first exception), `Replicate` (k workers, sequence-numbered batches, ordered reorder window or `ordered: false`); stages are `IThreadPoolWorkItem`s woken with one CAS, 0 bytes per item |
| `Prelude/` | Ruby helpers (`name?` -> `name_q`), formatting, IO, `Dynamic` (`send`, `respond_to?`) |

Invariants: no native code of its own (only user-opted `@[Native]`/`lib` calls), no compiler dependency.
