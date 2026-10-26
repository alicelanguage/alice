# ADR 0026: Specialization of higher-order functions (defunctionalization)

Status: Accepted (0.13.2)

A user function that takes a block or a `Func` was called through a delegate: `yield v` and `f.call(v)` compiled to
`f(v)`, an indirect call the JIT does not inline, and a capturing lambda allocated a closure and a delegate on every
call. Library iterators (`each`, `map`, `sum`) were already inlined (Iterators); user higher-order functions were not.

Decision (MLton-style, closed world: every callee's source is in the program):

- A call `m(args)` whose argument for a function parameter `f` is a lambda literal (or block) is lowered to
  `m__specN(other args, captures...)`, when `m` mentions `f` only as a call (`yield`, `f.call`, `f.(..)`; no
  `block_given?`, no passing `f` on, no storing it).
- The lambda body becomes a private static `[AggressiveInlining]` method `__lam_m__specN_f(params, captures)`. The
  clone is `m` lowered again (the `m__serial` precedent, ADR 0015) with each call of `f` replaced by a direct call
  of that method. No delegate is created or invoked; the JIT can inline the body into the clone's loop.
- Captured locals are extra parameters, by value, or by `ref` when any lambda of the caller assigns them (Ruby
  closures share the variable). `ref` is not used from a caller lambda, an async or iterator caller, or into a callee
  that contains lambdas (C# lambdas cannot capture `ref` parameters); those calls stay unspecialized.
- Evaluation order is unchanged: arguments left to right, then the callee body calls the lambda method exactly where
  it invoked the delegate. Purity is not required.
- Lambdas whose text is equal up to parameter and capture names share one clone. Limits: 8 clones per callee, callee
  bodies up to 400 AST nodes.
- Scope (first version): static callee and caller in the same type and file, no receiver, positional arguments, a
  callee that is not generic, async, an iterator, or a constructor. Instance methods (virtual dispatch) are left out.
- `ALICE_NO_SPECIALIZE=1` turns the pass off.

Measured (`work` = three HOF calls over 1000 elements with a capturing lambda, 200k calls, Release, noisy machine):
specialized 1.27-2.60 s vs delegates 3.05-6.64 s, about 2.3x faster, and 0 bytes allocated per call instead of a closure
plus three delegates.

Tests: `Compiler/SpecializeTests` (clone shape without `Func<`, captures by value and by ref, evaluation order,
escaping parameters left alone, shared clones, 0-byte capturing call).
