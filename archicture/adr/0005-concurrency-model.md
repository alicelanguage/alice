# 0005 - Concurrency model
Status: accepted
Context: Goal: Elixir-like massive parallelism on the CLR thread pool.
Decision:
| Construct | Lowering |
|---|---|
| `spawn { ... }` | `Task.Run` |
| `Channel(T)` | typed wrapper over `System.Threading.Channels` |
| actors/mailboxes | object + private `Channel` mailbox, one consumer task |
| `Par.For/Map/Sum` | chunked data parallelism in `Alice.Runtime` |
Consequences: No custom scheduler; relies on .NET thread pool. No process isolation like BEAM; shared memory is possible, so safety relies on typing and channels by convention. Status of language syntax: partly planned (see `docs/language.md`).
