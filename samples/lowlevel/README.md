# Low-level Alice

Four ways to get close to the machine, and what Alice still checks in each one: calling C, a native kernel
compiled with clang, raw pointers, and a tree built from array indices instead of objects. Linux (it binds
`libc.so.6`; on macOS use `/usr/lib/libSystem.B.dylib`).

| File | Shows |
|---|---|
| [src/libc.al](src/libc.al) | `lib LibC`: `getpid`, `strlen`, `getenv` twice (as `String?` and as a raw pointer), `malloc`/`free`, `qsort` with an Alice comparator |
| [src/pointers.al](src/pointers.al) | `unsafe def`: walk a C string byte by byte; fill, sum and free a `malloc`'d block |
| [src/kernel.al](src/kernel.al) | `@[Native]` dot product, and the same loop in plain Alice |
| [src/tree.al](src/tree.al) | `IndexTree`: a binary search tree in three `Int32` arrays, no recursion |
| [src/main.al](src/main.al) | The demo; `--measure` adds an allocation comparison |
| [test/lowlevel_test.al](test/lowlevel_test.al) | C calls, pointer walks, kernel vs managed loop, the tree (including a 20,000-deep chain) |

## Run

```bash
cd samples/lowlevel
alice run
```

```text
pid > 0: true
strlen("wonderland") = 10
qsort: 3 7 19 25 42
env_length(HOME) == strlen: true
env_length(unset) = -1
malloc'd squares 0..999: 332833500
dot = 600000.0 (managed 600000.0), native library loaded: true
index tree: 100000 nodes, height 39, in order: true, has 923271: true, has -1: false
```

`native library loaded` is `false` on a machine without clang: then the managed body runs, with the same result.

```bash
alice run -- --measure
```

adds, on .NET 10 x64:

```text
allocated for 100000 nodes: index tree 1200120 bytes, object tree 4000000 bytes (root true)
```

The index tree is three arrays (3 allocations, 1.2 MB); the object tree is 100,000 `Node` objects of 40 bytes,
each one a separate object for the GC to trace.

## The safety story

| Construct | What Alice checks | What is on you |
|---|---|---|
| `lib` / `fun` (C calls) | Strings cross as NUL-terminated UTF-8; a string with an embedded NUL raises `ArgumentException` instead of being cut short; arrays are pinned for the call; a `String?` return maps NULL to `nil`; a comparator lambda that captures a local is error AL0215 | That the declaration matches the C function: a wrong signature is undefined behaviour |
| `Pointer(T)` | Every use is inside `unsafe ... end` or an `unsafe def`; anywhere else it is error AL0216. Searching for `unsafe` finds every line where memory safety is up to you | Bounds (`p[i]` is not checked), lifetime (`free` exactly once), and NULL |
| `@[Native]` | The C version keeps Alice's rules: every array access is bounds-checked and integer overflow traps (the process aborts with a message, since C has no exceptions); aliasing arrays fall back to the managed version; only a numeric subset compiles to C, anything else is a compile error | Nothing extra; a trap ends the process instead of raising |
| `IndexTree` | Ordinary Alice: every index is bounds-checked, nothing is freed by hand | A stale index finds a wrong node, never freed memory |

Things found while writing this sample (see the workarounds in the code):

- A `Pointer(T)` has no NULL test yet, and `p.nil?` is `false` even for NULL. `env_length` asks the `String?`
  binding of `getenv` first, which maps NULL to `nil`, then walks the raw pointer.
  `Environment.SetEnvironmentVariable` is not a substitute: it changes only .NET's copy of the environment, which
  C's `getenv` does not see.
- A `malloc` result cannot be checked for NULL for the same reason.
- In an `unsafe def`, a `for` loop that sums through a pointer is parallelized by the compiler into a helper that
  is not marked unsafe, and the build fails with AL0216 pointing into generated code. `sum_of_squares` uses a
  `while` loop.
- `fun getenv_raw = getenv(...)` declares a second Alice name for the same C function.

## Test

```bash
alice test     # 6 examples, 0 failures
```
