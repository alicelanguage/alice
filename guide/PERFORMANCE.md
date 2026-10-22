# Performance in Alice

How Alice code becomes fast .NET code, and how to write code that the compiler can optimize. Check any
method with `alice explain file.al`.

## The model

Alice compiles to C#, then Roslyn emits IL that the .NET JIT (or NativeAOT) compiles. Alice cannot beat the JIT
at instruction selection. It wins by emitting code that idiomatic C# does not get without extra work:

| Optimization | Trigger | Effect |
|---|---|---|
| owned arrays | a number array that never escapes | pooled buffer, no GC allocation once warm |
| owned results | a function returning only its own owned array | the caller recycles it |
| in-place map | `xs = xs.map { }` on an owned array | no new array |
| shared objects | `T.new(constants)` of an immutable class, identity never observed | one static instance |
| fork/join | two independent pure calls, one recursive | parallel near the root, plain calls below |
| parallel loops | iterations write only their own index | all cores, chunked |
| fusion and SIMD | `map.select.sum` chains, `@[SoA]` records | one loop, `Vector<T>` |
| stack arrays | small fixed arrays used locally | `stackalloc` |

## When to use what

Plain inferred code is the default and is usually enough. Reach for an explicit tool when a measurement or
`alice explain` shows the need, not up front.

| Tool | Worth it when | Not worth it when |
|---|---|---|
| ownership (inferred) | always on; costs nothing | - |
| `own x = ...` | a hot loop must stay allocation-free and you want a refactor that breaks it to fail the build | one-off code, arrays under ~1 KB allocated rarely (the GC handles those cheaply) |
| `own` parameter | a pipeline passes a big buffer through stages that each rewrite it (`xs = xs.map`) | the caller still needs the array afterwards (use `borrow`) |
| `borrow` parameter | a helper reads a caller's array in a hot path and must never keep it | the helper is cold, or it legitimately stores the array |
| `scope ... end` | a large temporary should be freed before the method continues (lower peak memory, earlier reuse) | small temporaries; the method ends soon anyway |
| shared immutable objects | many identical constant objects (tree leaves, sentinels) | objects compared by identity, or mutable ones (never shared) |
| fork/join, `par a, b` | each call does at least ~10 µs of work (recursion over thousands of elements, big subproblems) | tiny calls: below the depth cutoff they already run inline; `par` on tiny work only adds task cost |
| parallel loops, `par for` | 64K+ cheap iterations, or a few hundred expensive ones | short loops, loops dominated by memory bandwidth on few cores |
| `pure def` | a function must stay parallelizable; you want a clear error when someone adds a `puts` | every small helper (purity is inferred anyway) |
| `safe def` | code handling untrusted input, plugins, scripts from users | trusted internal code |
| `inline def` | a tiny function called in a hot loop that the JIT does not inline | anything large: inlining big bodies grows code and can slow it down |
| `@[Native]` | a numeric kernel where clang `-O3 -march=native` beats the JIT (measure) | code that calls .NET or allocates; small functions (call overhead dominates) |
| `@[Fast]` on `fun` | short C functions called millions of times | anything that blocks, takes locks or calls back into .NET |
| `rule` / `alice prove` | invariants that must never break: money, game rules, parsers, numeric edge cases | properties over huge or unbounded inputs (exhaustion cannot cover them; use tests) |

Rules of thumb:

- Allocation matters when it happens per iteration of a hot loop or per request. A few allocations per
  program run never matter.
- Parallelism needs enough work per task. Measure with and without (`alice explain` shows what forked).
- An explicit keyword is a contract for the future: it is most valuable on code other people (or agents) will
  edit.

## Arrays and ownership

```alice
def smooth(n : Int32, steps : Int32) : Float64
  cur = Array(Float64).new(n, 1.0)
  for s in 0...steps
    nxt = Array(Float64).new(n)          # pooled after the first step
    for i in 1...(n - 1)
      nxt[i] = (cur[i - 1] + cur[i] + cur[i + 1]) / 3.0
    end
    cur = nxt                            # move: the old buffer goes back to the pool
  end
  cur[n / 2]
end
```

An array is owned while every use is an index, `.size`, `for v in a`, a lend to a parameter that does not keep
it, a move to another owned local, or the result. Storing it in a field, capturing it in a block, or passing it
to .NET ends ownership: it is then an ordinary GC array (correct, just not pooled). Write `own a = ...` to get
an error that says what broke ownership.

Measured against idiomatic C#: rolling rows, scratch buffers and 4 MiB frames run with 0 garbage collections in
Alice, against 74 to 1681 in C#. Speed is on par under workstation GC and up to 2.4x faster under server GC.

## Parallelism

```alice
def count(n : Int32) : Int64
  return 1_i64 if n < 2
  count(n - 1) + count(n - 2)          # forked: two independent redexes
end

def scale(xs : Array(Float64), k : Float64)
  for i in 0...xs.size                 # parallel when large: each iteration writes xs[i] only
    xs[i] = xs[i] * k
  end
end
```

- Only code proven pure is forked or parallelized. A call to `puts`, a write to a field or to an array the
  method did not create makes it serial.
- Forks only happen near the root (log2(cores) + 3 levels); deeper calls pay one thread-local read.
- `par for` and `par a, b = f(x), g(y)` make parallelism a requirement: AL0209 / AL0211 explain a failure.

## Objects

Class instances live on the .NET GC heap, as in C#. Two things help:

- Immutable classes (only `getter`s, a constructor that stores its arguments, no field writes) built from
  constants are shared: `Leaf.new(nil, nil)` in a tree builder allocates once. A binary-tree benchmark: half
  the allocation of C# and ~1.35x faster.
- `@[SoA]` record structs store many small records column by column, ready for SIMD.

## Start-up, JIT and runtime settings

- `alice run` caches the compiled image; an unchanged program starts in ~0.1 s.
- Loop nests and recursive numeric kernels are compiled fully optimized from their first call; other methods
  use tiered compilation with dynamic PGO.
- `alice build --native` produces a NativeAOT executable. Choose it for programs that run for less than a few
  seconds: they end before the JIT has optimized their hot code. A short game-update benchmark ran in 1.8 ms
  per trial native against 11.2 ms under the JIT, with 15 MB peak memory instead of 40 MB. Long-running
  programs reach the same speed either way.
- `[runtime] globalization = "invariant"` in `alice.toml` skips loading ICU: 2-4 MB less memory per process.
  Strings then sort and compare ordinally (`"A" < "B" < "a"`, as in Ruby) instead of linguistically
  (`"a" < "A" < "b"`). Number printing does not change; it is always invariant.
- `[runtime] gc = "server"` gives each core its own heap: best for long-running, allocation-heavy parallel
  work. The default workstation GC suits short programs. Tuning gen0 size or `GCConserveMemory` measured no
  gain.

Measured and not worth changing: turning off tiered compilation or PGO helps short programs and slows long ones
(use `--native` instead); precompiling the Alice runtime with ReadyToRun made no measurable start-up
difference.

## A checklist

1. `alice explain` the hot method. "none" on a hot loop means something blocked the optimizations.
2. Replace `Object`/`Any` containers with typed ones.
3. Keep buffers local; lend them to `borrow` parameters instead of storing them.
4. Split side effects (printing, logging) out of hot functions so the functions stay pure.
5. Add the explicit keyword (`own`, `pure`, `par`) and read the error.
