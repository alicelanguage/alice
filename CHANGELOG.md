# Changelog

## 0.13.0 - unreleased

- Version 0.13.0. This release was tracked as 0.12.3 until its scope (FFI, NuGet distribution, GPU, Avalonia, EF Core, source generators...) made a minor version the honest number. A bare `alice prove` skips nested directories that have their own `alice.toml`.
- Integration tests delete their temporary projects, feeds and NuGet caches. A full session had filled a 7.7 GB `/tmp` RAM disk with 2021 leftover directories, 175 MB per SDK-package run.
- `gpu ... end` regions keep the arrays of the `gpu for` loops inside on the device: each is uploaded once, and written ones are copied back at `end`. A CPU read inside the region is a compile error. 51 launches over 1M floats: 29 ms in a region against 132 ms with per-launch transfers.
- `gpu for` reductions: `t += e` into an outer Int32/Int64/Float32/Float64 is an atomic add on the device (100k-element Int64 sum, exact on the OpenCL GPU and the CPU accelerator).
- `gpu for i in a...b` (ADR 0021): the loop body is compiled to an ILGPU kernel and run on the preferred device. It ran on this machine's OpenCL GPU, with results identical to ILGPU's CPU accelerator, which `ALICE_GPU=cpu` forces. Admission is the `par for` independence proof plus GPU rules (arrays of numbers and numbers; arithmetic, indexing, `Math.*`, conversions; no calls, allocation or reductions), each rejection naming its cause. Kernel bodies are unchecked and not FMA-contracted: the device compiler contracts, and an explicit `FusedMultiplyAdd` was rejected by the OpenCL driver.
- GPU kernels in Alice: `examples/gpu/ilgpu` runs an Alice module function as an ILGPU kernel on the preferred device (it ran on this machine's OpenCL GPU; CUDA works the same, and ILGPU's CPU accelerator is the fallback). Covered by an integration test. To get there: a method reference to a static method lowers to the method group, not a wrapping lambda (C# caches the delegate, and APIs that need the real method get it); C#-style type arguments `ArrayView1D<Float32, Stride1D::Dense>` are accepted inside generic calls, with `>>` split; explicit type arguments work on generic extension methods.
- Avalonia UI: an `.axaml` window with its code-behind in Alice (`examples/ui/avalonia`, run headless in an integration test). Fixes on the way: the compiler reflected over reference assemblies (NuGet `ref/`, a project's `obj/.../ref/`), whose stripped private members crashed type loading (`TypeLoadException` on Avalonia's `StyledElement`), and now reflects over the implementation assemblies, while Roslyn still compiles against the references; `src/**/*.axaml` are `AvaloniaXaml` items in the generated project; extension methods inherited by a .NET base class are callable with implicit `self` (`FindControl<TextBlock>("x")`); a capitalized generic call without a receiver parses; and a paren-less member of unknown type standing alone as a statement is a call.
- Type-aware references and rename. The language server binds the project and keeps only occurrences resolving to the symbol under the cursor, or to an override of it in the same class hierarchy: `Square#area` renames without touching `Invoice#area`. Unresolved code falls back to name matching.
- Owned lists and hashes of any element type (`List(String)`, `Dictionary(String, Int32)`), not only of numbers. Only the collection must stay local, and `Clear` on release drops element references, so the pool keeps nothing alive.
- Class discovery: `Controller.subclasses`, `Controller.descendants` and `ObjectSpace.each_object(typeof(Controller)) { |c| }` over the program's own classes, which is what a framework needs to find controllers or plugins. Live-object enumeration is not offered: it has no safe .NET equivalent.
- Refinements: `refine String do ... end` inside a module is a monkey patch visible only in files that write `using <module>`. In C# it is an extension class in namespace `__Refine_<module>`, imported only by those files, so the scope holds there too.
- Forks of three calls in one expression (`trib(n - 1) + trib(n - 2) + trib(n - 3)`) via a three-way `Par.Fork`: `trib(32)` 335 -> 43 ms on 12 cores. A failing branch's exception surfaces once every branch has finished.
- Fix: an assignment inside a branch of a block now assigns the enclosing method's variable, as in Ruby. Before, `h.each { |k, v| best = v if v > best }` declared a new block-local `best`, and the method's `best` silently stayed 0. Found while checking the owned-hash benchmark's checksum, which was 0 under the old compiler too.
- Owned hashes (ADR 0016 amendment): a local `Dictionary(K, V)` of numbers that never escapes is pooled and its buckets reused (`benchmarks/gc/hashes.al`: 137-146 -> 97-104 ms, 2239 -> 0 MiB, 374 -> 0 gen0 collections). `Keys`/`Values` views count as escapes. `h.each { |k, v| ... }` on a Dictionary is inlined to `foreach (var (k, v) in h)`, with no closure or delegate.
- Owned lists (ADR 0016 amendment): a local `List(T)` of numbers that never escapes is rented from a per-thread pool and released at its last use, keeping its grown backing array. A scratch list built per call (`benchmarks/gc/lists.al`, 200k calls) took 59 ms, allocated 2926 MiB and ran 489 gen0 collections; now it takes 31 ms with 0 MiB and 0 collections. Views, spans, enumerators, storing and capturing keep a list on the GC, and a returned list moves out unreleased.
- View models for XAML UIs: `event PropertyChanged : PropertyChangedEventHandler` (capitalized event names, required to implement .NET interface events) and Ruby's private `@count` beside a public `def count` (the field takes another C# name instead of colliding). An `INotifyPropertyChanged` + `ObservableCollection` view model is tested.
- Language server: find references and rename. Matches come from the lexer, so strings and comments are left alone but `#{...}` holes are found. Top-level names are renamed across the project and locals within their enclosing `def`; an invalid new name is refused.
- Incremental package builds: `alice gen` runs only when a source, the reference or analyzer list, the compiler or the project changed (MSBuild `Inputs`/`Outputs` on `AliceGenerate`, with a sources list so that added and deleted files rebuild). A no-op `alice build` takes 1.0 s instead of 2.35 s. Covered by an integration test for no-op, added-file and deleted-file builds.
- `partial def` (no body): a declaration a source generator completes, for `[GeneratedRegex]` (regex compiled at build time) and `[LibraryImport]` (.NET's source-generated P/Invoke) next to Alice's own `lib`/`fun`.
- `docs/production.md`: what is ready for production, the test behind each claim, recommended `alice.toml` settings per workload, and the known gaps.
- F# consumes Alice libraries built with `naming = "dotnet"`: `Geometry.Distance`, `IsEmpty`, records, and sum types matched with `:? Shape.Circle as c -> c.R` (`Integration/FSharpInteropTests`, a real `.fsproj`).
- Roslyn source generators work on Alice code (`[JsonSerializable]` contexts for reflection-free, NativeAOT-safe JSON; `[GeneratedRegex]`; `[LoggerMessage]`; NuGet generators). `alice gen`'s type check now runs the generators MSBuild supplies (`@(Analyzer)`) instead of failing on members they add, and single-file builds find the framework's generators in the targeting pack. Framework generators load only when their attribute appears: hello world compiles in 1.06 s as before, not the 1.55 s it took with every generator loaded.
- Multi-line blocks inside call parentheses parse: `Task.Run(->() {` followed by statements on separate lines. The lexer dropped every newline inside `(`/`[`, so statements in a nested `{ }` ran together. It now tracks the innermost bracket.
- FFI stress tests (`FfiStressTests`): strings at the 85/86-char stack-buffer boundary with 2- and 4-byte UTF-8, lone surrogates (encoded as U+FFFD), 8 threads x 20k concurrent string calls, repeated callback exceptions, and empty and 16 MiB pinned arrays.
- Entity Framework Core works from Alice. `examples/data/efcore` defines a `DbContext` subclass with an `OnConfiguring` override and `DbSet` properties, and runs LINQ queries that EF translates to SQL from the Alice lambdas (`WHERE "b"."rating" > 3`). It is covered by an integration test. To get there: extension methods from any referenced assembly are callable without parentheses (`query.ToQueryString`), not only LINQ's; `obj.tap { |o| ... }` and `obj.then { |o| ... }` were added; and a multi-statement block passed where an `Action` is expected drops its tail value.
- `x.try &.m(args)` (Crystal): the safe call `x&.m(args)`, no longer "planned".
- Lambdas as blocks: `xs.sum(&->sq)`, `xs.select(&->(x : Int32) { x.even? })` and `xs.map(&f)`. A lambda literal or method reference passed this way is inlined into the loop like a literal block, with no delegate.
- Named tuples: `{name: "ada", age: 36}` and the type `{min: Int32, max: Int32}` are C# named tuples (`(string name, int age)`, no allocation), read with `t.name` or `t[:name]`, where an unknown element is a compile error. Hashes keep `=>`.
- Assignment as a condition: `if user = find(id)` and `while line = reader.ReadLine`. The `while` form assigns before every test, and `elsif x = f` evaluates `f` only when the earlier conditions failed.
- Flow narrowing for nullable numbers: inside `if n` and after a guard clause (`return unless n`, `break`/`next unless n`, `raise ... unless n`), an `Int32?` local reads as `Int32` (`n.Value`). Narrowing stops where the branch reassigns the local. C# already narrows nullable references.
- Conditions use Ruby truthiness: `if s`, `unless t`, `while q`, `s ? a : b` and `!s` on a nullable or reference value are nil checks, and `Bool?` counts only when it holds `true`. Before, `if s` on a `String?` failed with "expected Bool", despite the guide recommending it.
- `gc = "auto"` caps server GC's gen0 budget at 16 MB (`System.GC.Gen0MaxBudget`). Polyglot suite at its default size (cold runs of 60-110 ms, before vs after this release): uncapped server GC added 6-17 ms to some workloads (merkle 79 -> 93 ms); with the cap the cost is 0-10 ms (merkle 87.5 ms). Large parallel heaps keep the win (tree(23) build 116 vs 698 ms on workstation). Short-lived CLIs that must start fastest can set `gc = "workstation"`.
- Examples: `data/trees.al`, `dotnet/library_api.al`, `interop/libc.al`, and `tools/wand` (a generator CLI packaged as a .NET tool). `Integration/ExampleTests` runs every standalone example and checks its output.
- `lock obj ... end` (C# `lock`; `System.Threading.Lock` or any reference). A value-type target is a compile error, and writes inside are still race-checked: `Mutex(T)` remains the way to share Alice data.
- Method references: `->sq`, `->sq(Int32)`, `->shape.area`, `->Geometry.dist`. A reference is a lambda whose parameter types come from the method (explicit types pick an overload), so it passes as a block value, a `Func` or any delegate.
- Building and folding sum types fork. A variant constructor (`Tree::Node(l, r)`) is allocation, not an effect, and a `case` whose subject, patterns, guards and arms are pure is pure. Before, neither `build` nor `total` of `benchmarks/inet/tree.al` forked.
- `[runtime] gc = "auto"` is the new default. The build picks server GC (DATAS) when the compiled program forks or runs parallel loops, and workstation GC otherwise. Parallel code building long-lived data was collection-bound on workstation GC: tree(20) build took 85 ms (57 gen0 collections copying the live tree) against 24 ms on server GC, and tree(23) took 698 vs 137 ms. A serial hello world starts in 0.02 s either way, with 27.9 vs 28.9 MB. `workstation`, `server` and `low-memory` still force a mode.
- `Span(T)` / `ReadOnlySpan(T)` take `each`, `each_with_index`, `sum`, `count` and the other inlined iterators, lowered to an indexed loop over the span (no allocation, no enumerator). Before, they failed with AL0312.
- A `struct` with initialized properties (`property port : Int32 = 80`) failed to compile, because C# runs initializers only through a declared constructor. It now gets one. Field-only attributes on properties (`FieldOffset`, `MarshalAs`, `NonSerialized`, `ThreadStatic`) target the backing field, so `StructLayout(Explicit)` unions for C interop compile.
- Alice on NuGet (ADR 0020): `Alice.Sdk` is an MSBuild project SDK carrying the compiler, so `<Project Sdk="Alice.Sdk/x.y.z">` compiles `.al` files in any project with nothing installed (tested with an empty NuGet cache). `Alice.Cli` installs the `alice` command with `dotnet tool install -g Alice.Cli`. `Alice.Runtime` is packable for C# hosts. `scripts/pack-nuget.sh` builds all three packages, and the release guide covers pushing them.
- `alice new <name> --tool` creates a .NET tool package (`kind = "tool"`, `command`, an ARGV template). `alice new --lib` now writes `description` and `naming = "dotnet"`, so a new library is ready for `alice publish`.
- `[package] naming = "dotnet"` (ADR 0019): a library's public members compile to .NET names (`total_area` -> `TotalArea`, record `x` -> `X`, `empty?` -> `IsEmpty`), so C# and F# callers see a conventional API. Alice code keeps the Alice spelling, and `send`/`respond_to?` fall back to the .NET name. Private members and Ruby hooks are unchanged. Default `"alice"` leaves output as before.
- `await using r = x ... end` in `async def`: `DisposeAsync` is awaited at `end` (EF Core contexts, async streams). `alice fmt` now indents `using` / `await using` blocks, which it never did, as well as interface default methods and `unsafe def`.
- Declaring .NET events (`event clicked : EventHandler`, raised with `clicked&.Invoke(self, args)`), delegate types (`delegate Op(a : Int32, b : Int32) : Int32`) and default interface methods (an interface `def` with a body). An Alice library can now expose the same API shapes a C# library does.
- Nullable type arguments of .NET generics: `Dictionary(String, String?)` and `List(String?)` emitted as `string` (CLR generics carry no nullable-reference annotation), so `d[k] = nil` was rejected as AL0310. They now keep `string?`, and reading the value still has to be nil-checked.
- Iterators: a method returning `IEnumerable(T)`, `IEnumerator(T)` or `IAsyncEnumerable(T)` with no block parameter is a generator. `yield x` is `yield return`, a bare `return` is `yield break`, and evaluation is lazy (`nums(1_000_000_000).Take(3)`). `async def` streams work with `for await`. A class listing `IEnumerable(T)` as a base gets the non-generic `GetEnumerator` bridge. Ruby block `yield` is unchanged.
- Attributes on record parameters: `record Span(start : Int32, @[JsonPropertyName("end")] stop : Int32)`. They target the generated property, where System.Text.Json and other serializers read them; `@[param: X]` targets the parameter. JSON keys that are Alice keywords (`end`) now map to typed records.
- `fun` parameters can be `out x : T` / `ref x : T`. C receives a pointer to the caller's variable, pinned for the call, so out-parameters and structs passed by pointer need no `unsafe` (`LibC.clock_gettime(0, out ts)`, `LibC.frexp(8.0, ref e)`). `out` locals at the call site get the declared type.
- Native callbacks: a `fun` parameter of type `Callback(A, B, R)` takes a lambda literal, lifted to a static `[UnmanagedCallersOnly]` C function (`qsort` with an Alice comparator). No delegate, no GC handle, no allocation; the call site needs no `unsafe`. A capturing lambda is error AL0215. An exception raised in the callback no longer kills the process: it is caught and rethrown once the C call returns.
- `unsafe ... end` blocks and `unsafe def`: the only places where `Pointer(T)` values (from `fun malloc ... : Pointer(UInt8)`, `p[i]`) are allowed. Elsewhere they are AL0214 with a hint, instead of a raw C# error. Safe FFI types (numbers, strings, arrays) need no `unsafe`.
- FFI (ADR 0018): `lib` `fun` takes `String`/`String?` parameters as UTF-8 C strings on every OS (stack buffer, no allocation; an embedded NUL raises instead of truncating), returns `String?`/`String` as borrowed UTF-8 (`NULL` is `nil`, or raises for `String`), and pins `Array(number)` arguments. Every import is now blittable behind an inlined wrapper: no marshalling stubs, so NativeAOT has none to precompile. Fixes heap corruption: a `String` return used to be freed by the .NET marshaller (`getenv` crashed with `free(): invalid pointer`). Per call, a 12-char string costs 15.5 ns against 15.6 ns for .NET's UTF-8 marshaller.
- .NET members take Alice spellings on instances too: `sw.elapsed.total_milliseconds`, `sb.append_line(x)`, `sw.is_running`, and predicates `d.contains_key?(k)` / `uri.absolute_uri?` / `dir.exists?` (tries `IsX`, then `X`; must return `Bool`). Before, only statics (`Math.sqrt`, `Stopwatch.start_new`) were mapped. A Prelude method of the same name still wins wherever its receiver type fits (checked from the Prelude's signatures), so `s.split`, `xs.sum` and `task.wait` keep their Ruby meaning.
- Fork/join: a forking method gets `m__serial`, a clone with no forks whose recursion stays in the clone, and the sequential branch of every fork calls it. Before, every call below the fork depth read the thread-static `Par.Forkable`, which made `pow2` 2x slower than plain recursion. Compiled `pow2(26)`: 51.3 -> 15.7 ms. Found by `benchmarks/inet`, an HVM2-style interaction-net reducer on .NET. It runs (60-100 M interactions/s per thread) but stays 17-30x slower than serial code on 12 cores for this workload, so fork/join stays the parallel model ([README](benchmarks/inet/README.md)).
- `alice pack` and `alice publish`: Alice libraries and tools ship as NuGet packages. `[package]` takes `id`, `description`, `authors`, `license`, `repository`, `homepage`, `tags`, `copyright`, `icon`, `readme` (default `README.md`); bad ids, versions and kinds fail before `dotnet pack`. `kind = "tool"` (with `command`) packs a .NET tool, so a CLI or framework generator written in Alice installs with `dotnet tool install`. Library packages carry `Alice.Runtime.dll`, so C# consumers need nothing else. `alice publish` pushes to nuget.org (`--api-key` or `NUGET_API_KEY`, never echoed), a private feed, or a local folder; `--dry-run` packs only. Tested end to end: pack, push to a folder feed, `dotnet tool install`, run.
- Owned results lent straight to a borrowing parameter (`t += dist(seq(a), seq(b))`) live in a temporary for the statement and go back to the pool after it. `benchmarks/gc` `rows`: 0.06 MiB per run down to 0.00.
- `alice build file.al --native` keeps the directory layout of the entry file and everything it `require`s. A flat copy broke relative `require` paths, so multi-file programs could not be built natively. `benchmarks/alice/celeste_update` now builds: 1.8 ms per trial native against 11.2 ms under the JIT (the program ends before tiered compilation warms up), 15.5 MB against 40 MB peak.
- Presized collections: `xs = List(T).new` or `SoA(T).new` directly followed by a counted loop that adds exactly once per iteration (no `break`/`next`) is sized to the trip count first. SoA stores gain `reserve(n)`. `benchmarks/alice` `particles`: 128 to 61 MiB allocated, gen2 collections 10 to 2 (C#: 3), 9% less wall time. Found with a per-process profile (JIT time, methods compiled, bytes allocated, GCs) of every benchmark against its C# baseline; `wordfreq` reports 85-158 ms of JIT time, but it is mostly tier-1 recompilation on the JIT's background thread: turning tiering off cuts it to 35 ms with no wall-time gain and a 19% slower kernel, and forcing full optimization makes the run 26% slower. The default tiered + PGO setup stays.
- Polyglot suite profile (17 workloads, same per-process hook, Alice vs C#): Alice is faster and allocates far less on every substantial workload (tree-radix 117 vs 486 ms and 201 vs 1031 MiB; editdist 0.6 vs 1025 MiB with 0 vs 170 collections; symreg 116 vs 571 ms; hashmap 181 vs 409 ms; merkle 469 vs 663 ms). On the ten 40-60 ms workloads Alice is 5-10 ms slower; an empty Alice program starts as fast as an empty C# one (8 ms, 33 vs 30 methods), and the gap came from the Alice adapter returning `Dictionary(String, Object)` results that serialize through boxing. With typed records (same JSON, outputs byte-identical on all 17 workloads) the small workloads run within 2-5 ms of C# or ahead (histogram 39 vs 37 ms, symreg 40 vs 41, kmeans 43 vs 47). `lexer` keeps a dictionary: its JSON key `end` is an Alice keyword, and record parameters do not take attributes such as `JsonPropertyName` yet.
- `[runtime] globalization = "invariant"` in `alice.toml`: no ICU at run time, 2-4 MB less memory on every program measured. Strings sort ordinally (Ruby's order) in that mode; the default stays `culture`.
- Runtime knob survey (median of 5, four program shapes): turning off tiered compilation, PGO or quick JIT for loops helps short programs and slows long ones; ReadyToRun for `Alice.Runtime.dll` and GC gen0/conserve tuning gave no gain. The performance guide records the results and recommends `--native` for short-lived programs.
- Guide: no `rescue` modifier and no `<` on strings (use `<=>`) in the Ruby habits table.
- Every `benchmarks/alice` program builds with NativeAOT with no trimming or AOT warnings and prints the same results as under the JIT, using 20-70% less memory.
- Forks of `1 + l.check + r.check` over nullable tree children stay serial on purpose: forking it measured 21.5 -> 32.6 ms on `benchmarks/gc` `trees` (tasks of under a microsecond). The reason is recorded in the code and a test.

## 0.12.2 - 2026-10-22

- Explicit, checked paradigms on top of inference (ADR 0017):
  - Effects: `pure def` (AL0210) and `safe def` (secure execution, no ambient authority, AL0212).
  - Parallel let: `par a, b = f(x), g(y)` (Bend's fork without the recursion rule, AL0211).
  - Inlining: `inline def`.
- Ownership:
  - `own` parameters (the caller moves in).
  - `borrow` parameters (checked, AL0213).
  - `scope ... end` lifetime regions (arrays must be owned and are released at `end`).
  - In-place `xs = xs.map { }` for owned arrays. A 3-stage map over 1M `Float64`: 4.0 ms and 3 B per call, against 9.3 ms and 32 MB for hand-written C# loops.
- Owned results (ADR 0016 amendment): an array returned by a method that returns only its own owned local is owned by the caller and recycled. `benchmarks/gc` `scratch`: 3.81 MiB per run down to under 400 B, 0 collections.
- `alice run file.al` caches the compiled image by content hash (`~/.cache/alice/run`, `ALICE_NO_CACHE=1` to disable): an unchanged program starts in 0.13 s instead of 1.2 s.
- Shared immutable objects: `T.new(constants)` of an immutable class whose identity the program never observes is one static instance. `benchmarks/gc` `trees`: 68 to 34 MiB per run, half the collections, about 1.35x faster than C# (21.5 vs 28.9 ms).
- Interop: `fun` signatures over numbers and `Bool` are blittable (a `Bool` crosses as one byte behind an inlined wrapper), with no marshalling stub; a `Bool` C call is 2.7x faster. `@[Fast]` on a `Bool` function no longer fails at run time. Imports use `ExactSpelling`.
- Rules and proofs: `rule name(x in lo..hi, b : Bool) ... end` (RULES.al) declares a pure property over a finite domain. `alice prove` checks every case and writes `PROOF.al`, a certificate tied to a hash of the sources. `alice prove --check` fails when a rule fails or PROOF.al is stale. Domains over 2^32 cases are AL0214. The repository's `RULES.al` proves Alice's numeric semantics; `dotnet test` checks it.
- `divmod`, `tdiv`, `remainder` and `modulo` count as pure.
- `alice explain file.al`: per method, the optimizations applied (fork/join, parallel loops, owned arrays, in-place maps, shared objects, stack arrays, SIMD, fast native calls).
- `alice guide [performance|proofs|interop]` prints the guide from `guide/`, embedded in the CLI, for people and AI agents; every `alice` code block in it is compiled by the tests. `alice agents [--write]` prints or merges an Alice block into a project's `AGENTS.md`: workflow, Ruby/Crystal habits that are wrong in Alice, error codes. `alice new` writes `AGENTS.md` with that block and a `CLAUDE.md` that imports it (`@AGENTS.md`).
- Tests that run programs in `ExplicitParadigmTests` join the `console` collection.
- Fork/join of independent redexes (HVM-style): a statement whose value is a pure arithmetic term with exactly two calls to pure user methods, one recursive, forks the two calls without named results (`fib(n - 1) + fib(n - 2)`, `1 + l.count + r.count`). Calls under `&&`, `||`, `??` or `?:` are never forked, and a fork never captures a nullable local, because the helper would lose flow narrowing. Naive `fib(38)` on 12 cores: 250 ms -> 145 ms.

## 0.12.1 - 2026-10-22

- Loops over an owned array bounded by its fixed length (`for k in 0...n` over `Array(T).new(n)`) compare against `buf.Length`, so the JIT removes their bounds checks as it does for C#'s `new T[n]` (ADR 0016 amendment).
- README: mutability, ownership, fork/join and benchmark sections.
- `benchmarks/gc`: allocation-heavy workloads (rolling rows, per-call scratch buffers, 4 MiB frames, object trees) in Alice with inferred ownership, Alice with explicit `own`, and C#, under workstation and server GC. Reports time, allocated bytes, collections, GC pause and peak memory, with charts. Alice runs `rows` and `frames` with 0 collections (C#: 74-1002) and is about 2x faster than C# on `scratch` and `frames` under server GC.

## 0.12.0 - 2026-10-22

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
- Syntax: `par for i in a...b` parallelizes at any trip count and is compile error AL0209 when iterations are not provably independent; `unchecked ... end` is a wrapping region for integer `+ - *` (ADR 0013). `alice fmt` indents both.
- Benchmarks: `run.sh` runs Alice with the same server GC as the C# baselines; run 8 recorded (Alice at or ahead of idiomatic C# on all eight). The recursive-kernel rule requires a primitive result, so object-building recursion keeps tier-1 PGO.
- `examples/verify.sh` expects 0.12 float printing (`30.0`).
- Version 0.12.0.
- JIT tiering (ADR 0014): methods with a loop nest, and primitive-only static functions that loops call, compile fully optimized from the first call instead of running instrumented tier-0 code until OSR / delayed tier-up (suite nbody 151 → 78 ms, terrain at 512x512 143 → 115 ms).
- Parallel loops probe before dispatching: the first two iterations run serially, and the rest goes to the pool only when it projects past the dispatch cost (25 ms cold, 50 us warm); the runtime scheduler samples a chunk the same way. Short loops in short programs no longer pay thread-pool startup (small mandelbrot 80 → 75 ms); large ones stay parallel (4M-pixel mandelbrot 247 → 157 ms vs serial). Parallel chunk bodies compile fully optimized.
- Floored `/` and `%` by a positive literal are branch-free (`floor_div_pos`, `floor_mod_pos`).
- Fix: a loop whose body assigns its own counter is never auto-parallelized.
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
