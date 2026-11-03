# Alice invariants

This file lists every guarantee the Alice compiler, runtime and tools make about programs they accept. Each entry
says what holds, where it does not apply, which tests pin it, and how to report a violation. A program that the
toolchain accepts and that breaks one of these statements is a bug in Alice, and it is in scope for the planned bug
bounty (terms to be published with 1.0).

Applies to: the latest release of the 1.x line and the current `main`, on the platforms in
[docs/releases.md](docs/releases.md), with both backends unless an entry says otherwise. Test paths are relative
to the repository root; `dotnet test` runs all of them, on Linux, macOS and Windows in CI.

## General exclusions

These are outside every entry below unless the entry says otherwise:

- Code that opts out on purpose: `unsafe` blocks and pointers, `lib`/`fun` imports (native libraries), and the
  inside of .NET libraries and NuGet packages you call. Alice checks its side of each call, not the callee.
- Direct use of `System.Reflection`, `System.Runtime.CompilerServices.Unsafe`, `Marshal` or similar .NET APIs that
  bypass the type system.
- The limits listed under "Not ready yet" and "Security limits" in [docs/production.md](docs/production.md). They
  are known and documented; a way around a limit that is not listed there is in scope.
- Error codes marked "planned" in [docs/errors.md](docs/errors.md) (features not shipped yet).
- Hardware faults, out-of-memory and stack overflow, a modified toolchain, or a modified .NET runtime.

## How to report a violation

Follow [SECURITY.md](SECURITY.md): report privately (the address and the GitHub private advisory are listed there),
not in a public issue. Include `alice --version`, the platform, the backend (`--backend il` or the default), the
smallest program that shows the problem, what this file promises, and what you observed.

## 1. Nil safety

**Statement.** A value of a non-nilable type (`String`, `Point`, `Array(T)`, ...) is never nil when read. A nilable
value (`T?`) must be checked (`if x`, `x || d`, `return unless x`, ...) before it is used as a `T`. A declared field
or getter of a non-nilable reference type that no `initialize` assigns is a compile error (AL0221).

**Scope and exclusions.** Values that come from .NET APIs declared without nullable annotations are trusted as
declared. Locals first assigned in only some branches, ivars created outside `initialize`, and `property`/`setter`
accessors are known limits (docs/production.md).

**Pinned by.** `tests/Alice.Tests/Compiler/NullSafetyTests.cs`, `tests/Alice.Tests/Compiler/BookIssuesTests.cs`,
`tests/Alice.Tests/Compiler/RunTests.cs`.

## 2. `val` is immutable

**Statement.** A name declared with `val` is assigned exactly once; any later assignment is a compile error (AL0203).

**Scope and exclusions.** The binding is immutable, not the object it refers to: `val xs = [1]` still allows
`xs[0] = 2` unless the value is itself immutable.

**Pinned by.** `tests/Alice.Tests/Compiler/PropertyTests.cs`, `tests/Alice.Tests/Tooling/LspTests.cs`.

## 3. No data races (AL0202)

**Statement.** In concurrent contexts (`spawn`, `Par.For`, `par_each`, `Task.Run` lambdas, `Actor` handlers, forks
and parallel loops) Alice code never writes state another concurrent context can read or write, unless it goes
through a synchronizing type (`Mutex(T)`, `Atomic`, `Channel`, `Actor`, `System.Collections.Concurrent`,
`Interlocked`). Violations are compile errors: AL0202 (race), AL0219 (use after send), AL0230 (message not
sendable), AL0231 (actor not isolated). Copies of shared references (`ys = xs`, conditionals, `for` variables) are
tracked.

**Scope and exclusions.** Threads started by .NET code the program calls, and state reached only inside .NET
libraries, are outside the check.

**Pinned by.** `tests/Alice.Tests/Compiler/RaceSafetyTests.cs`, `tests/Alice.Tests/Compiler/RacesAliasTests.cs`,
`tests/Alice.Tests/Compiler/ActorIsolationTests.cs`, `tests/Alice.Tests/Compiler/ActorsSendableTests.cs`,
`tests/Alice.Tests/Compiler/SharingTests.cs`, `tests/Alice.Tests/Compiler/BorrowSafetyTests.cs`.

## 4. Pooled buffers and borrows are safe

