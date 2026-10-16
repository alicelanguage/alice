# Releases

## Install a release

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), download the matching archive from the repository's GitHub Releases page, and extract it into a permanent directory. Add that directory to `PATH`, then run `alice version`.

Keep the complete extracted directory: the executable, DLLs and `sdk/` work together. These are framework-dependent builds; the .NET runtime is not bundled. Project builds, package restore and tests use the .NET SDK.

| Archive suffix | Platform | Build and smoke-test runner |
|---|---|---|
| `linux-x64.tar.gz` | Linux, x64, glibc | Ubuntu 24.04 |
| `win-x64.zip` | Windows, x64 | Windows 2025 |
| `osx-x64.tar.gz` | macOS, Intel | macOS 15 Intel |
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

[`Release`](../.github/workflows/release.yml) validates the version, calls the same CI workflow, then packages all four targets. Each archive is extracted and its CLI tested outside the checkout: version, a loose Alice program, `alice new`, project build, project run and `alice test`. Required compiler/runtime assemblies and SDK imports are checked explicitly. A GitHub release is created only after every test and package job passes.

Actions are pinned to commit SHAs. The SDK follows the latest stable `10.0.x` patch; this is a compatibility gate, not a bit-for-bit reproducible build guarantee. Hosted runners and NuGet access are required. Integration tests restore the sample workspace, including MonoGame, without launching graphical games.

## Cut a release

1. Set `Version` in `Directory.Build.props` to an unused `0.10.x`–`0.19.x` version. Update `CHANGELOG.md`, run `dotnet build Alice.sln` and `dotnet test`, and commit the release changes.
2. Run **Release → Run workflow** for that commit. Manual runs produce the four downloadable workflow artifacts without creating a GitHub release. Review the test and package jobs.
3. Tag the reviewed commit and push the tag:

   ```bash
   git tag -a v0.10.0 <reviewed-commit> -m "Alice 0.10.0"
   git push origin v0.10.0
   ```

4. The tag run repeats the gates and publishes the four archives plus four `.sha256` files with generated release notes. Review the release page and add highlights from the changelog.

The tag must exactly match `v` plus the source version. Malformed or mismatched tags fail before testing or publishing. Only the final release job gets `contents: write`; pull requests and manual builds cannot publish. GitHub Actions must be allowed to write repository contents for that job. No signing secrets or third-party release service are required.

Failed tests or packaging leave the release unpublished. Inspect the workflow logs and TRX artifacts, fix the problem in a new commit, and prepare a new reviewed tag. Existing releases are not overwritten; rerunning an already published tag fails at release creation.

## Package locally

PowerShell 7 and the .NET 10 SDK are required. Run on the same OS and architecture as the requested target so the extracted binary can run:

```powershell
./.github/scripts/package.ps1 -Runtime linux-x64 -Version 0.10.0 -OutputDirectory /tmp/alice-package
```

Use `win-x64`, `osx-x64` or `osx-arm64` on the respective host. Choose a new output directory for each attempt. Successful output lives under `dist/`; `publish/`, `build/` and `smoke/` retain the build and extracted files for inspection. The script creates no tag and uploads nothing.

References: [.NET publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/), [GitHub-hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners), [GitHub CLI release creation](https://cli.github.com/manual/gh_release_create).
