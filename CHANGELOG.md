# Changelog

## 0.12.0 - Unreleased

- Breaking (ADR 0013): signed `/` and `%` are floored like `divmod` (`-7 / 2 == -4`); `tdiv`/`remainder` truncate. Float `%` follows the divisor.
- Breaking (ADR 0013): integer overflow raises `OverflowException`; `&+ &- &*` wrap. `2 ** -1` raises instead of returning 1.
- Breaking (ADR 0013): `to_u8` and the other sized conversions raise when out of range (NaN too); `to_X!` wraps. `2.5.round` is `3.0` (half away from zero).
- Breaking: floats print with a decimal point and Ruby exponent rules everywhere (`2.0`, `1.0e-07`, `1.0e+16`).
- Generated programs default to workstation GC (faster startup, lower RSS); `[runtime] gc = "server" | "low-memory"` in `alice.toml`.
- Startup: the `format` regex is source-generated (no Reflection.Emit on every launch); an `if`/`case` with a `raise` branch no longer becomes `dynamic` (the C# runtime binder was loading at startup).
- Fix `(-5).abs` and `-7.5.abs` returning negative values (the C# emitter dropped the parentheses).
- Fix `round(n)`/`floor(n)`/`ceil(n)` on decimal fractions (`0.29.floor(2)` was `0.28`).
- `[a, b, c].min`/`.max` on a literal lower to `Math.Min`/`Math.Max` with no array allocation (editdist inner loop).
- Compiler: MIR pass pipeline (ADR 0014) in `src/Alice.Compiler/Optimizer`: constant folding, strength reduction (`x / 8` is `x >> 3`, `x % 16` is `x & 15` with floored semantics), literal `[a, b].min/max` without allocation, unchecked counted-loop steps. `ALICE_DISABLE_PASSES` for A/B runs.
- Compiler: fork/join of independent pure recursive calls (ADR 0015): `a = f(x)` / `b = f(y)` run in parallel with lazy task creation (only the top log2(cores)+3 levels spawn tasks). Writes into arrays allocated in the method keep it pure. Suite `tree-radix` at 1M elements: 3226 ms → 319 ms (C# 665 ms).
- Breaking: `Channel(T).new` is unbuffered (a rendezvous, as in Crystal): `send` waits for a receiver. It was silently unbounded. `Channel(T).new(n)` buffers n. The channel is a lock-guarded ring (no Task per blocking call, no allocation per message); `send` after `close` raises `ClosedChannelError`.
- Row flattening: an array of fixed-width literal rows filled by a full `for i in 0...n` loop and never escaping (`points[i] = [x, y]`) is one flat array `points[i * k + j]` with the column still bounds-checked (suite kmeans at 1M points: 0.44 s / 95 MB → 0.38 s / 63 MB).
- Fix `Array(T).new(n) { |i| ... }` ignoring its block.
- Heap-free small arrays: a local array of literal size (<= 256 unmanaged elements, or a literal of <= 32 items) that never escapes lives in one `stackalloc` buffer at method entry, also when declared inside a loop.
- Fix `Array(T).new(n, v)` ignoring `v` (it returned zeros); a non-default fill uses an uninitialized allocation plus one `Fill`.
- Range analysis: counted loops bounded by `.Length`/`.Count` and their `i ± k` offsets drop provably dead overflow checks (editdist: 998 → 884 ms, C# 841 ms).
- Stdlib fork/join: `sort` of a large primitive array is a parallel merge sort with a pooled buffer (10M `Int32`: 756 → 207 ms).
- Docs: [fork/join](docs/fork-join.md) (idea credited to Victor Taelin's BendRT) and [heap and GC](docs/heap-gc.md).
- Value-range analysis: integer `+ - *` whose operand ranges (literals, masks, shifts, widened Int32, `^`/`|` of non-negatives, floored `% d`) prove no overflow are emitted unchecked; a proven `x >= 2^31 ? x - 2^32 : x` (also 8/16 bits) is one sign extension instead of a data-dependent branch. Suite terrain at 1M: 345 → 212 ms (C# 189 ms).
- Purity gate: `Alice.Runtime.Pure` marks runtime functions the compiler may parallelize through (`Digest::*`); static `Module.f(x)` receivers, string interpolation, array literals, `min/max/sum` and the new numeric primitives are pure reads. Suite merkle at 1M leaves: 1812 → 1489 ms (C# 1885 ms).
- Stdlib fork/join: `sum`, `min`, `max` of primitive arrays of 2^20+ elements split into SIMD chunks across cores (32M `Float64` sum + max: 68.7 → 16.4 ms).
- `2.0 ** n` lowers to `Math.ScaleB` and `x ** 2` to `x * x` (both bit-identical to `Math.Pow`); widening conversions (`to_i64` on Int32, `.to_f`) are plain casts, narrowing ones an inlined checked helper.
- Fork/join also forks Bend's single-statement parallel call `a, b = f(x), g(y)` (pure, independent, recursive), including into existing variables.
- Startup: Prelude's dynamic/reflection/regex caches are created on first use instead of when every program starts.
- Compiler: a loop nested in a serial loop is no longer auto-parallelized with the "heavy" threshold of 2 (game of life dispatched its 3-cell neighbourhood loop to the thread pool per cell: 197 ms → 60 ms); constant short trip counts emit no parallel path.
- Fix `for k in 0...m` with `m : Int64` (and `n.times` on Int64) declaring an `int` counter that could never reach the bound.
- Allocation-free: `round(n)`/`floor(n)`/`ceil(n)`, `q, r = a.divmod(b)` (no array), `a.step(limit, k)` with a literal step (inlined loop), and `puts`/`str`/`inspect` of numbers (no boxing).
- `Digest::SHA256.hexdigest`, `Digest::SHA1`, `Digest::MD5` (Ruby API).
- New primitives: `to_i8`..`to_u64`, `to_i32`, `to_f64`, `fdiv`, `sign`, `popcount`, `leading_zeros_count`, `trailing_zeros_count`, `bit_length`, `finite?`; `Int32::MAX`, `UInt8::MIN`, `Float64::INFINITY`, `Float64::NAN`; snake_case .NET statics (`Math.sqrt`, `Math.atan2`).
- Ownership (ADR 0016): local arrays of unmanaged elements that provably never escape are owned; their buffers return to a per-thread exact-length pool (`Own(T)`) before reassignment and at every exit, so a loop building a fresh row per iteration stops allocating. Moves (`prev = row`), interprocedural borrows and returns keep ownership. `own a = ...` makes it a checked contract: escape AL0207, use after move AL0208. 12 of the 17 suite workloads use it; editdist RSS 40.7 MB (C# 46.9 MB).
- Auto-parallel loops prove row-major writes disjoint: `img[y * w + x] = ...` (directly or through a local bound once) with `x` counting `0...w` in a nested loop runs rows across cores, as game of life, mandelbrot and image loops are written.
- Runtime: `Par.Fork` offers its left half as one thread-pool work item instead of a `Task` (no Task, wrapper closure or continuation per fork); the join runs an unclaimed half inline, spins briefly, then blocks on the job. When the right half throws, the left half finishes before the exception unwinds.
- Fix (ADR 0008): a counted loop whose body read the array it writes at another index (`row[j] = row[j - 1] + ...`) was auto-parallelized from 65,536 iterations and could compute wrong values. Such loops stay serial; stencils over a different array stay parallel behind a runtime `ReferenceEquals` alias check.
- Fix: an exception raised inside an auto-parallelized loop reaches the caller as itself, as in the serial loop, not wrapped in `AggregateException`.
- Runtime: `Par.Range`/`Reduce`/`MapSum` run on a lean chunk scheduler (`ParJob`) instead of `Parallel.For`: one allocation per loop, an atomic chunk counter, and the calling thread works through chunks itself, so nested parallel loops cannot starve the pool and no TPL machinery is JIT-compiled at startup.
- Shift counts are modular (ADR 0013): the `+ - *` computing `x << n` drop their overflow checks.
- A self-recursive function over primitives and primitive arrays compiles fully optimized from its first call (`AggressiveOptimization`). Suite queens: 0.92 s → 0.81 s (C# 0.91 s).
- `q, r = 7.divmod(2)`: multiple assignment destructures arrays and lists.
- Hex and binary literals accept `_` separators and type suffixes (`0xFFFF_FFFF_i64`, `0xFF_u8`).

- `samples/wonderland3d`: Alice-authored procedural 3D Wonderland film, interactive preview, deterministic 30-second MP4 export, GLSL bloom and an original synthesized stereo score (ADR 0012).

- Windows installer: launch Microsoft's .NET SDK installer with a child-process execution policy override, preserving the user's policy and reporting SDK installation failures.

- Root npm commands build/test Alice and generate VSIX/Zed packages from the companion `extensions/` checkout.

## 0.11.2 - 2026-10-16

- Fix editor document-symbol ranges after edits and mixin expansion: outlines use current source declarations and keep selections inside their full ranges.

## 0.11.1 - 2026-10-16

- `alice lsp --stdio`: incremental editor diagnostics, completion, .NET/NuGet members, auto-import quick fixes, hover with XML documentation, definitions, document symbols, formatting and signature help (ADR 0011).
- Editor project assistance: NuGet ID/version completion in `alice.toml` and `.alproj`, setting hover, manifest/XML formatting, and restored C#/NuGet/framework references.
- Rust editor adapters in the companion extensions repository: VS Code/VSCodium napi-rs client, grammar, snippets and icons; Zed Rust/WASI client and pinned tree-sitter highlighting.
- Project metadata uses collectible contexts without locking C# dependency DLLs; rebuilt APIs refresh in the editor.
- Fix incomplete parameter lists looping at EOF; serialize shared CLR type lookup for concurrent editor queries.

## 0.11.0 - 2026-10-16

- Toolchains (ADR 0010): `alice toolchain list|available|install|uninstall|default|pin|unpin|which|show`; versions side by side in `~/.alice/toolchains`, `alice-toolchain` pin files, `ALICE_TOOLCHAIN`, and `alice +VERSION <command>`.
- Updates: `alice self update|check|uninstall`, `alice upgrade`; a daily background check prints a one-line notice when a newer stable exists (off in CI and with `ALICE_NO_UPDATE_CHECK=1`).
- Install: `curl -fsSL https://alice-lang.org/install.sh | sh` or `irm https://alice-lang.org/install.ps1 | iex`; an `Installers` workflow tests both on clean Windows, Linux and macOS runners after every release.
- Installers: `scripts/install.sh` and `scripts/install.ps1` verify SHA-256, install the .NET 10 SDK per-user when missing (no admin rights, no Visual Studio), and set PATH. They are attached to every release and served by the site.
- CLI: `alice doctor` checks the environment; `alice completions bash|zsh|fish|pwsh`.
- REPL: live syntax highlighting, bracket matching, grey history hints (→ accepts), ↑/↓ prefix search, Ctrl-R reverse search, word motions and kills, a column completion menu, auto-indented continuation lines, `:type`, `:history`, `:clear`.
- Release: nightly channel (manual `channel: nightly` or a daily schedule) publishes `X.Y.(Z+1)-nightly.YYYYMMDD` as the rolling `nightly` prerelease; unchanged commits are skipped. `alice version` reports the full informational version.

## 0.10.1 - 2026-10-16

- Fix: escape package-mode brackets in the interactive REPL banner and `:help` so they render without Spectre.Console markup exceptions.

## 0.10.0 - 2026-10-16

- Fix: manual Release runs publish the validated version's GitHub release after packaging, including when the full test stage is skipped.
- Fix: build and smoke-test the ReadyToRun `osx-x64` package on `macos-15-intel`; reject mismatched packaging hosts before publishing.
- Release: optional `skip_tests` checkbox for manual runs and `RELEASE_SKIP_TESTS=true` Actions variable for tag pushes; package smoke checks remain required.
- Fix: package smoke checks recognize the current `alice test` summary (`2 examples, 0 failures`).
- Docs: feature a captured Celeste Classic gameplay demo in the README gallery, with sample instructions and original credits.
- Tests: use the platform newline in REPL assertions and tolerate Windows assembly locks during temporary-directory cleanup.
- Fix: generated-project type checking retains ASP.NET and other framework-pack references instead of dropping every SDK pack.
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
- `for await x in stream` (IAsyncEnumerable); explicit generic method arguments on member calls (`Channel.CreateUnbounded<Int32>()`).
- `alice test`: RSpec-style specs, matchers, hooks, focus/skip/pending, xUnit `@[Test]`/`@[Cases]`, `file:LINE`/`-e`/`--seed`/`--fail-fast`/`--profile`, doc/progress/JUnit output; app top-level code is not run in test builds.
- Block bodies carry `#line` (stack traces and caller lines point at Alice source); block values passed to `Action` parameters are discarded; Prelude zero-arg functions callable without parens; `include` is no longer reserved outside type bodies.
- Block iteration over generic collections of Alice types (`List(Entity)`) now lowers to loops instead of closure calls; `benchmarks/alice/celeste_update.al` (headless Celeste update benchmark).
- CLR generics over Alice types (`List(Entity)`, `Dictionary(String, Entity)`) are fully typed via stand-in proxy types: members, indexers and element types resolve (was untyped, e.g. `Count()` via LINQ). Celeste update benchmark 3.5 → ~1.9 ms.
- Native calls: leaf `@[Native]` functions skip the GC transition; `@[Native(fast)]`, `lib` `@[Fast]` opt in; `Bool` marshals as 1-byte C bool.
- `each` over a `List` iterates its backing span when the body provably can't modify the list. `benchmarks/run.sh`: interleaved rounds (ROUNDS, default 3; pass names to run a subset), median + spread, ratio vs hand-optimized C#.
- `benchmarks/run.sh`: ~37 s for a full run (was 10+ min): incremental builds, ReadyToRun compiler (compile 1.8 s → 0.8 s per file), parallel compilation, 7 trials with 0.3 s warm-up.
- CI: the full test suite runs on ARM64 macOS; the `osx-x64` release package is built and smoke-tested on an Intel runner. Release packages are ReadyToRun (faster compiler startup).
- CLI: every usage mistake is `error:` + `hint:` (exit 2) or a missing file/dir (exit 1) with "did you mean"; no stack traces (exit 70 + `--verbose` for internal errors); no more `.alproj` in messages.
- Packages: `alice remove`, `list`, `search`, `install` (`--tool`), `cache`; restores share the standard .NET NuGet cache.
- Errors from the generated C# are translated (AL0300–AL0315) with Alice type names; no `CS####` or `global::` in messages.
- New tagline: "curiouser and faster: write it like Ruby, run it like C, on all of .NET".
- REPL (`alice` / `alice repl`): Julia-style help/shell/package modes, multi-line input, history, Tab completion, `ans`, :reset/:load/:save/:emit/:time; piped mode for scripts.
- Brand: white-rabbit mark (question-mark ear, diamond + spiral eyes), app icon, logo/wordmark (light/dark), mono mark, Ace-of-Rabbits card, social preview, PNG/ICO exports (`assets/brand`); `alice.exe` carries the icon.
- Fix: auto-parallel loop helpers in instance methods can read `@ivars` (were emitted static → C# error).
- Constants holding array literals infer their type (`ROWS = ["a", "b"]`); `String#each_char_with_index`.
- Sample `samples/life`: Game of Life (terminal animation, PPM frame export → `docs/media/life.gif`), specs.
- Licensing: source code under Apache-2.0 (`LICENSE`, `NOTICE`); brand assets © Julia Klee, all rights reserved (`assets/brand/LICENSE.md`).
- Fix: diagnostics and stack traces inside lowered blocks map to the right Alice line (synthesized statements re-anchor `#line`).
- `to_i`/`to_f` for UInt8/Int8/Int16/UInt16/UInt32/UInt64.
- Sample `samples/raytracer`: parallel ray tracer (shadows, mirrors, supersampling), PNG and orbit GIF in `docs/media/`.
- Fix: array patterns accept literal elements (`in ["go", dir]`, `in [1, _, x]`); literals were wrongly treated as bindings.
- Fix: `instance_eval` as the only statement of a block.
- Sample `samples/wonderland`: text adventure written as a Ruby-style DSL (yield builders, instance_eval, case/in).
- Fix: overloads of .NET methods are chosen by argument types (`Math.Floor(Float64)` no longer picks `Decimal`).
- Branch locals (also destructuring) are visible after `if`/`case` inside blocks and loops, like Ruby.
- Project builds type-check in `alice gen`: errors are reported in Alice terms, never raw `csc` output; MSB3073 noise removed.
- Sample `samples/fractal`: parallel Mandelbrot zoom; README rewritten with GIFs, architecture, capabilities, roadmap.
- Fix: `@[Native]` builds on Windows (no `-fPIC`/`-lm` for the MSVC target).
