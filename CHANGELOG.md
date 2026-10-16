# Changelog

## 0.10.0 - Unreleased

- Tests: use platform-native math libraries, paths and newlines; account for ARM64 fused multiply-add support.
- Docs: refreshed Alice introduction, quick start, practical example catalog and contribution guide.
- Examples: organized basics, data, concurrency and web programs; ASP.NET Core API written in Alice, backed by an Alice pricing library.
- Automation: cross-platform tests and gated binary release archives with package smoke checks and SHA-256 checksums (ADR 0009).
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
- Architecture docs and ADRs 0001-0008.
- Language: sum-type enums with fields + exhaustive `case` (AL0201); `record` / `record struct`; generic classes and `forall T where` constraints; reopening .NET types (extension methods); untyped params monomorphized; .NET base members + overriding .NET virtuals; `async def`/`await`; `@[SoA]` + `SoA(T)` store.
- Safety: data races in all concurrent contexts are compile errors (AL0202, ADR 0008); `Mutex(T)`, `Atomic`, async `Channel`, async-mailbox `Actor`.
- Performance: auto-vectorization (`Vector<T>`), auto-parallelization of pure pipelines and counted loops (interprocedural purity), idiom rewrites (span Split, alternate lookup, single-probe increment), sealed leaf classes, `SkipLocalsInit`, server+concurrent GC default. Parallel float sums reassociate.
- Tooling: `alice new|build|run|check|test|fmt|add|clean|update|emit|version`; rustc-style diagnostics with AL codes and hints; en/pt/es (`ALICE_LANG`, `--lang`); MSBuild SDK (`src/Alice.Sdk`, `.alproj`); interop and MonoGame samples.
- Docs: `docs/errors.md`, `docs/performance.md`, `docs/tooling.md`.
- Modules: `require "./path"` (transitive, single-file CLI too), `include`/`extend` mixins, `def self.x` module functions, nested modules, `using <Module>` (using static).
- Syntax: heredocs (`<<~`, `<<-`), `%w[]`/`%i[]`, regex literals + `=~`/`!~`/`match?`, `&:sym`, `lambda {}`/`proc {}`, endless `def`, `attr_accessor/reader/writer`, inferred `@x` fields, `alias`, `retry`, `String#%`/`*`, chainable `<<`, array `+ - & |`, `Hash(K, V)`, exhaustive `case` over plain enums (AL0201).
- Stdlib: Ruby/Crystal String, Enumerable, Hash, number, Range, file/time/env and JSON helpers in `Prelude`.
- Tooling: `alice.toml` manifest (NuGet, path and workspace deps, `[build] native`); generated project hidden in `.alice/<name>.alproj`; cargo-style workspaces (globs, dependency-ordered builds, `alice run <member>`); `alice.lock` + `alice update`; `alice new --workspace` with auto member registration; `alice add --path`; `alice test` builds the package and uses its references.
- Removed: `alice sln` (C# references `<pkg>/.alice/<pkg>.alproj`; Alice references C# via path deps).
- Performance: SoA pass fusion + loop interchange (registers, fused `sum`); SIMT vectorization of loops with a data-dependent `while`; load-balanced heavy parallel loops; parallel word-count map-reduce with order-preserving merge. Benchmarks run 5: within ~10% of hand-optimized C#.
- Samples: `samples/` is a workspace; MonoGame `Breakout`.
- `@[Native]` functions: C via clang -O3 -march=native, function-pointer calls, automatic managed fallback (opt-in); small-data specialization (per-length workers on stack copies) — nbody at C speed.
- Floating-point contraction (FMA) by default, `@[StrictMath]` to opt out.
- C# compatibility suite (`CSharpCompatTests`, 17 programs): events, delegates, nullable `||`, tuples, flags, out/ref, `using r = ... end`, constrained generics (`where T : I` members), spans, LINQ, async WhenAll.
- Ruby reflection: `instance_variable_get/set`, `instance_variables`, `methods`, `public_send`.
- CLR events accept Alice blocks/Func values (`obj.Event += block`, `-=` unsubscribes the same handler).
- Ruby DSLs: implicit `yield` blocks, `instance_eval`/`instance_exec`, executable class bodies (macros), `@@class_vars`, runtime `define_method`, `send` → `method_missing` fallback (`RubyDslTests`).
