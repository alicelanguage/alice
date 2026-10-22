# ADR 0013: Exact numeric semantics and workstation GC default

Status: Accepted

Alice 0.12.0 fixes numeric behavior so programs need no BCL workarounds
(`System.Convert`, `System.Math`, hand-written bit loops) to get correct results.
Behavior follows Ruby/Crystal where they agree; the generated IL stays plain C#.

- Signed `/` and `%` are floored (`-7 / 2 == -4`, `-7 % 3 == 2`), matching
  `divmod`. Float `%` follows the divisor's sign too. `tdiv` and `remainder`
  truncate. Lowered to inlined `Prelude.floor_div/floor_mod`; `@[Native]` C uses
  the same helpers.
- Integer overflow raises `OverflowException` (Roslyn overflow checks, MSBuild
  `CheckForOverflowUnderflow`). `&+ &- &*` wrap. Integer `**` raises on a negative
  exponent or overflow.
- `to_i8 ... to_u64`, `to_i`, `to_i32`, `to_f`, `to_f64`, `to_f32` raise when the
  value does not fit (NaN included) and truncate floats toward zero; `to_X!` wraps
  integers and saturates floats.
- `round` is half away from zero. `round(n)`, `floor(n)`, `ceil(n)` round the
  shortest round-trip decimal digits (`0.29.floor(2) == 0.29`).
- Floats print the shortest round-trip digits with a decimal point (`2.0`), and
  scientific notation below 1e-4 or from 1e16 (`1.0e-07`, `1.0e+16`), in
  `puts`, `print`, `p`, interpolation, `to_s` and `inspect`.
- Literal `-5.abs` is `(-5).abs`; `-2 ** 2` stays `-(2 ** 2)`.
- Shift counts are modular: `x << n` uses the low 5 (32-bit) or 6 (64-bit) bits
  of `n`, so the `+ - *` that compute a count wrap instead of raising. Wrapping
  and exact arithmetic agree on every low bit, so the shifted value is the one
  exact arithmetic gives (`1 << (row - c + n - 1)` carries no overflow branch).

Generated programs use workstation concurrent GC by default: faster startup and
lower RSS for CLIs and short jobs. `alice.toml` `[runtime] gc = "server"` opts in
to per-core heaps for long-running allocation-heavy parallel work; `"low-memory"`
selects non-concurrent GC with `GCConserveMemory`.

Consequences: programs relying on truncating division, silent wraparound or the
old float text change behavior (CHANGELOG "Breaking"). Checked arithmetic adds an
overflow branch per integer op; Alice's SIMD lowering keeps lane arithmetic
wrapping because vector lanes cannot trap.
