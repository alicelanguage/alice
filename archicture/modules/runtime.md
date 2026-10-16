# Alice.Runtime
Path: `src/Alice.Runtime`. Pure C#. Depends on nothing in-repo.

| Folder | Content |
|---|---|
| `Simd/` | `Vector<T>` kernels: Sum, Dot, Add, Multiply, Scale, Min, Max |
| `Sparse/` | `CsrMatrix<T>`, `SparseVector<T>` |
| `Parallel/` | `Par`: chunked For, Map, Sum |
| `Math/` | `AMath`: MatMul (blocked, parallel, SIMD), Mean, Variance, Norm, Axpy |
| `Concurrency/` | `Channel<T>`, `Actor<T>` |
| `Prelude/` | Ruby helpers (`name?` -> `name_q`), formatting, IO, `Dynamic` (`send`, `respond_to?`) |

Invariants: no native code, no compiler dependency.
