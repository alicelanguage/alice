# ADR 0015: Fork/join of independent pure calls

Status: Accepted

Alice parallelized loops only; divide-and-conquer recursion (tree sorts, tree
folds, Fibonacci-shaped splits) ran on one core. BendRT parallelizes exactly this
shape through its parallel let `l r = f(x) g(y)` and lazy task creation. Alice
infers the same fork instead of asking for it:

```
a = f(x)   # consecutive declarations, both calls to pure user methods,
b = f(y)   # b's call does not read a, and at least one call is recursive
```

lowers to `if (Par.Forkable) (a, b) = __fork_f_N(x, y); else { a = f(x); b = f(y); }`.
The helper holds the two lambdas, so the recursive method itself never allocates
a closure on its sequential path.

- Purity is the interprocedural gate of loop parallelization (never fork code
  not proven pure). It now also accepts writes into fresh arrays: locals whose
  every assignment in the body is a new array, so they cannot alias a caller's
  data. Allocating an array is not an effect.
- Lazy task creation: `Par.Forkable` is a thread-static depth check; only the top
  `log2(cores) + 3` levels spawn tasks, deeper calls run as plain calls. The join
  uses `GetAwaiter().GetResult()`: a task the pool has not started runs inline,
  and exceptions surface unwrapped.
- Requiring recursion keeps two small pure calls (two getters) from forking.

Unlike BendRT, the .NET pool still steals work, so unequal splits are tolerated;
Alice keeps the GC and adds no ownership rules.
