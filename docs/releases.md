# Releases

## Install

```bash
# Linux, macOS
curl -fsSL https://alice-lang.org/install.sh | sh
curl -fsSL .../install.sh | sh -s -- --nightly       # or --version 1.0.0, --home DIR, --no-modify-path
```

```powershell
# Windows (no administrator rights, no Visual Studio)
irm https://alice-lang.org/install.ps1 | iex
```

The scripts (`scripts/install.sh`, `scripts/install.ps1`, attached to every release and served by the site) pick the
archive for the platform, verify its SHA-256, install it into `~/.alice/toolchains/VERSION`, make it the default,
add `~/.alice/bin` to PATH (the same files are attached to every GitHub release as a mirror), and install the .NET 10 SDK per-user with Microsoft's `dotnet-install` script when it is
missing (`--no-dotnet` / `-NoDotnet` skips that). `--archive FILE` installs a downloaded archive offline. Afterwards
use `alice toolchain` and `alice self update` ([toolchains](tooling.md#toolchains)).

## Install a release manually

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), download the matching archive from the repository's GitHub Releases page, and extract it into a permanent directory. Add that directory to `PATH`, then run `alice version`.

Keep the complete extracted directory: the executable, DLLs and `sdk/` work together. These are framework-dependent builds; the .NET runtime is not bundled. Project builds, package restore and tests use the .NET SDK.

| Archive suffix | Platform | Build and smoke-test runner |
|---|---|---|
| `linux-x64.tar.gz` | Linux, x64, glibc | Ubuntu 24.04 |
| `win-x64.zip` | Windows, x64 | Windows 2025 |
| `osx-x64.tar.gz` | macOS, Intel | macOS 15 Intel (`macos-15-intel`) |
| `osx-arm64.tar.gz` | macOS, Apple silicon | macOS 15 ARM |

Linux ARM64, Alpine/musl and Windows ARM64 are not included. macOS binaries are not notarized; archives are not signed installers. SHA-256 files verify download integrity, not publisher identity. NativeAOT and optional native compilation are not part of the binary release smoke test.

Verify downloads before extraction, from the directory containing the archive and its checksum:

```bash
# Linux
sha256sum --check alice-0.10.0-linux-x64.tar.gz.sha256
# macOS
shasum -a 256 --check alice-0.10.0-osx-arm64.tar.gz.sha256
```

```powershell
# Windows: compare this hash to the first field of the downloaded .sha256 file.
Get-FileHash .\alice-0.10.0-win-x64.zip -Algorithm SHA256
Get-Content .\alice-0.10.0-win-x64.zip.sha256
```

## Quality gates

[`CI`](../.github/workflows/ci.yml) runs on pull requests, pushes to `main`, and manual dispatch. Each platform restores, builds in Release mode and runs the entire xUnit suite, including `AllocationTests` and sample integration tests. The examples verifier checks console output and live ASP.NET HTTP routes. TRX results are kept for 14 days, including failed runs when results exist. No test categories are excluded.

[`Release`](../.github/workflows/release.yml) validates the version, calls the same CI workflow by default, then packages all four targets. Each archive is extracted and its CLI tested outside the checkout: version, a loose Alice program, `alice new`, project build, project run and `alice test`. Required compiler/runtime assemblies and SDK imports are checked explicitly. A GitHub release is created only after version validation, successful or explicitly skipped tests, and successful package jobs.

To avoid repeating CI for an already tested commit:

- **Manual runs:** select **Release → Run workflow → Skip the full test stage** (`skip_tests`). The checkbox defaults to off. You can also run `gh workflow run release.yml --ref main -f skip_tests=true`.
- **Tag pushes:** set the repository Actions variable **`RELEASE_SKIP_TESTS`** to **`true`** in **Settings → Secrets and variables → Actions → Variables** before pushing the release tag. This applies to tag-triggered releases until you remove the variable or set it to `false`.

These options skip the full CI test stage inside Release. Version validation and all package smoke checks still run. Failed or canceled tests still block packaging and publishing. The standalone CI workflow is unchanged.

Actions are pinned to commit SHAs. The SDK follows the latest stable `10.0.x` patch; this is a compatibility gate, not a bit-for-bit reproducible build guarantee. Hosted runners and NuGet access are required. Integration tests restore the sample workspace, including MonoGame, without launching graphical games.

## Nightly builds

**Release → Run workflow** with `channel: nightly`, or the daily schedule (03:17 UTC), builds `main` as
`X.Y.(Z+1)-nightly.YYYYMMDD` (`release-version.ps1 -Channel nightly`; during a release candidate `X.Y.Z-rc.N` it is `X.Y.Z-nightly.YYYYMMDD`), runs the same gates and package smoke tests,
and replaces the rolling `nightly` prerelease and its tag. A scheduled run is skipped when `main` has not moved
since the last nightly. Users get it with `alice toolchain install nightly`.

## 1.0 release checklist

Every gate below was checked off, with a link to its evidence, before the `v1.0.0` tag was pushed on 2026-11-04.
Later releases use the same gates; a minor release may publish `X.Y.Z-rc.N` candidates first, and the final version
is then cut from the last candidate with no changes other than the version and the CHANGELOG heading.

| Gate | Evidence | Owner |
|---|---|---|
| Full suite green on Linux, macOS and Windows (`dotnet test`, no skipped SMT tests: `ALICE_REQUIRE_Z3=1`) | `tests/Alice.Tests` (3289 tests), `tests/Alice.Check.Tests` (46 tests), CI workflow | CTO |
| `bend2` and the repo benchmark suite re-run on the release commit and published, with machine and date | `benchmarks/bend2/results/arena.md` (2024-11-03, arch@forge), `benchmarks/bend2/results/il-vs-csharp.md`, `benchmarks/core/regex/results.md` | CTO |
| `bend-vs` re-run and published | `benchmarks/bend-vs/results/summary.md` | CTO |
| IL backend is the default target; the C# printer remains as the differential oracle (`alice emit` shows AIR/IL) | Done: IL is the default backend in 1.0.0 (ADR 0033, `ResolveBackend` in `src/Alice.Cli/Commands/CompileCommands.cs`); `--backend csharp`, `[build] backend = "csharp"` or `ALICE_BACKEND=csharp` select the C# printer | CTO |
| Fuzzers clean for 24 h on the release commit (today: the prove differential fuzz, `ProveDifferentialTests`; plus any fuzz target added before release) | `/work/reports/fuzz-findings.md`, `/work/reports/fuzz-findings-2.md` (24h+ clean runs, wt/release branch) | CTO |
| All review rounds closed (security audit findings fixed or listed as known limits in `docs/production.md`) | `/work/reports/review-r1.md` (review round 1 complete), `/work/reports/security-audit.md`, `CHANGELOG.md` Security section, `docs/production.md` Security limits | CTO |
| Every entry in [INVARIANTS.md](../INVARIANTS.md) names tests that exist and pass | `tests/Alice.Tests/Meta/ReleaseMetadataTests.cs:Invariants_file_tests_exist_and_pass` | CTO |
| `SECURITY.md` supported-versions table and contact address current for 1.x | `SECURITY.md` (updated for 1.x support policy) | lead |
| Signed archives (publisher identity, macOS notarization) | **Decision: SHA-256 only for 1.0.0** (archives verified by hash; no code-sign/notarization; documented in `docs/production.md#security-limits`); signing planned for 1.1+ | lead |
| `CHANGELOG.md` section for the version, empty `## Unreleased` above it | `CHANGELOG.md` (1.0.0 section exists) | CTO |

## Cut a release

1. Set `Version` in `Directory.Build.props` to an unused `MAJOR.MINOR.PATCH` version (1.0.0 or later), or a release candidate `MAJOR.MINOR.PATCH-rc.N` (`.github/scripts/release-version.ps1` rejects anything else). A release candidate is published as a GitHub prerelease, so `stable` installs and `alice self update` skip it. Update `CHANGELOG.md`, run `dotnet build Alice.sln` and `dotnet test`, and commit the release changes.
2. Run **Release → Run workflow** for that commit. Select **Skip the full test stage** to reuse earlier CI validation. After all four package smoke checks pass, the workflow publishes a GitHub release named `v<Version>` with the archives and checksums. If the tag does not exist, it is created at the selected commit; an existing tag is reused without moving it. Release notes record the exact build commit and workflow run.
3. Alternatively, tag the reviewed commit and push the tag to start a release:

   ```bash
   git tag -a v1.0.0 <reviewed-commit> -m "Alice 1.0.0"
   git push origin v1.0.0
   ```

4. A tag-triggered run repeats the gates (unless `RELEASE_SKIP_TESTS=true` skips the full test stage) and publishes the four archives plus four `.sha256` files with generated release notes. Choose either manual dispatch or a tag push for a release. Review the release page and add highlights from the changelog.

The tag must exactly match `v` plus the source version. Malformed or mismatched tags fail before testing or publishing. Only the final release job gets `contents: write`; pull requests cannot publish. GitHub Actions must be allowed to write repository contents for that job. No signing secrets or third-party release service are required.

Failed tests or packaging leave the release unpublished. Inspect the workflow logs and TRX artifacts, fix the problem in a new commit, and prepare a new reviewed tag. Existing releases are not overwritten; rerunning an already published tag fails at release creation.

## Package locally

PowerShell 7 and the .NET 10 SDK are required. Run on the same OS and architecture as the requested target so the extracted binary can run:

```powershell
./.github/scripts/package.ps1 -Runtime linux-x64 -Version 0.10.0 -OutputDirectory /tmp/alice-package
```

Use `win-x64`, `osx-x64` or `osx-arm64` on the respective host. Packages use ReadyToRun: their DLLs contain architecture-specific code, so `dotnet alice.dll` cannot run an Intel package on an ARM64 host. The packaging script rejects mismatched hosts before publishing. Choose a new output directory for each attempt. Successful output lives under `dist/`; `publish/`, `build/` and `smoke/` retain the build and extracted files for inspection. The script creates no tag and uploads nothing.

References: [.NET publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/), [GitHub-hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners), [GitHub CLI release creation](https://cli.github.com/manual/gh_release_create).

## NuGet packages

`scripts/pack-nuget.sh [out]` builds `Alice.Sdk`, `Alice.Cli` and `Alice.Runtime` into `artifacts/nuget/`
([ADR 0020](../archicture/adr/0020-nuget-distribution.md)). Publish them after the release checks pass:

```bash
scripts/pack-nuget.sh
dotnet nuget push "artifacts/nuget/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY"
```

| Consumer | How |
|---|---|
| any .NET project | `<Project Sdk="Alice.Sdk/0.13.0">`: `.al` files compile with the C# ones |
| a machine | `dotnet tool install -g Alice.Cli`, then `alice` |
| C# using the runtime | `dotnet add package Alice.Runtime` |

