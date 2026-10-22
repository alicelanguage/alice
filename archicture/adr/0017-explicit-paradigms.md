# ADR 0017: Explicit, checked paradigms over inference

Status: Accepted (0.12.2)

Alice infers purity, forks, parallel loops, ownership and borrows. Inference is silent: code that misses a
rule is compiled correctly but slower, and the programmer cannot tell why. Programmers who want the fast path
need a way to say so and to be told when they do not get it.

Decision: every inferred property gets an optional keyword. The keyword never changes what is proven safe;
it makes a failed proof a compile error, and, where a heuristic gates an optimization, it replaces the
heuristic.

| Keyword | Inferred property | Error |
|---|---|---|
| `pure def` | interprocedural purity (ADR 0015) | AL0210 |
| `par a, b = f(x), g(y)` | fork of independent pure calls; drops the recursion heuristic | AL0211 |
| `inline def` | `@[Inline]` | - |
| `safe def` | new: no ambient authority, transitively over Alice calls | AL0212 |
| `own p` (parameter) | ownership (ADR 0016) across a call: the argument moves in | AL0207 / AL0208 |
| `borrow p` (parameter) | the inferred borrow of ADR 0016 | AL0213 |
| `scope ... end` | lifetime region: arrays created inside are owned and released at `end` | AL0207 |

Interaction nets (HVM, Bend) motivate the forms. A term's independent redexes reduce in parallel, which is
why expressions fork (`fib(n - 1) + fib(n - 2)`) and why there is an explicit parallel let. Values are used
once, which is why there are `own` moves and why `xs = xs.map { }` on an owned array overwrites it in place
(the single-owner update). Alice keeps the .NET GC as the fallback: whatever is not proven owned is ordinary
garbage.

Lifetimes are regions (`scope`), not Rust-style named lifetime parameters. A borrow cannot outlive a call,
since a `borrow` parameter may not be stored or returned, so no lifetime ever has to be named in a signature.

`safe` is a capability check, not a sandbox. It rejects reaching the machine by name: .NET statics other than
`Math` and `Alice.Runtime`, constructing I/O, network, process, reflection, threading or interop types,
`dynamic`, `@[Native]` and `unchecked`. It allows instance members of values the caller handed in. It does not
bound time or memory.

Limits: an `own` argument must come from inside a method (top-level script code has no ownership analysis).
The in-place map needs a one-expression block that does not mention the array. `safe` does not yet inspect
.NET types that reach authority through instance members of arguments.
