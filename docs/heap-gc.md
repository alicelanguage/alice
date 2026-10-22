# Heap, GC and allocations

Alice runs on the .NET garbage collector. It does not replace it: the compiler keeps values off the GC heap where it can prove that is safe, and leaves the rest to the collector. This page collects what Alice does about memory, what still allocates, and how to check. Related: [performance model](performance.md), [fork/join](fork-join.md), ADRs [0013](../archicture/adr/0013-numeric-semantics.md), [0014](../archicture/adr/0014-mir-pass-pipeline.md) and [0015](../archicture/adr/0015-fork-join-of-pure-calls.md).

## The rule

Hot code allocates nothing it does not need. "Need" means the program asked for a value that outlives the expression: a string to print, an array to return, a collection to keep. Everything else, such as iterator chains, blocks, small temporary arrays, boxing, closures and destructuring, is lowered away. `tests/Alice.Tests/Compiler/AllocationTests.cs` is the contract: each kernel there must allocate **0 bytes per call**, measured with `GC.GetAllocatedBytesForCurrentThread`, and a change that breaks one is a regression.

## Choosing the collector

Generated programs default to **workstation, concurrent GC**: one heap, fast startup and low RSS, which is right for CLIs, tools, games and short jobs. `alice.toml` changes it:

```toml
[runtime]
gc = "workstation"   # default
# gc = "server"      # one heap per core (DATAS keeps it adaptive): long-running, allocation-heavy parallel work
# gc = "low-memory"  # non-concurrent GC with System.GC.ConserveMemory = 7: smallest footprint
```

Measured on the polyglot suite: server GC started slower and used up to 70% more RSS on short runs (merkle: 126 MB → 216 MB), and was ~13% faster only on one long allocation-heavy workload. The usual .NET environment variables (`DOTNET_gcServer`, `DOTNET_GCgen0size`, ...) still override the configuration for experiments.

## What the compiler keeps off the heap

| Source | Without Alice's lowering | What Alice emits |
|---|---|---|
| `xs.map { }.select { }.sum`, `each`, `times`, `upto`, `step(lim, k)` with literal `k`, `sum/count/any?/find/reduce` with literal blocks | delegate + closure + iterator per call | one inlined `for` loop |
| capture-free lambda | closure object | `static` lambda (cached delegate) |
| `[a, b, c].min` / `.max` | a 3-element array per evaluation | `Math.Min(Math.Min(a, b), c)` (MIR pass `literal-minmax`) |
| `q, r = x.divmod(y)` | a `[q, r]` array | two floored helpers on temporaries |
| local array of unmanaged elements that never escapes, reallocated per iteration (`row = Array(Int32).new(n)` ... `previous = row`) | one GC array per iteration | ownership (ADR 0016): the buffer goes back to `Own(T)`, a per-thread exact-length pool, before reassignment and at exit; `own` makes it a checked contract (AL0207, AL0208) |
| small local array that never escapes: `rgb = Array(Float64).new(3, 0.0)`, `p = [x, y]` | an array per evaluation, also inside loops | one `stackalloc` buffer at method entry, re-initialized where the source declares it |
| `Array(T).new(n, v)` | zeroing pass, then a fill loop | `GC.AllocateUninitializedArray` + one `Fill` |
| `puts n`, `"#{n}"`, `inspect` of numbers | boxing the number before formatting | formatted directly to its string |
| `round(n)`, `floor(n)`, `ceil(n)` | a temporary string | formatting into a stack buffer |
| `d[k] = d.GetValueOrDefault(k) + 1` | two hash lookups | one `CollectionsMarshal.GetValueRefOrAddDefault` |
| word counting over `Split` | a string per word | spans + alternate lookup on the dictionary |
| forked recursion | a closure on every recursive call | the closure lives in a helper only the forking calls near the root enter |
| `sort` of a large primitive array | - | merge buffer rented from `ArrayPool`, leaves sorted in place |
| array of fixed-width literal rows (`points[i] = [x, y]`) | n + 1 arrays | one flat array, `points[i * k + j]` |
| `Channel(T)` send/receive | a `Task` per blocking call | monitor wait on a fixed ring |

### Flat rows

An array of fixed-width literal rows

```ruby
points = Array(Array(Int32)).new(n)
for i in 0...n
  points[i] = [x, y]
end
```

