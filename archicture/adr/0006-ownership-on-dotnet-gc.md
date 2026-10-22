# 0006 - Ownership as a static layer on the .NET GC
Status: proposed; owned local arrays and `own` implemented by ADR 0016 (which amends "Alice never implements freeing" for pooled array buffers)
Context: Users want ownership/lifetime safety. The CLR already prevents use-after-free, double free, dangling heap refs, and out-of-bounds access (GC, verifiable IL, bounds checks). Roslyn already enforces ref-safety for `ref struct`/`Span`/`scoped`. GC does not solve deterministic resource release, data races, mutable aliasing, or allocation-free hot paths.
Decision:
- Alice never implements allocation or freeing; memory reclamation is the GC.
- Ownership is compile-time only and lowers to plain C#: `using`/`own` (dispose), `move` (concurrency transfer), `borrow`/`&` (read-only views), `val`/`frozen` (immutability), `@[Stack]` -> `ref struct`, `Span(T)`, `scoped`, `stackalloc`, `in`, `unsafe` blocks, `checked`.
- Stack escape rules are Roslyn's; no Alice borrow checker, no lifetime annotations.
- New Alice analysis pass only for: `own` leaks, use-after-move, Send-like checks at `spawn`/`Channel`/actors, `val` reassignment. Must be implemented in stage0 and `selfhost/`.
- Rollout: P0 reuse-only syntax, P1 resource lints, P2 move/Send checks, P3 opt-in strict mode.
- Detail: `docs/design/memory-safety.md`.
Consequences: No runtime cost beyond C#'s own; no allocator to maintain. GC pauses remain (tunable: Server GC, concurrent GC, NativeAOT). Aliasing of heap state is only limited at API boundaries, weaker than Rust. Extends ADR 0004 and 0005; the extra pass doubles work across stage0 and selfhost.
