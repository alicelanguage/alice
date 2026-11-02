# ADR 0035: Bootstrap by pinned previous release and component-wise replacement

Status: Accepted (1.0 direction; not yet implemented). Supersedes ADR 0003 and revives its goal (the compiler written in
Alice).

ADR 0003 planned a parallel full `selfhost/` compiler and ported every feature twice. That is why it was deferred (ADR
0007). The language now has what a compiler needs: sum types, patterns, generics, BCL collections, iterators, closures,
exceptions, out/ref interop and modules. Two things are missing: strict typing and deterministic output.

Decision:

- **Prerequisites.** First, strict mode: `[build] strict = true` makes every `Unknown`/`dynamic` type an error. Compiler
  components in Alice are built strict. Second, determinism: ordered symbol tables and dictionaries wherever output
  order depends on them.
- **Mechanism.** The trusted stage0 is the pinned previous release binary, fetched by `alice toolchain` (ADR 0010), as
  Rust and Go do. There is no parallel full tree. Each component is rewritten once as an Alice project (`.alproj`)
  that the remaining C# components reference. The C# version is deleted the release after the Alice version reaches
  parity. The compiler in Alice runs on .NET and may use the BCL. The playground keeps the .NET wasm compiler for 1.0.
- **Order and oracle per component:**
  1. Syntax (Lexer, Parser, Formatter): identical AST dump on every `.al` in the repo.
  2. AIR, passes and `AirVerifier` (ADR 0032): identical AIR dump.
  3. Emitters (`AirToIl`, `AirToWasm`, ADR 0033/0034): byte-identical outputs.
  4. Binder: identical HIR dump and diagnostics.
  5. Lowering: identical AIR dump, plus the soundness suites.
  6. CLI.
- **`alice bootstrap`**: stage0 (pinned) builds stage1, stage1 builds stage2, and stage2 builds stage3. It requires
  **stage2 == stage3 byte for byte** (fixed point) and stage1 output == stage0 output on the parity corpus. CI runs it
  on the forge. A release ships only when the fixed point has been stable for one release.
- **Parity corpus**: `tests/parity/`. The generated `selfhost/PARITY.md` lists each component's state (C#, Alice,
  C# deleted).

Consequences: each component costs one rewrite instead of two parallel implementations. A language change during the
migration touches whichever implementation is live for that component. Bootstrapping needs network access, or a cached
previous release. The compiler's own speed becomes an Alice benchmark, so strict mode has to come first.

Tests: AST/AIR/HIR dump commands (`alice emit --ast|--air|--hir`) and parity tests per component under
`tests/parity/`; the `alice bootstrap` fixed-point job in CI; a determinism test that compiles the corpus twice and
compares the bytes.
