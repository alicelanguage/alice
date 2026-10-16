# Alice.Compiler
Path: `src/Alice.Compiler`. Depends on: Roslyn (`Microsoft.CodeAnalysis.CSharp`), `Alice.Runtime` reference for emitted code.

Pipeline: `Lexer` -> `Translator` (Alice -> C# syntax) -> Roslyn backend (IL assembly).

Invariants:
- Single backend (ADR 0001).
- Deterministic output (ADR 0003 fixed point).
- Every feature is mirrored in `selfhost/*.al`.
- Null-safety diagnostics are errors (ADR 0004).
