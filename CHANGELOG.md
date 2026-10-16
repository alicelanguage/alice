# Changelog

## 0.10.0 - Unreleased

- Stage0 compiler (C#): lexer, Alice -> C# translator, Roslyn IL backend.
- CLI: `alice run|build|emit|bootstrap`; `build --native` via NativeAOT.
- Runtime: `Simd`, `CsrMatrix`/`SparseVector`, `Par`, `AMath`, `Native` (clang kernels + managed fallback), `Prelude`.
- Self-hosting bootstrap with fixed-point check (`selfhost/`).
- Nullable reference types enforced; null-safety diagnostics are errors.
- Architecture docs and ADRs 0001-0005.
