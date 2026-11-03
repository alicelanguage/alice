# Proofs

Alice validates rules through `alice prove`, which exhaustively checks every input in the rule's domain or uses symbolic execution to reason about unbounded properties. Every verdict is independently verifiable, ensuring that proven guarantees hold.

## Proving rules

A rule declares a pure property over a finite or symbolic domain:

```alice
rule add_commutes(a : Int32, b : Int32)
  a + b == b + a
end
```

### Commands

- **`alice prove`** — Enumerates finite domains (up to 2³² cases) and writes `PROOF.al`, a certificate tied to a hash of the sources.
- **`alice prove --smt`** — Uses Z3 for symbolic reasoning; handles unbounded domains like `Int32` × `Int32` (2⁶⁴ cases) or proves properties over all integers. With `--smt`, proven rules produce checkable certificates.
- **`alice prove --check`** — Re-validates all certificates without re-running the provers. Fails if any rule fails or if `PROOF.al` is stale (sources changed).

### Verdicts

- **PROVEN** — Every input in the domain makes the rule return `true`.
- **FAILS** — A counterexample exists. Alice reports the failing input and replays it through the compiled rule to confirm.
- **NOT PROVEN** — The rule uses constructs outside the supported subset (loops, recursion, external calls), Z3 times out, or the domain exceeds enumeration limits without `--smt`.

## Trusted computing base

What you must trust for each mode:

| Mode | Trusted components |
|------|-------------------|
| **Enumeration** | Alice compiler, .NET runtime, your environment |
| **--smt** | Above, plus Z3 SMT solver |
| **--smt with certificates** | Alice compiler for translation, .NET runtime for execution. **Proof checking**: only `alice-check` (< 600 lines of C#, no compiler/runtime/Z3 dependencies) verifies: (1) LRAT proof refutes the CNF, (2) CNF is the correct bit-blast of the IR, (3) hashes match |

### Certificate verification

When `--smt` generates a certificate:

1. **Translation**: Alice's compiler translates the rule into bit-vector IR (Alice.Check terms).
2. **Bit-blasting**: The IR is converted to CNF (conjunctive normal form) via Tseitin encoding.
3. **SAT solving**: A proof-emitting SAT solver (CaDiCaL) refutes the CNF and writes an LRAT proof.
4. **Checking**: `alice-check` independently verifies:
   - The LRAT proof refutes the CNF
   - The CNF matches the re-derived bit-blast of the IR
   - Hashes match between PROOF.cert and PROOF.al

The translation (rule → IR) is part of the TCB, but the proof itself (SAT, bit-blasting, LRAT checking) is verified by a tiny, auditable checker.

## Re-checking certificates

```bash
alice prove --check
```

Or use the standalone checker:

```bash
alice-check PROOF.cert PROOF.al
```

The standalone checker requires only .NET; it does not load the compiler, runtime, or any proof search tool.

## Installing a proof-emitting SAT solver

Certificates require CaDiCaL 1.7+ or another LRAT-capable solver:

```bash
# Debian/Ubuntu
apt install cadical

# macOS
brew install cadical

# Or set ALICE_SAT=/path/to/your/solver
export ALICE_SAT=/path/to/cadical
```

Without a solver, `--smt` still works (via Z3) but does not produce checkable certificates.
