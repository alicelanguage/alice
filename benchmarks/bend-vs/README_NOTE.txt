## 2026-11-04: Bend not compatible with bend-vs suite

Attempted to install Bend via `cargo install bend-lang hvm`. Installation succeeded (bend 0.2.38, hvm installed).
However, the build command `bend pow2.bend -o pow2` fails with:

```
error: unrecognized subcommand 'pow2.bend'

Usage: bend [OPTIONS] <COMMAND>
```

Expected: Bend 2.0.39 (per README.md)
Available on crates.io: bend-lang 0.2.38
Incompatibility: command syntax changed between versions. The installed bend requires subcommands (run-rs, gen-c, etc.) rather than direct file compilation.

Bend-vs benchmarks NOT run. Results from previous measurement (i5-1235U laptop) remain in results/.
