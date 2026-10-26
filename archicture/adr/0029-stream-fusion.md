# ADR 0029: Stream fusion of iterator chains

## Status

Accepted (0.13.2).

## Context

`src.map{}.select{}.reject{}.sink` already lowered to one loop (Lowerer.Iterators). Three gaps remained: `take(n)`
ended the chain (an intermediate array per stage below it), `to_a` grew a `List<T>` even when the length was known,
and the fusion did not check effects: `xs.map { puts ... }.select { puts ... }` interleaved the two outputs, and an
effectful stage before `any?`/`take` ran fewer times than written.

## Decision

- Stages are map/select/reject blocks and `take(n)` with a side-effect-free count (literal, local, arithmetic).
  A take is a limit and a counter read before the loop: `if (taken >= limit) break; taken++;` at its position.
- Effect gate: walking from the sink down, a stage block followed by another block, a take, or an early-exit sink
  (`any?`, `all?`, `find`, `first`) must be effect-free (`EffectFree`: writes only its own locals or arrays it
  allocates; calls only pure Alice methods (`MethodPure`), non-mutating extension methods, `[Pure]` runtime statics
  and value/string members; no lambdas, `yield`, `raise`, `<<` appends). Otherwise the chain stops there and the lower
  part runs stage by stage, as written. The sink block itself may have effects: it runs last per element either way.
- `map` and `to_a` after only maps/takes write into an exact-size array when the length is known: arrays, a string
  behind `chars`, Int32 ranges with simple bounds; each take caps it with `Math.Min(len, Math.Max(n, 0))`.
  A presized `to_a` is lowered as a `map` sink, so it stays eligible for the `Vector<T>` prologue.
- Small literal / fixed-size arrays declared inside a block being inlined become one stack buffer declared before the
  fused loop (StackArrays rules applied to the block body), when the pipeline is not itself inside a loop or lambda,
  the method is not recursive, async or an iterator, and the per-method stack budget allows it.
- Chains with a take skip the vector prologue and the SoA path; parallel planning is unchanged (it already requires
  `PureBlock` for every stage and never sees a take).

## Consequences

- Allocation tests cover take/count/max/any?/chars/stack-literal kernels at 0 bytes.
- Implicit exceptions (overflow, bounds) are not treated as effects: a fused chain can raise after earlier sink work.
- Stack buffers for block-local arrays inside an outer loop are not hoisted to method entry yet (they stay on the heap).
