# ADR 0014: MIR pass pipeline

Status: Accepted

Optimizations used to live inside individual lowerings: each one that produced a
counted loop decided by itself whether its step could skip the overflow check,
and `[a, b].min` was special-cased in call lowering. The same idea was written
once per producer, and a new producer silently missed it.

Alice 0.12.0 adds `src/Alice.Compiler/Optimizer`, an LLVM-style pipeline of small
primitive passes run on every lowered method body before it is printed to C#
(`Emitter.Print` and `Printer.Root` for generated helpers):

- `MirRewriter`: the single post-order MIR traversal all passes share; a pass
  overrides only the node kinds it rewrites. Opaque `MCode` text is never
  touched, so every rewrite is conservative.
- `MirFacts`: shared analyses (integer literals, powers of two, whether a body
  writes a local).
- Passes, in order: `const-fold` (integer literals, floored `/` `%`),
  `strength-reduce` (floored division/modulo by 2^k becomes `>>` / `&`, exact for
  two's complement), `literal-minmax` (`[a, b, c].min` becomes a `Math.Min`
  chain, no allocation), `induction` (a typed `i < end` counter that the body
  never writes steps unchecked).

Lowering stays a direct translation and supplies facts the passes need instead
of optimizing: counted loops declare their counter type (`MFor.VarType`), and
float modulo lowers to `floor_fmod`, so `floor_mod` is integer-only.
`ALICE_DISABLE_PASSES=name,...` switches passes off for A/B measurements.

Passes must be idempotent: helpers can reach the pipeline twice.

Value ranges need source types, so they are computed in lowering (`Lowerer.Ranges`), not in MIR: a
flow-insensitive interval per integer local (the union of every value assigned to it, iterated to a fixpoint
with widening), used to emit provably safe `+ - *` unchecked and to turn a proven two's-complement
reinterpretation into a sign extension.