is one `int[n * 2]` when the rows are filled by that full loop right after the allocation, every row has the same literal width, and every other use is `points[i][j]` or `points.size`. That is one allocation instead of n + 1 and contiguous memory instead of n pointers to chase. The column index stays checked against the width, so `points[i][2]` raises as it would on the jagged array instead of reading the next row. A row that escapes (`row = points[i]`, `for p in points`, `points.inspect`), a ragged row, or a partial fill keeps the jagged array. On the polyglot suite's k-means with 1M points: 0.44 s / 95 MB → 0.38 s / 63 MB.

### Channels

`Channel(T)` is a lock-guarded ring sized at creation (one slot for the unbuffered rendezvous), the design of BendRT's channel rows. Blocking `send` and `receive` wait on the lock's monitor: no `Task` per blocking call, no allocation per message, and no thread-pool thread stuck in sync-over-async. Async variants allocate one waiter only when they actually wait.

### Small arrays on the stack

A local qualifies when it is bound exactly once to an array of at most 256 unmanaged elements of literal size (or an array literal of at most 32 items), and every use is `l[i]`, `l.size`, `l.length` or `for v in l`. It must not be returned, passed to a call, aliased, used in a block or lambda, or used inside a loop that it was declared outside of. Those are exactly the uses for which a `Span<T>` over one stack buffer behaves like the heap array. The buffer is declared once at method entry, so a declaration inside a loop does not grow the stack per iteration. Async and iterator methods keep heap arrays, since a span cannot live in their state machines.

The .NET 10 JIT also stack-allocates some small non-escaping arrays by its own escape analysis, but not reliably, and not inside loops. Alice's rule makes it a compile-time guarantee instead of a JIT heuristic.

## Startup

Every program touches `Alice.Runtime.Prelude` on its first line, which runs all of Prelude's static initializers. Anything expensive there is paid by every program, used or not: a compiled regex used to cost every launch Reflection.Emit plus JIT time. Caches for dynamic `send`, `define_method`, reflection and regex literals live in nested holder classes that initialize on first use. `tests/Alice.Tests/Architecture/StartupTests.cs` fails if Prelude gains an eager static field of a non-trivial type.

Values typed `dynamic` load the C# runtime binder (tens of milliseconds). Alice types an `if`/`case` value as the common type of its branches, and a branch that ends in `raise` does not count, so ordinary code never becomes `dynamic`.

## What still allocates

- **Results the program keeps:** strings (formatting, interpolation, `to_s`), arrays returned by `map`/`select`/`sort`, collections, objects from `.new`, ADT variants with fields (fieldless variants are singletons).
- **`Object`-typed storage:** a number stored in `Dictionary(String, Object)`, `List(Object)` or an untyped container is boxed. Typed containers avoid it.
- **Non-inlined blocks:** a block passed to a method Alice does not inline (user methods taking blocks, most .NET APIs) is a delegate, plus a closure if it captures locals.
- **Parallel dispatch:** `Par.Range`, `Par.Reduce` and `Par.Fork` allocate a small fixed amount per dispatch (task, closure, partials array). That only happens above their size thresholds or near the root of a fork tree, where the work dwarfs it.
- **Exceptions**, `define_method`, `send` and reflection-based features.

## Checking a program

- `alice emit file.al` prints the generated C#: look for `new`, `=>` and `stackalloc`.
- `ALICE_DISABLE_PASSES=literal-minmax,induction alice run file.al` turns MIR passes off for an A/B comparison.
- In a test, wrap a call in `GC.GetAllocatedBytesForCurrentThread()` deltas after a warm-up, as `AllocationTests` does.
- `DOTNET_gcServer=1`, `DOTNET_GCgen0size=0x4000000` and similar runtime variables compare GC configurations without rebuilding.

## Writing allocation-free Alice

- Prefer typed arrays and typed collections over `Object` containers.
- Keep small temporaries local: declare them where they are used, and do not pass them around if they are only scratch space.
- Use literal blocks on `each/map/select/sum/...` and `times/upto/step`: those inline.
- Return values instead of building small arrays to return two results; `q, r = a.divmod(b)` is already free.
- For point/record-like rows, fill an `Array(Array(T))` with literal rows in one loop right after creating it, and index it as `a[i][j]`: it becomes one flat array.
- `xs = xs.map { }` on an owned array overwrites it in place (Bend-style single-owner update, 0.12.2).
- A function that returns only an owned local (`def source(n)` building and returning `xs`) hands the buffer to
  its caller: `src = source(n)` is owned, released by the caller and reused by the next `source` call (0.12.2).
- Reuse buffers across iterations when they are large: `stackalloc` covers only small, fixed-size arrays.
