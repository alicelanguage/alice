## 2026-11-04: Bend 2.0.39 not installable on the forge

Attempted installations:
1. `cargo install bend-lang` → succeeded, but installed 0.2.38 (latest on crates.io), not 2.0.39
2. `cargo install --git https://github.com/HigherOrderCO/Bend --tag v2.0.39 bend-lang` → failed

Error from GitHub install:
```
Updating git repository `https://github.com/HigherOrderCO/Bend`
error: could not find `Cargo.toml` in `/home/arch/.cargo/git/checkouts/bend-24fcabe54a3cd639/a3efe8a`
```

Tag v2.0.39 exists (ref a3efe8a3801f423827424227172eed9802c5e6cb) but no Cargo.toml at that ref.

Expected: Bend 2.0.39 (per README.md, used for original measurements)
Available: bend-lang 0.2.38 (incompatible CLI syntax)

Bend-vs benchmarks NOT run. Results from previous measurement (i5-1235U laptop, Bend 2.0.39) remain in results/.
