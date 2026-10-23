# ADR 0020: Alice on NuGet

Status: Accepted (0.12.3)

Release archives (ADR 0009) install the `alice` CLI on a machine. A C# team that wants to add Alice files to a
project, or a CI image without the archive, needs Alice from the place every .NET dependency comes from: NuGet.

Decision: three packages, versioned with `Directory.Build.props`, built by `scripts/pack-nuget.sh`.

| Package | Use | Contents |
|---|---|---|
| `Alice.Sdk` | `<Project Sdk="Alice.Sdk/0.12.3">` in any .csproj or .alproj: compiles every `*.al` with the C# sources | `Sdk/Sdk.props`, `Sdk/Sdk.targets` (wrap Microsoft.NET.Sdk), `tools/` = compiler, Alice.Runtime, `sdk/Alice.props` and `Alice.targets` |
| `Alice.Cli` | `dotnet tool install -g Alice.Cli`: the `alice` command | the CLI's publish output, including `sdk/` |
| `Alice.Runtime` | C# hosts that call the runtime directly | the runtime assembly |

The SDK package uses the same `Alice.targets` as the archive: `AliceHome` points at `tools/`, so there is one
build pipeline. Libraries packed with `alice pack` keep bundling `Alice.Runtime.dll` (no dependency on a
nuget.org package that a private feed may lack). Bundling becomes a `PackageReference` on `Alice.Runtime` once
the package is published and versions are pinned.

Tests: `Integration/SdkPackageTests` packs `Alice.Sdk` into a folder feed and builds a C# + Alice project against
it with an empty NuGet cache. `Integration/PackageTests` covers `alice pack` / `alice publish`.