**Statement.** An array the compiler returns to a pool (ADR 0016) is the only reference to its buffer: it is never
read or written after it is released, never released twice, and never visible to two owners at once. A local that
breaks the rules silently stays an ordinary GC array. Written as a contract, `own` and `borrow` make breaking the
rules a compile error: AL0207 (owned value escapes), AL0208 (use after move), AL0213 (borrowed value escapes), AL0217
(conflicting borrow), AL0218 (stack value escapes). Borrows through virtual calls must hold for every override.

**Scope and exclusions.** Arrays stored in fields and arrays of references stay with the GC (not pooled). Buffers
passed to `lib` funs are pinned for the call only.

**Pinned by.** `tests/Alice.Tests/Compiler/OwnershipTests.cs`, `tests/Alice.Tests/Compiler/BorrowSafetyTests.cs`,
`tests/Alice.Tests/Compiler/StackArraySafetyTests.cs`, `tests/Alice.Tests/Compiler/OwnedBoundsTests.cs`,
`tests/Alice.Tests/Compiler/AllocationTests.cs`.

## 5. Only checked-pure code runs in parallel

**Statement.** The compiler parallelizes on its own (forks of independent calls, chunked loops, parallel reduces,
vectorized loops) only code its purity and race checks accept; when it cannot show that, the code stays serial. A
parallelized program prints the same results as its serial version. Asking for parallelism that cannot be shown safe
is a compile error: AL0209 (`par for`), AL0211 (`par a, b = ...`), AL0210 (`pure def`).

**Scope and exclusions.** Parallel floating-point sums and reductions reassociate: results may differ from the
serial order in the last bits (docs/performance.md). Integer results are exact. `gpu for` follows the same checks;
device numerics are those of the device.

**Pinned by.** `tests/Alice.Tests/Compiler/ParallelSoundnessTests.cs`, `tests/Alice.Tests/Compiler/ParallelizeTests.cs`,
`tests/Alice.Tests/Compiler/LoopDependenceTests.cs`, `tests/Alice.Tests/Compiler/PurityMutatesTests.cs`,
`tests/Alice.Tests/Compiler/ExplicitParadigmTests.cs`, `tests/Alice.Tests/Runtime/ParReduceTests.cs`.

## 6. Bounds safety, also in `@[Native]` functions

**Statement.** Every array, span and string index is in bounds or raises `IndexOutOfRangeException`; the compiler
removes a check only where it has shown the index is in range. `@[Native]` functions compiled to C keep the same
behaviour: out-of-range indexing, integer overflow and division by zero trap instead of continuing, and shift counts
are masked as in managed code. The managed fallback gives the same results as the native build.

**Scope and exclusions.** `unsafe` pointer access and memory handed to `lib` funs are excluded.

**Pinned by.** `tests/Alice.Tests/Compiler/NativeTests.cs`, `tests/Alice.Tests/Compiler/OwnedBoundsTests.cs`,
`tests/Alice.Tests/Compiler/LoopTilingTests.cs`, `tests/Alice.Tests/Compiler/PatternOptTests.cs`,
`tests/Alice.Tests/Runtime/Tensors/TensorTests.cs`.

## 7. Checked arithmetic and wrapping operators

**Statement.** Integer `+ - *` and `**` raise `OverflowException` on overflow; integer division and `%` by zero
raise `DivideByZeroException`; signed `/` and `%` are floored (ADR 0013). Conversions (`to_i32`, `to_u8`, `to_i`,
...) raise when the value does not fit, NaN included; only the `to_X!` forms wrap (integers) or saturate (floats).
Only the wrapping operators `&+ &- &*` (and `&+=` forms), `unchecked ... end` regions and shift counts (modular, low
5 or 6 bits) wrap, and a wrapping operator wraps only its own operation: checked arithmetic inside its operands still
raises. The compiler drops a check only where its range analysis shows the result fits. `@[Native]` C code follows
the same rules.

**Scope and exclusions.** `/` and `%` on `Any` values under the C# backend truncate (known limit,
docs/production.md); typed code and the IL backend are floored.

