# Contributing

Use the .NET 10 SDK. Start with [AGENTS.md](AGENTS.md) for repository rules and [the architecture notes](archicture/README.md) for compiler boundaries.

## Make a change

1. Add a failing regression test for a behavior change, then implement it.
2. Keep compiler and runtime code in C#; preserve compile-time safety and purity checks.
3. Update affected docs and `CHANGELOG.md`. Record architectural decisions in `archicture/adr/`.
4. Run the checks below before committing. Keep commits focused; leave unrelated work untouched.

```bash
dotnet build Alice.sln --configuration Release
dotnet test Alice.sln --configuration Release
bash examples/verify.sh
```

The full test suite includes compiler, runtime, tooling, architecture and interop checks. `AllocationTests` must continue to report zero allocations. Run a focused test while iterating with `dotnet test --filter FullyQualifiedName~TestClassName`.

## Add an example

Group programs by topic under `examples/basics/`, `examples/data/`, `examples/concurrency/` or `examples/web/`. Give larger integrations their own directory and README. Write example application code in Alice. Include prerequisites, exact commands and expected output. Add executable output checks to `examples/verify.sh`. Keep generated `.alice/`, `bin/` and `obj/` directories out of Git.

## Report an issue

Include `alice version`, your OS, the smallest `.al` reproducer, the command you ran and its complete diagnostic. For compiler issues, include `alice emit file.al` output when useful.

## Releases

Follow [the release guide](docs/releases.md) for versioning, validation, platform archives and checksums. Release automation is defined in [`.github/workflows/`](.github/workflows/).
