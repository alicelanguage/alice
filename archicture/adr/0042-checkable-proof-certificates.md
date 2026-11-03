# ADR 0042: Checkable Proof Certificates

## Status

**Accepted** for 1.0.0-rc.1

## Context

ADR 0022 (`alice prove --smt`) solves unbounded domains by translating rules to Z3 bit-vector queries, replaying every counterexample through the compiled rule to ensure soundness. A proven rule is trusted modulo:

1. The Alice-to-Z3 translation (floored division, checked arithmetic, wrapping operators, conversions)
2. Z3 itself
3. The compiler and runtime that execute the counterexample replay

For mission-critical guarantees, trusting a 500k-line SMT solver is often unacceptable. Lean's `bv_decide` tactic demonstrates the gold standard: bit-blast to SAT, emit an LRAT proof, check it in a tiny kernel (<2k lines). Alice 1.0 must meet that bar without depending on Lean or any external proof assistant.

## Decision

`alice prove --smt` produces **independently checkable certificates** when a proof-emitting SAT solver (CaDiCaL 1.7+, on `PATH` or `ALICE_SAT`) is available. Each certificate holds:

1. **Symbolic IR**: The rule's goal as Alice.Check terms (a minimal bit-vector DAG: variables, constants, arithmetic with Alice semantics—checked +/-/\*, wrapping &+/&-/&\*, floored `/` `%`—comparisons, if-then-else).
2. **CNF**: The Tseitin encoding of the IR's negation (DIMACS format). Deterministic: the same IR always yields the same clauses in the same order.
3. **LRAT proof**: The SAT solver's refutation of that CNF (one RUP clause per line, with hints).
4. **Hash**: SHA-256 of the certificate body (rule name, IR, CNF, proof), cited in `PROOF.al`.

`PROOF.cert` (Brotli-compressed, alongside `PROOF.al`) bundles all proven rules. `alice prove --check` re-translates each rule, compares the IR text, and re-verifies the certificate. The standalone `alice-check` tool (src/Alice.Check, <600 lines, **no dependency on Alice.Compiler/Runtime/Cli**) checks:

- The LRAT proof refutes the CNF (RUP steps only; RAT steps rejected).
- The CNF is the bit-blast of the stated IR (re-derive via the same Tseitin code and compare text).
- Hashes match.

### Trusted computing base

- **Translation**: Alice.Cli.Proof.Symbolic (rule → IR) is trusted. It reuses the existing SMT translation logic (ADR 0022), now shared between Z3 and the certificate path.
- **Proof construction**: Z3 + SAT solver (CaDiCaL) are **not** trusted; they find the proof, but `alice-check` verifies it.
- **Checker**: Alice.Check (Blaster, Lrat, Cert: bit-blasting, LRAT verification, format) is the TCB for proof validity. It has no compiler or runtime dependency; a third party can audit and reimplement it.

### Workflow

1. `alice prove --smt` (without `--check` and with CaDiCaL available):
   - Translate the rule to IR.
   - Query Z3 for a quick verdict (UNSAT or a counterexample).
   - If UNSAT or unknown, bit-blast and run CaDiCaL with `--lrat` for a proof.
   - Verify the proof in-process (alice-check logic) before accepting it.
   - Write `PROOF.al` and `PROOF.cert`.
2. `alice prove --check`:
   - Read `PROOF.cert`, re-translate every rule.
   - Compare IR text with the certificate.
   - Re-verify proof + CNF (no SAT solving).
3. `alice-check PROOF.cert [PROOF.al]`:
   - Standalone verification: no sources, no compiler, just the certificate file.

### Solver integration

- **CaDiCaL**: Preferred (1.7+). Invoked with `-q --lrat=true --binary=false -t <timeout> input.cnf output.lrat`. Exit code 20 (UNSAT) + `output.lrat` → certificate.
- **Detection**: `ALICE_SAT` or `PATH`, probed at first use (run it on a 2-variable contradiction, check LRAT output).
- **Fallback**: Without a proof-emitting solver, `--smt` still works (Z3 only, no certificates).

## Alternatives Considered

### Export to Lean and check there

Lean's `bv_decide` is exactly what we need, but:

- Adds a Lean dependency (install, elan, mathlib) to Alice's toolchain.
- Requires learning Lean's term language and proof objects.
- Certificate is Lean-specific, not reusable by other ecosystems.

**Rejected**: Alice owns its proof story; a <600-line C# checker is simpler than Lean integration.

### Trust Z3's proof objects

Z3 can emit proofs (`(get-proof)` in SMT-LIB2), but:

- The format is complex and not standardized.
- Checking it requires a Z3-aware proof checker (still large).
- Z3's proofs are less mature than SAT solvers' DRAT/LRAT.

**Rejected**: SAT + LRAT is the industry standard for verified unsatisfiability.

### Status quo (trust Z3, no certificates)

Simplest implementation, but fails the "precision of Lean" requirement. A 1.0 gate.

**Rejected**.

## Consequences

- **Precision**: Alice's proven rules are now checkable by a tiny, auditable kernel, matching Lean's trust level without depending on it.
- **Transparency**: Third parties can verify `PROOF.cert` with `alice-check` (or their own implementation of the format) without installing Alice.
- **Performance**: CaDiCaL adds ~0.1–2s per rule (bit-blasting + SAT + proof check); Z3 is tried first for speed.
- **Maintenance**: src/Alice.Check must stay minimal (<600 lines) and dependency-free. The IR and bit-blasting are now load-bearing: changes require re-checking old certificates.
- **Testing**: Differential fuzz (enumeration vs. SMT) extended to certificates; Alice.Check.Tests validate bit-blasting semantics against enumeration on small domains.

## Implementation

- **Shared IR**: src/Alice.Cli/Proof/Symbolic.cs (rule → Alice.Check.Term).
- **Checker**: src/Alice.Check (Blaster, Lrat, Cert, Ir), alice-check executable, tests/Alice.Check.Tests.
- **Integration**: src/Alice.Cli/Proof/SatCert.cs (solver invocation), Prove.CheckSmt (certificate workflow).
- **Docs**: docs/proofs.md (TCB table, re-checking), links from README and docs/language.md.
- **CI**: .github/workflows/ci.yml installs CaDiCaL on Linux, sets `ALICE_REQUIRE_SAT=1` (tests skip otherwise).
