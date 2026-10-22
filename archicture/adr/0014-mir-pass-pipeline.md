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
  never writes steps unchecked), `shift-count` (the `+ - *` spine of a shift
  count over names and literals is unchecked, ADR 0013).

The emitter marks a self-recursive static function whose parameters are all
primitives or primitive arrays `AggressiveOptimization`: its hottest frames
would otherwise run in tier 0 and OSR, and with no reference parameters tiered
PGO has no virtual call to devirtualize (suite queens: 0.92 s → 0.81 s). Marking
every loop-bearing method this way was measured and rejected (±5%, no net win).

Two narrower tiering rules were measured and kept. A method with a loop nest
(a loop inside a loop) usually runs long from one call, so instrumented tier 0
runs it until on-stack replacement: it is compiled fully optimized (suite nbody
151 → 78 ms, C# 78 ms; steady-state benchmarks within noise). Static functions
over primitives that loops call, directly or through another such function
(`noise`, `lattice` in a pixel loop), stay in tier 0 for most of a short run
because call-counting tier-up waits for startup to settle; they are compiled
fully optimized too (suite terrain at 512x512: 143 → 115 ms, C# 105 ms).
Lowering the runtime's call-counting delay globally was measured and rejected
(+2-6 ms of background JIT on short runs).

`strength-reduce` also rewrites floored `/` and `%` by any other positive
literal to branch-free helpers (`a / d + ((a % d) >> 31)`,
`r + ((r >> 31) & d)`).

Lowering stays a direct translation and supplies facts the passes need instead
of optimizing: counted loops declare their counter type (`MFor.VarType`), and
float modulo lowers to `floor_fmod`, so `floor_mod` is integer-only.
`ALICE_DISABLE_PASSES=name,...` switches passes off for A/B measurements.

Passes must be idempotent: helpers can reach the pipeline twice.

Value ranges need source types, so they are computed in lowering (`Lowerer.Ranges`), not in MIR: a
flow-insensitive interval per integer local (the union of every value assigned to it, iterated to a fixpoint
with widening), used to emit provably safe `+ - *` unchecked and to turn a proven two's-complement
reinterpretation into a sign extension.
