# Memory safety and ownership on the .NET GC (proposed)

Status: **proposed** (ADR 0006). Nothing here is implemented unless marked *implemented*. *Implemented*: owned local arrays, `own`, moves and use-after-move (ADR 0016, AL0207/AL0208), `own`/`borrow` parameters (AL0213) and `scope ... end` lifetime regions ([language.md](../language.md#ownership-adr-0016)).

## Thesis

Alice does not allocate or free memory itself. The CLR already gives:

| Guarantee | Source |
|---|---|
| No use-after-free, no double free, no dangling heap refs | GC (tracing, compacting) |
| No out-of-bounds read/write | array/span bounds checks; `@[Native]` C checks every index (8a) |
| No type confusion | verifiable IL, type-safe casts |
| No dangling `ref`/`Span` to stack | Roslyn ref-safety rules + `ref struct` |
| No null deref (compile time) | nullable reference types; Alice makes warnings errors (*implemented*, ADR 0004) |

Ownership/lifetimes in Alice are a **static compile-time layer** for what the GC does not solve:
deterministic resource release, data races, mutable aliasing, zero-alloc paths, and fenced `unsafe`.

## Goals / non-goals

| Goals | Non-goals |
|---|---|
| Reuse GC for all memory reclamation | Custom allocator, manual `free`, refcounting |
| Deterministic release of `IDisposable` | Rust-style borrow checker over all references |
| Data-race freedom across `spawn`/`Channel`/actors | Lifetime annotations (`'a`) |
| Zero-alloc paths via C# ref-safety | Own escape analysis for stack values |
| Fence `unsafe` and native pointers | Banning `unsafe`/P/Invoke |
| Every check lowers to plain C# | Runtime checks that C# would not emit |

## Who enforces what

| Rule | CLR | Roslyn | New Alice pass |
|---|---|---|---|
| Memory reclamation, bounds, type safety | yes | - | - |
| `ref struct`/`Span`/`scoped` escape | - | yes | - |
| `readonly struct`, `in` params | - | yes | - |
| Nullability | - | yes (errors) | - |
| `checked` arithmetic | yes (throws) | emits `checked` | - |
| `unsafe` only in `unsafe` context | - | yes | syntax fence |
| `own` must be disposed / moved | - | - | yes |
| use-after-`move` | - | - | yes |
| `Send`-like check at `spawn`/`Channel.send` | - | - | yes |
| `val` reassignment | - | yes (via `readonly`/locals check) | yes for locals |

Any new Alice pass must also be ported to the self-hosted compiler (`selfhost/`, ADR 0003) to keep the bootstrap fixed point.

## Features

### 1. Deterministic resources: `using` / `own`

```crystal
using f = File.OpenRead(path)       # scope-bound
  data = read_all(f)
end

using f = File.OpenRead(path)       # declaration form; disposed at scope end
own s : Socket = connect(host)      # must be disposed, moved, or returned
```

```csharp
using (var f = File.OpenRead(path)) { var data = read_all(f); }
using var f = File.OpenRead(path);
Socket s = connect(host); // pass checks: s disposed / moved / returned on every path
```

Rules (P1): a local holding an `IDisposable`/`IAsyncDisposable` created in scope and not `using`/`own` -> warning; `own` leaked on any path -> error. `await using` for async disposables.

### 2. Move semantics for concurrency

```crystal
buf = List(Int32).new
ch.send(move buf)       # buf is dead after this line
puts buf.Count          # error: use after move

spawn(move data) { process(data) }
```

C#: unchanged call (`ch.Send(buf)`); the pass only marks `buf` as moved. Zero runtime cost.

### 3. Send-like check

A value crossing `spawn`, `Channel(T)`, or an actor mailbox must be one of:

| Kind | Allowed |
|---|---|
| Primitives, `readonly struct`, `string`, enums | yes (copy/immutable) |
| `frozen` / `System.Collections.Immutable` / `Frozen*` | yes |
| Thread-safe types (`Concurrent*`, `Channel`, attribute `@[ThreadSafe]`) | yes |
| Mutable reference passed with `move` | yes |
| Mutable reference captured/sent without `move` | error in strict mode, warning otherwise |

### 4. Immutability: `val` / `var` / `frozen`

```crystal
val n = 10              # single assignment
var i = 0               # mutable
x = 1                   # today's form; stays = var (compatibility)
cfg = frozen {"a" => 1} # FrozenDictionary
xs = [1, 2].freeze      # ImmutableArray / ReadOnly view
```

```csharp
var n = 10;            // pass rejects reassignment
var i = 0;
var cfg = new Dictionary<string,int>{{"a",1}}.ToFrozenDictionary();
var xs = ImmutableArray.Create(1, 2);
```

Default stays mutable (bare `x = 1`) to keep current code valid; strict mode (P3) may flip the lint.

### 5. Aliasing of mutable state

`borrow` / `&` = read-only view for a call; lowers to `in` (structs) or `IReadOnly*`/`ReadOnlySpan<T>` (collections).

```crystal
def sum(xs : &Array(Int32)) : Int32 ... end
```

```csharp
static int sum(ReadOnlySpan<int> xs) { ... }
```

No exclusivity checker across heap references; aliasing is limited at API boundaries only.

### 6. Stack-only / zero-alloc

| Alice | C# |
|---|---|
| `@[Stack] struct Parser` | `ref struct Parser` |
| `@[ReadOnly] struct P` | `readonly struct P` |
| `Span(T)`, `ReadOnlySpan(T)` | `Span<T>`, `ReadOnlySpan<T>` |
| `scoped s : Span(Byte)` | `scoped Span<byte> s` |
| `buf = stackalloc Byte, 256` | `Span<byte> buf = stackalloc byte[256];` |
| `def f(in p : Point)` | `void f(in Point p)` |

Escape rules come from Roslyn: a `Span` from `stackalloc` cannot be returned or stored in a field; errors surface as Alice diagnostics mapped to source lines.

### 7. Pooling for hot paths

```crystal
using buf = Pool.rent(Byte, 4096)   # struct wrapper over ArrayPool<byte>.Shared
  read_into(buf.span)
end
```

Lowers to `ArrayPool<T>.Shared.Rent` / `Return` in `finally`. `MemoryPool<T>` for `IMemoryOwner<T>`. Wrapper lives in `Alice.Runtime`. Rented buffer is `own`: move or return-to-pool enforced by the P1 pass.

### 7a. Inferred ownership: what a pooled buffer must prove (*implemented*, ADR 0016)

A local array, list or hash goes back to the `Own`/`OwnList`/`OwnDict` pool only when every mention of it is a proven
local use. When in doubt it stays a GC value: a missed release costs an allocation, never safety.

- Every mention counts, wherever it is: inside `spawn { }`, `using ... end`, `await`, `lock`, `when` guards, blocks and
  lambdas, and if/case/begin used as a value. A mention inside a `spawn` or block is a capture: the value stays GC.
- An assignment or move inside an if/case/begin used as a value keeps both locals GC (the move flow does not see it).
- Arguments bind to parameters only for direct Alice calls; `method_missing` and dynamic calls (`send`) take the value.
- A virtual call borrows or returns an owned array only when every override in the program does.
- `xs << v` returns `xs`: it keeps a list owned only as a whole statement whose value is dropped.

The same rules guard `par for`: a written array handed to a callee, or read whole (`xs.sum`), may be read at any index,
so the loop stays serial (AL0209 for `par for`) unless the callee provably reads it only at the loop counter
(`f(a, i)` reading `a[i]`, in every override), or the array is a distinct local the runtime alias check covers and
the callee reaches arrays only through its array parameters (no fields, no globals). A local passed by `ref`/`out`
loses its value range, so arithmetic on it stays overflow-checked.

### 8. `unsafe` fence

```crystal
unsafe
  p = buf.pointer       # byte*
  p[0] = 1
end
```

```csharp
unsafe { fixed (byte* p = buf) { p[0] = 1; } }
```

Pointers, `fixed`, raw `stackalloc` to pointer, and `NativeMemory.Alloc/Free` only inside `unsafe`. `lib` P/Invoke stays the native boundary (today `lib` emits an `unsafe partial class`; proposed: only `unsafe` inside the class when pointer types appear). Build flag `--deny-unsafe` rejects any `unsafe` outside allow-listed files.

### 8a. `@[Native]` C (*implemented*)

`@[Native]` functions compile to C but keep the table above: every array access is bounds-checked against the length
passed with the pointer, integer `+ - *` use `__builtin_*_overflow`, `/ %` check for zero and MIN / -1, shift counts are
masked like C#, and counters of inclusive ranges (or moved by the body) step checked. A violation prints `alice @[Native]: <what>` and aborts the
process (C has no exception to raise); it never reads or writes out of bounds.

### 9. Checked arithmetic

| Alice | C# |
|---|---|
| default | unchecked (C# default) |
| `checked { a + b }` | `checked(a + b)` |
| `alice build --checked` | `<CheckForOverflowUnderflow>true` |
| `&+`, `&*` (Crystal-style wrapping) | `unchecked(a + b)` |

### 10. Null safety

*Implemented*: NRT on, warnings are errors. Proposed: `try &.x` -> `?.`, `x || default` -> `??`, `not_nil!` -> `!` (allowed, but lintable in strict mode).

## Phased rollout

| Phase | Content | New pass? |
|---|---|---|
| P0 reuse-only | `using`, `Span`, `@[Stack]`, `@[ReadOnly]`, `in`, `scoped`, `stackalloc`, `unsafe` block, `checked`, `frozen`, `Pool` | no (syntax + lowering) |
| P1 resources | undisposed `IDisposable` lint, `own` leak error, pooled buffer return | yes |
| P2 concurrency | `move`, use-after-move, Send-like check on `spawn`/`Channel`/actors, `val` | yes |
| P3 strict mode | `--strict`: warnings -> errors, `--deny-unsafe`, `--checked`, `val` preferred | flags only |

Each phase: syntax in stage0 + selfhost, tests per rule (`alice emit` golden + diagnostic tests).

## Performance notes

| Knob | Use |
|---|---|
| Server GC (`<ServerGarbageCollector>`) | throughput, many cores |
| Concurrent/background GC (default on) | lower pause times |
| `GCHeapAffinitizeMask` / `GCHeapCount` | pin heaps when co-hosting processes |
| `DOTNET_GCgen0size`, `GCConserveMemory` | tune for memory vs throughput |
| NativeAOT (`--native`, *implemented*) | startup, footprint; same GC |
| `TieredPGO` (default on) | hot-path codegen |

Allocation avoidance: structs over classes for small data, `Span`/`stackalloc` for scratch, `ArrayPool` for buffers, avoid closures/LINQ/boxing in hot loops, `StringBuilder`/`string.Create` for strings, `ValueTask` for hot async. Measure with `BenchmarkDotNet` + `[MemoryDiagnoser]` before specializing.

## Versus Rust

| Area | Rust | Alice on .NET |
|---|---|---|
| Heap safety | ownership + borrowck | GC (free) |
| Freeing | `Drop` at scope end | GC; `using`/`own` only for resources |
| Stack refs | lifetimes | Roslyn ref-safety (`ref struct`, `scoped`) |
| Data races | `Send`/`Sync` | `move` + Send-like pass (P2) |
| Mutable aliasing | `&mut` exclusivity everywhere | API-level `borrow`/read-only views only |
| Lifetime annotations | yes | **skipped** |
| Pauses | none | GC pauses (tunable) |
| Unsafe | `unsafe` | `unsafe` + `lib` |
| Learning cost | high | low; checks are opt-in to strict |

Deliberately skipped: global borrow checker, lifetime syntax, affine types for all values, custom allocators.
