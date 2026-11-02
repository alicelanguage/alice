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
| `Prelude/` | Ruby helpers (`name?` -> `name_q`), formatting, IO, `Dynamic` (`send`, `respond_to?`) |
| `Core/` | Alice Core standard library, namespaces `Alice.Core.*` (ADR 0036): `Core/<Module>/` per module (`Text`, `Json`, `Csv`, `Toml`, `Fs`, `Process`, `Time`, `Crypto`, `Net`, `Log`, `Cli`, `Collections`). .NET names; Alice reaches them in Ruby spelling through the binder (`Binder.CoreSpellings`) |
| `Core/Abstractions/` | Frozen contracts (ADR 0036): `RegexNode`/`RegexPattern`/`RegexFeatures`, `IInput<TUnit>` (`Utf8Input`, `Utf16Input`), `JsonDocument`/`JsonAny`/`JsonIterator`/`JsonWriter` (partial; bodies in `Stubs/` until C2.3/C2.4), `HttpRequest`/`HttpResponse` views |

Invariants: no native code of its own (only user-opted `@[Native]`/`lib` calls), no compiler dependency, no eager
reference-type statics on startup paths (`StartupTests`; Core caches live in lazy holders). Core hot paths carry an
allocation test and a row in `benchmarks/core/` (ADR 0036).
