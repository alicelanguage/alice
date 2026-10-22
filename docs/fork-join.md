# Fork/join parallelism

Alice runs independent pure recursive calls in parallel without annotations, and its standard library sorts large arrays with the same mechanism. Design: [ADR 0015](../archicture/adr/0015-fork-join-of-pure-calls.md). Code: `src/Alice.Compiler/Lowering/Lowerer.Fork.cs`, `src/Alice.Runtime/Parallel/Par.cs`, `src/Alice.Runtime/Parallel/ParSort.cs`.

## Credit

The idea comes from **Victor Taelin** (Higher Order Company, Rio de Janeiro), creator of HVM and the Bend language, and his co-authors of [BendRT: A Parallel Runtime for CPUs and GPUs](https://github.com/bendlang/bend/blob/main/paper/BendRT.pdf). BendRT shows that the parallelism of a functional program can be carried by a single construct, the *parallel let* `l r = f(x) g(y)`, if the runtime makes forking almost free where it does not pay off: a fork met deep in the recursion runs as a plain call (lazy task creation), so only the top of the call tree becomes tasks. The [HVM2](https://github.com/HigherOrderCO/HVM2) line of work behind it argues the same point from interaction nets: parallelism should come from the shape of the program, not from threads the programmer manages.

Alice takes that idea to .NET. Bend asks the programmer to write the fork; Alice infers it from code that is already parallel in shape, and keeps the .NET garbage collector and thread pool instead of Bend's ownership-based heap and task cube.

## What forks

Two consecutive declarations whose values are calls

```ruby
def radix(values : Array(Int64), shift : Int32) : Array(Int64)
  # ... split values into left and right ...
  sorted_left = radix(left, shift - 1)     # forked
  sorted_right = radix(right, shift - 1)   # runs on this thread meanwhile
  # ... merge ...
end
```

or the single-statement form, Bend's parallel call (`a b = pow2(p) pow2(p)`) written the Alice way:

```ruby
def pow2(n : Int32) : Int64
  return 1_i64 if n == 0
  a, b = pow2(n - 1), pow2(n - 1)   # one statement, two calls: forked
  a + b
end
```

fork when all of these hold:

| Rule | Why |
|---|---|
| both calls go to user methods proven **pure** | the same interprocedural purity gate as loop parallelization: impure code never runs in parallel |
| the second call does not read the first result (`b = f(a)` stays serial) | the calls must be independent |
| at least one call is **recursive** (calls the method being compiled) | recursion means the work divides; two small calls (two getters) are not worth a task |
| no block or named argument, arguments are pure reads | arguments are evaluated inside the task |
| not inside `async`, generic methods, lambdas or already-parallel helpers | a task boundary must not cross those |

Calls into the runtime are pure when the runtime says so: a static `Alice.Runtime` method marked `[Pure]` (such as `Digest::SHA256.hexdigest`) may be called from forked or parallel code. Arbitrary .NET methods are never trusted. Static receivers (`Module.f(x)`), string interpolation, array literals and `min`/`max`/`sum` without a block are pure reads. A method stays pure when it writes only into arrays it allocated itself: a local whose every assignment is a new array (`zeros = Array(Int64).new(n)`) cannot alias caller data, so `zeros[i] = v` is not an effect. Writing a parameter's array, or an alias of it, is.

## What it lowers to

```csharp
if (global::Alice.Runtime.Par.Forkable)
{
    (sorted_left, sorted_right) = __fork_radix_1(left, right, shift);
}
else
{
    sorted_left = radix(left, shift - 1);
    sorted_right = radix(right, shift - 1);
}

private static (long[], long[]) __fork_radix_1(long[] left, long[] right, int shift)
{
    return global::Alice.Runtime.Par.Fork(() => radix(left, shift - 1), () => radix(right, shift - 1));
}
```

The lambdas live in a separate helper on purpose: lambdas that capture a method's parameters make Roslyn allocate their closure at method entry, on every call, forking or not. In the helper only the calls that really fork pay for a closure; the recursive method itself allocates nothing on its sequential path.

## Runtime operations

| Operation | Meaning |
|---|---|
| `Par.Forkable` | true while the current fork depth is below `log2(cores) + 3` and there is more than one core. A thread-static read: deeper calls pay nothing else |
| `Par.Fork(left, right)` | offers `left` to the thread pool as one work item (`ForkJob`, no `Task`) and runs `right` on the calling thread, then joins. A left half no pool thread has claimed yet runs inline on the joining thread (nested forks never block pool threads); one already running is awaited with a short spin, then a monitor wait. Exceptions surface as themselves, and when `right` throws, `left` finishes before the exception unwinds |
| `xs.sort` (arrays of primitives, length >= 65536) | fork/join merge sort: halves sort in parallel near the root, leaves (16384 elements) sort in place on spans, merges use one scratch buffer from `ArrayPool`. Same result as `Array.Sort`; other element types use `Array.Sort` |

| `xs.sum`, `xs.min`, `xs.max` (arrays of primitives, length >= 2^20) | fork/join over index halves near the root, one SIMD pass per leaf (2^18 elements), partials combined in index order. Integer results and min/max are exact; a float sum reassociates like any SIMD sum |

The runtime uses fork/join inside ordinary library calls: the caller writes `xs.sort` or `xs.sum`, and large inputs split across cores without any change to the program. The depth cutoff is lazy task creation as in BendRT: with 16 cores the top 7 levels fork (at most 128 tasks), so the pool has enough work to balance and nothing more.

## Results

Same machine, median of runs, outputs checked against the reference implementation:

| Workload | Alice 0.11.2 | Alice 0.12.0 | idiomatic C# |
|---|---:|---:|---:|
| tree radix sort, 1M elements (polyglot suite) | 3226 ms | 319 ms | 665 ms |
| `sort` of 10M `Int32` | 756 ms (clone + `Array.Sort`) | 207 ms | 756 ms |
| `sum` + `max` of 32M `Float64` | 68.7 ms (serial SIMD) | 16.4 ms | 134.7 ms (LINQ) |

## Differences from BendRT

- Bend's fork is written by the programmer and trusted absolutely; Alice's is inferred and must be proven safe.
- BendRT schedules a fixed grid of rings without work stealing and leaves balance to the program. Alice uses the .NET thread pool, which steals work, so unequal splits still use the cores.
- BendRT has no garbage collector (an affine type system places every free). Alice keeps the .NET GC; see [heap and GC](heap-gc.md) for how generated code stays off the heap.
- Bend runs the same code on GPUs. Alice targets the CPU through .NET.

## Parallel loops

Counted loops use the same proofs: an iteration may write only locals declared in its body, `a[i]`, or its row `a[i * w + j]` (with `j` counting `0...w` in a nested loop), plus one `+=` reduction. A loop that reads the array it writes at another index (`row[j - 1]`) is a scan and stays serial; a shifted read of another array (a stencil) stays parallel behind a `ReferenceEquals` alias check. Loops run on `ParJob`: one work item per loop, an atomic chunk counter, and the calling thread working through chunks too. `par for` drops the trip-count threshold and makes a failed proof compile error AL0209.

## Limits

- Only the two shapes above fork (two consecutive declarations, or `a, b = f(x), g(y)`). An expression with two recursive calls (`1 + l.check + r.check`, `fib(n - 1) + fib(n - 2)`) does not yet: name the two results.
- The parallel sort covers primitive element types; `sort_by` and comparer sorts stay serial.
