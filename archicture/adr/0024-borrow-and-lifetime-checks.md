# ADR 0024: Borrow, lifetime and send checks

Status: Accepted (extends ADR 0008, 0016, 0017)

Audit of what Alice already enforced before this ADR, against Rust's safety guarantees:

| Rust guarantee | Alice before 0.13.2 |
|---|---|
| move semantics, use after move | `own` locals and parameters: AL0207 (escape), AL0208 (use after move), ADR 0016/0017 |
| borrows cannot outlive the call | `borrow` parameters: AL0213; `scope ... end` regions |
| exhaustive `match` | `case` over enums and sum types: AL0201 |
| data-race freedom in concurrent code | writes from concurrent blocks: AL0202 (ADR 0008) |
| `unsafe` for raw pointers | `unsafe ... end` / `unsafe def`: AL0216; capturing native callbacks: AL0215; `safe def`: AL0212 |
| null safety | AL0310 |

Missing, added here as one post-binding pass (`Semantics/Binder/Binder.Borrow.cs`) with no lowering change:

- **AL0217 conflicting borrow** (`&mut` with `&`): the same array, .NET collection or Alice class instance passed
  as two arguments of one Alice call when the callee writes through either parameter (element store, mutating call,
  or lending it on to a parameter that writes, interprocedurally, assuming read-only through recursion). Read-only
  aliases stay legal (`total(a, a)`).
- **AL0218 stack value escapes** (lifetimes): a `@[Stack]` value (a C# `ref struct`) lives in its frame. Capturing it
  in a block or lambda, declaring a field of that type in a heap type, or building a collection of it is an Alice
  error instead of a Roslyn error at emit time. Passing it to a parameter and returning it by value stay legal.
  Stack arrays (`Lowerer.StackArrays`) need no check: they are inferred only when they never escape.
- **AL0219 use after send** (`Send` moves): a mutable value passed to `Channel#send`/`send_async` or `Actor#send`
  belongs to the receiver. A later mention of the name in the same block is an error until the name is rebound with
  `=`. Numbers, strings, records and thread-safe types are unaffected.
- **AL0202, write while a task reads** (`Sync`): after `t = spawn { ... }` reads a mutable local, writing that local
  (assignment, element store, mutating call) before a statement that mentions `t` (such as `t.wait`) or awaits is a
  data race. `Par.For` and the other blocking parallel calls finish before returning and are not affected.

Native interop already requires the explicit boundary for pointers (AL0216); plain `lib`/`@[Native]` calls with
blittable arguments stay outside `unsafe`, as ADR 0018 marshals them safely. No new syntax.

Consequences: the checks are flow-insensitive within a statement list (conservative: a branch that rebinds does
not end tracking), and run once on the generic form of each method. All samples and benchmarks compile unchanged.
Tests: `BorrowSafetyTests`.
