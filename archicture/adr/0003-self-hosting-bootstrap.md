# 0003 - Self-hosting bootstrap
Status: superseded (2026-10-15: self-hosting deferred; compiler and runtime stay C#)
Context: The compiler must be written in Alice eventually, without losing a trusted build path.
Decision:
- Stage0: C# compiler in `src/Alice.Compiler`.
- `selfhost/*.al`: the compiler in Alice.
- `alice bootstrap`:
  1. stage0 compiles selfhost -> stage1.
  2. stage1 compiles selfhost -> S1 (C#/IL).
  3. stage2 built from S1 compiles selfhost -> S2.
  4. Require S1 == S2 (fixed point).
  5. Require stage1 output == stage0 output on the test corpus.
- Every feature added to stage0 is ported to `selfhost/` in the same change.
Consequences: Language changes cost two implementations until stage0 is retired. Fixed point catches miscompilation. Output must be deterministic.
