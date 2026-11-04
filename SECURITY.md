# Security policy

## Reporting a vulnerability

Please report security problems privately to **security@alice-lang.org** (or a GitHub private security advisory on
this repository). Do not open a public issue for a vulnerability.

Include the Alice version (`alice --version`), the platform, and the smallest input that shows the problem. We
acknowledge reports within 3 working days, agree on a fix and a disclosure date with you (90 days at most by
default), and credit you in the release notes unless you prefer otherwise.

## Supported versions

| Version | Security fixes |
|---|---|
| latest 1.x minor (1.0.x, 1.1.x, ...) | yes |
| release candidates (`X.Y.Z-rc.N`) | fixed in the next candidate or the release |
| nightly | fixed in the next nightly |
| older stable releases | no: update with `alice self update` |

## Workspace trust: what runs repo content

Treat a checkout you did not write like a program you are about to run. In an untrusted directory:

| Command | What it uses from the repo |
|---|---|
| every `alice` command (also the LSP) | `alice-toolchain` selects the toolchain: only an exact version installed under `~/.alice/toolchains` (or `stable`/`nightly`) is accepted, never a path |
| most commands (also the LSP) | `alice.toml` is turned into `.alice/NAME.alproj`; names, versions and targets are validated and other values escaped, so the file only declares settings |
| `alice build`, `run`, `test`, `pack`, `publish` | runs MSBuild and NuGet on the project: `Directory.Build.props`/`.targets`, NuGet packages (analyzers, source generators, build targets) and the program itself are code, exactly as with `dotnet build` |
| `alice run` on a `wasm` package | runs the bundle under Node.js |
| `alice prove` | compiles the rules and runs them to enumerate cases: this executes the repo code it covers |
| `alice check` | compiles only; `@[Native]` functions invoke the C compiler |
| `alice fmt`, `explain` | parse only; nothing from the repo is executed |

The CLI starts `dotnet`, `node` and `clang` (`alice doctor`) from absolute `PATH` entries only, never from the working
directory; the C compiler for `@[Native]` functions is not covered yet.
Known limits are listed in [docs/production.md](docs/production.md#security-limits).
