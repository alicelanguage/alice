# Alice.Runtime
Path: `src/Alice.Runtime`. Depends on: nothing in-repo.

| Type | Content |
|---|---|
| `Simd` | `Vector<T>` kernels: Sum, Dot, Add, Multiply, Scale, Min, Max |
| `CsrMatrix<T>`, `SparseVector<T>` | sparse storage and ops (`Sparse.cs`) |
| `Par` | chunked parallel For, Map, Sum |
| `AMath` | MatMul (cache-blocked, parallel, SIMD), Mean, Variance, Norm, Axpy |
| `Native` | P/Invoke to `libalice_native`; managed fallback |
| `Prelude` | Ruby-like stdlib; `name?` -> `name_q` |

Invariants: native and managed paths give equal results; no compiler dependency.
