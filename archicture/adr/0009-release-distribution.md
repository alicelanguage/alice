# 0009 - Tested CLI distribution archives

Status: accepted

Context: Alice needs downloadable CLI binaries and repeatable release checks without changing the compiler backend or project format. Roslyn loads assemblies from disk, and generated projects import the SDK beside `alice.dll`.

Decision:

- Distribute framework-dependent, RID-specific apphosts together with all published DLLs, runtime configuration and `sdk/` files. Installation requires the .NET 10 SDK.
- Keep trimming, single-file bundling and NativeAOT disabled for the compiler distribution. Application publishing is a separate concern.
- Publish ReadyToRun packages and smoke-test them on matching operating systems and process architectures, including a native Intel macOS packaging runner.
- Run full CI on the supported operating systems before packaging by default; allow an explicit release test-stage opt-out through a manual input or repository variable. Always smoke-test the extracted archive with single-file compilation and a generated Alice project.
- Match release tags to `Directory.Build.props`; publish versioned archives and SHA-256 checksums only after validation succeeds.
- Publish from both manual release runs and tag pushes. Manual runs derive the release tag from the validated version; new tags target the selected commit, existing tags are retained, and release notes identify the exact package build.
- Keep write permissions in the final release job. Pull-request checks never publish releases.

Consequences: Users install a complete directory rather than one executable. Release archives exercise the same SDK assets used by project builds. New platforms need a runner and an archive smoke test before inclusion.

Implementation and operator commands: [release guide](../../docs/releases.md).
