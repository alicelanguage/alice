# ADR 0022: `alice prove --smt` through Z3

Status: Accepted (0.13.0)

`alice prove` (ADR 0013) enumerates every input of a rule, so domains stop at 2^32 cases. Properties of whole
Int32/Int64 ranges (`(a ^ b) ^ b == a`, floored `%` signs) need a decision procedure, not enumeration.

Decision:

- `alice prove --smt` translates each rule to bit-vector formulas and asks Z3 for an input of the domain that returns
  false or raises. Unsatisfiable means proven for every input; a model is a counterexample, printed as Alice values.
- The translation is symbolic execution with the compiler's semantics: checked `+ - * -x abs` add overflow conditions
  to the failure set, `/` and `%` are floored (ADR 0013) and fail on a zero divisor and `MIN / -1`, `&+ &- &*` wrap,
  checked conversions fail outside the target range. `if`/`elsif`/`else`, early `return`, ternaries, locals,
  `divmod`, `Math.Min/Max`, `MIN`/`MAX` constants and non-recursive pure Alice methods (inlined) are supported.
- Anything else (loops, recursion, objects, floats) makes the rule untranslatable, never half-proven: such rules fall
  back to enumeration, and a rule too large to enumerate is reported as not proven. Each query has a 20 s limit; a
  timeout is treated the same way.
- Rule parameters gain `x : Int32` and `x : Int64` (whole range). Enumeration rejects domains over 2^32 cases (AL0214)
  with a hint to use `--smt`.
- Z3 is opt-in and stays out of the compiler and runtime (pure C#). The CLI calls Z3's C API directly
  (`Proof/Z3`: `NativeLibrary` plus unmanaged function pointers into the system libz3: `pacman -S z3`,
  `apt install libz3-4`, `brew install z3`, or `ALICE_Z3=path`). There is no managed binding: Microsoft.Z3 4.12.2 is
  x64-only, while the C API is the same on every architecture, so osx-arm64 and linux-arm64 work. Without libz3,
  `--smt` is a usage error and the SMT tests skip.
- PROOF.al marks SMT proofs `proven for every input of its domain (smt)`.
