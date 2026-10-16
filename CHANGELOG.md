# Changelog

## 0.10.0 - Unreleased

- Compiler rewritten as Syntax -> Semantics (HIR) -> Lowering (MIR) -> Backend (C# + Roslyn Release IL) (ADR 0007).
- Inference: Crystal-style return types across later defs; unresolvable -> `dynamic`; Ruby function-scoped locals.
- Language: symbols, `send`/`respond_to?`, `yield` with `&block : A -> R`, `if`/`case` expressions, `val`, `Any` = dynamic.
- Attributes: `@[Inline]`, `@[NoInline]`, `@[Hot]`, `@[Stack]` (ref struct).
- Concurrency: `spawn`/`t.wait`, `Channel(T)`, `Actor(T)`, `par_map`; spawn data-race check is a compile error.
- Performance: literal-block iterators inlined to loops, map/select chains fused (0 allocations), `static` capture-free lambdas, virtual only where subclassed, invariant non-boxing interpolation.
- CLI: `alice run|build|emit`; `build --native` via NativeAOT; `-r` extra references.
- Runtime: `Simd`, `CsrMatrix`/`SparseVector`, `Par`, `AMath`, `Channel`, `Actor`, `Prelude`.
- Removed: clang native kernels, `Native` class, `alice bootstrap`, `selfhost/` (ADR 0003 superseded; ADR 0002 amended).
- Nullable reference types enforced; null-safety diagnostics are errors.
- Architecture docs and ADRs 0001-0007.
