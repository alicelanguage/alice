# Alice.Cli
Path: `src/Alice.Cli`. Thin entrypoint; no compiler logic.

| Command | Action |
|---|---|
| `alice run file.al` | compile and run |
| `alice build file.al [--native]` | emit assembly; `--native` = NativeAOT |
| `alice emit file.al` | print generated C# |
| `alice bootstrap` | self-host bootstrap + fixed-point check (ADR 0003) |
