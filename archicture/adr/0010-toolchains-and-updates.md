# 0010 - Toolchain manager, installers and nightlies

Status: accepted

Context: ADR 0009 ships complete release directories but leaves installation, updates and multiple versions to the user. Projects need to pin a compiler version; contributors want nightlies; Windows users should not need Visual Studio or administrator rights.

Decision:

- `alice` manages its own versions, rustup-style, under `ALICE_HOME` (default `~/.alice`): `toolchains/VERSION/` holds unmodified release directories, `bin/alice` (or `alice.cmd`) is a shim to the default, `settings.json` stores the default.
- Selection: `alice +VERSION`, then `ALICE_TOOLCHAIN`, then the nearest `alice-toolchain` file, then the default. A non-default selection re-executes that toolchain's binary with `ALICE_TOOLCHAIN_ACTIVE` set, so dispatch never loops.
- Installs come from GitHub Releases only, are verified against the published `.sha256`, and extract into a temporary directory that is moved into place, so a failed install leaves nothing behind.
- Update notices: at most one background request per day, cached; never blocks a command; disabled in CI and by `ALICE_NO_UPDATE_CHECK`.
- Bootstrap installers (`install.sh`, `install.ps1`) are plain scripts, attached to every release and served by the site. They install the .NET 10 SDK per-user through Microsoft's `dotnet-install` script when it is missing.
- Nightlies: version `X.Y.(Z+1)-nightly.STAMP`, published as one rolling `nightly` prerelease by the same workflow and gates; scheduled runs skip unchanged commits.

Consequences: Several versions coexist without conflicts; projects are reproducible through `alice-toolchain`. Release asset names and the archive layout are now a public contract (`Platform.ArchiveName`, both scripts). Only one nightly is downloadable at a time.
