# ADR 0016: Ownership of local arrays and pooled buffers

Status: Accepted (amends ADR 0006)

ADR 0006 kept memory reclamation entirely in the GC. Hot loops that build a fresh
array per iteration (dynamic-programming rows, scratch buffers, per-frame
images) then cost one GC allocation per iteration, and large ones land on the
large-object heap. Alice now takes those arrays out of the GC's steady state
without giving up memory safety.

Decision:

- A local array of unmanaged elements is **owned** when it is provably the only
  reference to its array: every value it receives is a fresh `Array(T).new(n)` /
  `Array(T).new(n, v)` or another owned local (a **move**), and every use is an
  element access, `.size`, `for v in a`, a borrow by an Alice parameter that never
  retains it (interprocedural: the callee only indexes, sizes, iterates or lends
  it on), a move, or the method's result. Captured by a block, stored, passed to
  .NET or reached from `begin/rescue`: not owned.
- A move-state flow analysis (definitely live / possibly moved, joined over
  branches and loops) rejects reads of a possibly moved local and decides where
  the old array is definitely dropped: before a reassignment and at every exit
  (`return`, tail value, end of body), except the array being returned.
- Owned buffers come from `Alice.Runtime.Own(T)`: four per-thread slots per
  element type with exact lengths (`.size` is unchanged), zeroed or filled on
  reuse, arrays above 64 MiB left to the GC. The editdist row loop allocates two
  rows in total instead of one per row.
- Inference is silent: a local breaking a rule stays an ordinary GC array.
  `own a = ...` makes the rules a contract checked at compile time: an escape is
  AL0207 (`owned value ... escapes`), a read after a move is AL0208.
- No release on exception paths: the array becomes ordinary garbage. A missed
  release costs one allocation, never safety; releases are only emitted where the
  analysis proves no other reference exists.

Consequences: no runtime checks, no reference counting, no `try/finally` in hot
methods. Buffers released on one thread are reused by that thread only. Owned
locals are limited to unmanaged element arrays declared in the method; fields,
collections of references and arrays shared across threads stay with the GC.
Ports to the self-hosted compiler follow ADR 0006's rule.

Amendment (0.12.1): a pooled array hides its length from the JIT. `new T[n]`
tells RyuJIT that the length is `n`, so `for (k = 0; k < n; k++) buf[k]` has no
bounds checks; `Own<T>.Rent(n)` is an opaque call and the checks stay. The MIR
pass `owned-bounds` (ADR 0014) rewrites such a loop's bound to `buf.Length` when
`n` is a local or parameter written only by its declaration and `buf` is written
only by its own: the two values are equal everywhere, and the JIT then drops the
checks as it does for `new`. Measured in `benchmarks/gc` (`scratch`).
