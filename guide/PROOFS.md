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
- At most 2^32 cases per rule (AL0214). Narrow the ranges, split the rule, or use `alice prove --smt`.

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

A failing rule prints its first counterexample in domain order (the same on every run and machine): the exact
inputs and what the rule did on them, `rule NAME FAILS: x = 1001, lo = 0 -> false`. An exception thrown while
checking (overflow, division by zero, a missing key) is a failure with its type:
`rule NAME FAILS: x = 0 -> raised DivideByZeroException`.

## Before committing

`alice prove --check` proves again and compares with `PROOF.al`. It fails when:

- a rule fails, or
- the sources changed since `PROOF.al` was written (the SHA-256 covers every source file, by relative path),
  or a rule was added or removed.

Run it in CI and before every commit; commit `PROOF.al` with the code. An agent that edits the code must run
`alice prove` and keep every rule proven. A rule may only change when the requirement it states changes.

- `alice build --prove` (or `prove = true` under `[build]` in `alice.toml`) runs `alice prove --check` after
  the build whenever the package has a `RULES.al`, so CI needs no extra step.
- `--format json` prints only a JSON summary (verdict, counterexample values, `ok`, `PROOF.al` state), rules in
  name order. The exit code is 0 only when every rule is proven and `PROOF.al` is current.
- `--timeout 30` limits each rule to 30 seconds. A rule that runs out is `not proven`: the check fails, it
  never passes by timing out.

## A worked example: the counterexample loop

`app.al` has a bug, and `RULES.al` states what `percent` must do:

```alice
def percent(part : Int32, total : Int32) : Int32
  part * 100 / total
end

# RULES.al
rule percent_in_range(part in 0..1000, total in 0..1000)
  return true if part > total
  p = percent(part, total)
  p >= 0 && p <= 100
end
```

```text
$ alice prove
╭──────────────────┬───────────┬────────┬────╮
│ rule             │     cases │ result │ ms │
├──────────────────┼───────────┼────────┼────┤
│ percent_in_range │ 1,002,001 │ FAILS  │  7 │
╰──────────────────┴───────────┴────────┴────╯
rule percent_in_range FAILS: part = 0, total = 0 -> raised DivideByZeroException
error: some rules fail: fix the code (or the rule, if the rule is wrong) and run `alice prove` again
$ echo $?
1
```

The rule is right (0 of 0 is a percentage too), so the code changes, `return 0 if total == 0`, and the loop runs
again:

```text
$ alice prove
╭──────────────────┬───────────┬────────┬────╮
│ rule             │     cases │ result │ ms │
├──────────────────┼───────────┼────────┼────┤
│ percent_in_range │ 1,002,001 │ proven │  6 │
╰──────────────────┴───────────┴────────┴────╯
```

Every input up to 1000 is fine now, but not every `Int32`. With `rule percent_in_range(part : Int32, total : Int32)`
(and `return true if part < 0 || part > total`), `alice prove --smt` finds what enumeration never reaches:

```text
$ alice prove --smt
╭──────────────────┬───────┬────────┬────╮
│ rule             │ cases │ result │ ms │
├──────────────────┼───────┼────────┼────┤
│ percent_in_range │   all │ FAILS  │ 54 │
╰──────────────────┴───────┴────────┴────╯
rule percent_in_range FAILS: part = 148725653, total = 603979777 -> raised OverflowException
```

`part * 100` overflows Int32. Computing in Int64, `(part.to_i64 * 100 / total).to_i32`, makes it `proven` for every
input (`all`, 1.2 s). Z3's counterexample was replayed through the compiled rule before it was printed: it really
raises.

## Whole ranges: `alice prove --smt`

`alice prove --smt` asks Z3 to prove each rule for every input instead of enumerating, so a variable may take a
whole type: `x : Int32` or `x : Int64`. Supported: integer arithmetic with Alice's semantics (checked overflow,
floored `/` and `%`, wrapping `&+ &- &*`, checked conversions), `if`/`elsif`/`else`, ternaries, early `return`,
locals, `divmod`, `Math.Min/Max`, `MIN`/`MAX` and non-recursive pure methods. A rule with loops, recursion,
objects or floats is enumerated as usual, never half-proven. `PROOF.al` marks SMT proofs `(smt)`.

Z3 is opt-in: install the system `libz3` (`pacman -S z3`, `apt install libz3-4`, `brew install z3`; on Windows a
release zip from github.com/Z3Prover/z3) or set `ALICE_Z3=path`. Without it, `--smt` is a usage error. A Z3
counterexample is always run again through the compiled rule; if the rule holds there, `alice prove` reports the
disagreement as `not proven` instead of trusting either side.

## What is proven, and what is not

- Proven: on every listed input, the rule holds for the program as this Alice version compiles and runs it.
- Not covered: inputs outside the domains. Choose domains that include the edges (`0`, negatives, the
  limits your code branches on).
- Trusted, not proven: the Alice compiler, Roslyn and the .NET runtime. Bend makes the same split between the
  kernel and its backends.

Alice's own numeric semantics (floored division, `divmod`, wrapping, bit tricks) are pinned by rules proven
the same way on every Alice release.
