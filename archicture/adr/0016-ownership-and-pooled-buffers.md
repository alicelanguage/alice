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

Amendment (0.12.2): owned results. A call to a method whose every returned value is a local it
owns (fed only by fresh arrays, never stored, captured or aliased) yields an array its caller may own:
`src = source(n)` joins the caller's ownership analysis like a fresh array. The callee never releases what it
returns, so the buffer moves to the caller, which releases it, and the next `source` call rents it back.
`benchmarks/gc` `scratch`: 3.81 MiB and 18 collections per 7 runs down to under 400 B and 0 collections.
In-place `xs = xs.map { }`, `own`/`borrow` parameters and `scope` regions are in ADR 0017.

Amendment (0.12.1): a pooled array hides its length from the JIT. `new T[n]`
tells RyuJIT that the length is `n`, so `for (k = 0; k < n; k++) buf[k]` has no
bounds checks; `Own<T>.Rent(n)` is an opaque call and the checks stay. The MIR
pass `owned-bounds` (ADR 0014) rewrites such a loop's bound to `buf.Length` when
`n` is a local or parameter written only by its declaration and `buf` is written
only by its own: the two values are equal everywhere, and the JIT then drops the
checks as it does for `new`. Measured in `benchmarks/gc` (`scratch`).

Amendment (0.12.3): owned lists. A local `List(T)` of unmanaged elements is owned under the same rules: fed only by
`List(T).new` / `List(T).new(capacity)` or other owned locals, and used only through operations that leave the list
where it is (`<<`/`Add`, indexing, `Count`/`size`, `Clear`, `Contains`/`IndexOf`, `Sort`, `ToArray`, and Prelude
iterators such as `each`, `sum`, `map`, `select` that never hand the list itself out). A view, an enumerator or a
span over it (`AsReadOnly`, `CollectionsMarshal.AsSpan`), storing or capturing it keeps it on the GC. Owned lists come
from `Alice.Runtime.OwnList(T)` (two per-thread slots, cleared on release, lists above 16 MiB left to the GC), so a
method that builds a scratch list per call keeps the grown backing array. A returned list moves out unreleased, as an
array does. Measured (`benchmarks/gc/lists.al`, 200k calls building a 100-element list): 59 ms, 2926 MiB allocated
and 489 gen0 collections, down to 31 ms, 0 MiB and 0 collections.

Amendment (0.12.3): owned hashes. A local `Dictionary(K, V)` with number keys and values follows the same rules (fed by
`Dictionary(K, V).new[(capacity)]`; used through indexing, `fetch`, `has_key?`, `ContainsKey`, `TryGetValue`, `Remove`,
`Clear`, `Count`/`size`, `each`; `Keys`/`Values` are live views and count as escapes) and is rented from
`Alice.Runtime.OwnDict(K, V)`. `Clear` keeps the buckets. `benchmarks/gc/hashes.al` (100k calls, a 64-key table each):
140-146 ms, 2238 MiB and 374 gen0 collections, down to 97-104 ms, 0 MiB and 0 (with `h.each { |k, v| }` inlined
to `foreach`).

Amendment (0.12.3, later): lists and hashes of any element type. Only the collection must not escape; its elements
may (they are ordinary values the program keeps or drops). `Clear` on release nulls reference slots, so a pooled
collection never keeps an object alive. Arrays keep the unmanaged rule: their pool hands out buffers without clearing
references.

