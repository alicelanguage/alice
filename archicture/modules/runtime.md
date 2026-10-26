# Alice.Runtime
Path: `src/Alice.Runtime`. Pure C#. Depends on nothing in-repo.

| Folder | Content |
|---|---|
| `Simd/` | `Vector<T>` kernels: Sum, Dot, Add, Multiply, Scale, Min, Max |
| `Sparse/` | `CsrMatrix<T>`, `SparseVector<T>` |
| `Parallel/` | `Par`: chunked For, Map, Sum |
| `Math/` | `AMath`: MatMul (blocked, parallel, SIMD), Mean, Variance, Norm, Axpy |
| `Concurrency/` | `Channel<T>`, `Mutex<T>`, `Atomic` |
| `Actors/` | `ProcessCore` (mailbox scheduling, links, monitors, restart), `Actor<T>`, `Actor<S, T>`, `Supervisor`, `ExitReason` (ADR 0024) |
| `Prelude/` | Ruby helpers (`name?` -> `name_q`), formatting, IO, `Dynamic` (`send`, `respond_to?`) |

Invariants: no native code, no compiler dependency.
