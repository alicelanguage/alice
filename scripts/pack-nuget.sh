#!/usr/bin/env bash
# Packs Alice for NuGet: Alice.Sdk (MSBuild SDK with the compiler), Alice.Cli (`dotnet tool install -g Alice.Cli`)
# and Alice.Runtime. Usage: scripts/pack-nuget.sh [out-dir]   (default: artifacts/nuget)
# Publish: dotnet nuget push "artifacts/nuget/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY"
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/artifacts/nuget}"
mkdir -p "$out"
for p in Alice.Runtime Alice.Cli Alice.Sdk; do
  dotnet pack "$root/src/$p" -c Release -o "$out" -nologo -v q
done
ls -1 "$out"/*.nupkg