**Pinned by.** `tests/Alice.Tests/Compiler/PrimitiveTests.cs`, `tests/Alice.Tests/Compiler/RangeTests.cs`,
`tests/Alice.Tests/Compiler/ShiftCountTests.cs`, `tests/Alice.Tests/Tooling/ProveDifferentialTests.cs`,
`tests/Alice.Tests/Runtime/DynTests.cs`, `tests/Alice.Tests/Backends/AirToIlTests.cs`.

## 8. `safe def` is an allow-list

**Statement.** A `safe def` (and every constructor it calls) can only do what the allow-list permits: no file,
network, environment or process access, no `lib` funs, no reflection, `send`, `instance_variable_*` or
`method_missing`, no inherited .NET members or calls on receivers of unknown type, anywhere in the body (including
`using`, `spawn` and if-as-value). Anything else is AL0212. Prelude entry points marked `[Ambient]` are rejected.
`instance_variable_get/set` never write readonly fields and return copies of foreign or readonly arrays.

**Scope and exclusions.** The guarantee is about what the body can reach, not about time or memory use.

**Pinned by.** `tests/Alice.Tests/Compiler/EffectsSafeDefTests.cs`, `tests/Alice.Tests/Runtime/ReflectionSecurityTests.cs`,
`tests/Alice.Tests/Compiler/FfiTests.cs`.

## 9. Arena sum types are safe

**Statement.** A sum type the compiler places in an arena (ADR 0037) behaves exactly like the same type as ordinary
records: no value is read after it is consumed, freed slots are never reachable, and a value that escapes (stored,
compared, printed, passed to .NET, used after a move) moves the whole type back to records. With `affine enum` those
cases are compile errors instead: AL0240 (payload), AL0241 (escape), AL0242 (use after consume), AL0243 (borrow
consumed).

**Scope and exclusions.** Arena slabs are per thread; arena values do not cross threads except through forks the
compiler creates.

**Pinned by.** `tests/Alice.Tests/Compiler/AffineTests.cs`, `tests/Alice.Tests/Runtime/ArenaTests.cs`.

## 10. `alice prove` verdicts are sound

**Statement.** When `alice prove` reports a rule as holding, the rule returns `true` without raising on every input
of its domain. Enumeration runs every case, including domains that end at `Int32::MAX`/`Int64::MAX`. `--smt` (Z3)
replays every counterexample through the compiled rule and evaluates every statement, so a disagreement or a raising
call is `not proven` or a failure, never `holds`. A rule that times out is `not proven`. `alice prove --check` exits 1
on any failing, unproven or stale rule, and `PROOF.al` is tied to the sources.

**Scope and exclusions.** Rules with loops or recursion fall back to enumeration under `--smt`. The verdict is about
the rule as written, not about properties it does not state.

**Pinned by.** `tests/Alice.Tests/Tooling/ProveTests.cs`, `tests/Alice.Tests/Tooling/ProveCliTests.cs`,
`tests/Alice.Tests/Tooling/ProveDifferentialTests.cs`, `tests/Alice.Tests/Tooling/SmtProveTests.cs`,
`tests/Alice.Tests/Tooling/ProveSmtStatementsTests.cs`, `tests/Alice.Tests/Compiler/RuleTests.cs`.

## 11. IL output verifies

**Statement.** Assemblies the IL backend emits pass ILVerify with no errors other than the ones Roslyn's own output
for the same unsafe constructs gets (pointers, `localloc`, `calli`), and programs run with the same output under the
IL and the C# backends.

**Scope and exclusions.** Constructs the IL backend reports as unsupported are compiled by the C# backend instead.

**Pinned by.** `tests/Alice.Tests/Backends/AirToIlTests.cs`, `tests/Alice.Tests/Backends/AirToIlFfiTests.cs`,
`tests/Alice.Tests/Backends/IlBuilderTests.cs`.

## 12. Builds are deterministic

**Statement.** The same sources, compiler version and options produce byte-identical IL assemblies and PDBs (content-
hashed MVID and PDB id, metadata in definition order), and `PROOF.al` certificates are deterministic.

**Scope and exclusions.** The .NET SDK patch version and NuGet package contents are inputs: a different SDK or a
changed package may change the output. Determinism of the C# backend's Roslyn step is not pinned by a test yet.

**Pinned by.** `tests/Alice.Tests/Backends/IlBuilderTests.cs`, `tests/Alice.Tests/Tooling/ProveTests.cs`.
