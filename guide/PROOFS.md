# Rules and proofs in Alice

How to state what a program must never do, and have the compiler prove it. Alice's take on Bend's `LAWS.bend`
and `PROOF.bend`.

## A rule

```alice
def clamp(x : Int32, lo : Int32, hi : Int32) : Int32
  x < lo ? lo : (x > hi ? hi : x)
end

rule clamp_stays_in_range(x in -1000..1000, lo in -50..50)
  c = clamp(x, lo, lo + 10)
  c >= lo && c <= lo + 10
end
```

- `rule name(vars) ... end` is a method returning `Bool`. The last expression is the verdict; `return true`
  early is fine.
- Each variable has a finite domain: `x in lo..hi` (inclusive), `x in lo...hi` (exclusive), or `b : Bool`.
  Bounds are integer literals, so the domain is visible in the source. A variable outside `Int32` becomes
  `Int64`.
- A rule is `pure` (enforced, AL0210): no I/O, no writes to state it did not allocate, only pure calls. So it
  gives the same answer for the same inputs, and checking every input is a proof.
- At most 2^32 cases per rule (AL0214). Narrow the ranges, or split the rule.

Put rules in `RULES.al` next to the code they constrain. Rules may call any pure function of the program.

## Proving

```text
$ alice prove
╭───────────────────────┬────────┬────────╮
│ rule                  │  cases │ result │
├───────────────────────┼────────┼────────┤
│ clamp_stays_in_range  │ 202101 │ proven │
╰───────────────────────┴────────┴────────╯
```

`alice prove` compiles every `.al` file under the current directory (or the files given), runs each rule on
every case of its domain, in parallel over the first variable, and writes `PROOF.al`:

```text
# alice 0.12.2
# sources sha256 6935e8c4...
# rule clamp_stays_in_range: proven over 202101 cases
```

A failing rule prints a counterexample, the exact inputs: `fails at x = 1001, lo = 0`. An exception thrown
while checking (overflow, division by zero, a missing key) is a failure with its type:
`x = 0: raised DivideByZeroException`.

## Before committing

`alice prove --check` proves again and compares with `PROOF.al`. It fails when:

- a rule fails, or
- the sources changed since `PROOF.al` was written (the SHA-256 covers every source file, by relative path),
  or a rule was added or removed.

Run it in CI and before every commit; commit `PROOF.al` with the code. An agent that edits the code must run
`alice prove` and keep every rule proven. A rule may only change when the requirement it states changes.

## What is proven, and what is not

- Proven: on every listed input, the rule holds for the program as this Alice version compiles and runs it.
- Not covered: inputs outside the domains. Choose domains that include the edges (`0`, negatives, the
  limits your code branches on).
- Trusted, not proven: the Alice compiler, Roslyn and the .NET runtime. Bend makes the same split between the
  kernel and its backends.

Alice's own numeric semantics (floored division, `divmod`, wrapping, bit tricks) are pinned by rules proven
the same way on every Alice release.
