#!/usr/bin/env bash
# Emits the C# of every .al under samples/, benchmarks/ and examples/ into OUT (one .cs per source, path
# flattened with __). Output that fails to compile is kept too (stdout+stderr and the exit code), so a diff of
# two runs shows every change in emitted C#: byte-identical output is the acceptance for MIR/AIR refactors.
#
#   tools/emit-corpus.sh OUT [ALICE]      ALICE defaults to the Release build of this tree
#   diff -r before/ after/                 empty diff == no change in emitted C#
#
# Extra arguments for `alice emit` (e.g. --air) can be passed in EMIT_ARGS.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?usage: tools/emit-corpus.sh OUT [ALICE]}"
alice="${2:-$root/src/Alice.Cli/bin/Release/net10.0/alice}"
[ -x "$alice" ] || { echo "no alice at $alice (dotnet build Alice.sln -c Release)" >&2; exit 2; }
mkdir -p "$out"
out="$(cd "$out" && pwd)"
export ALICE_TELEMETRY=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$root"
find samples benchmarks examples -name '*.al' -type f | LC_ALL=C sort > "$out/.files"
emit_one() {
  local f="$1" name
  name="$(printf '%s' "$f" | sed 's|/|__|g').cs"
  local code=0
  "$alice" emit ${EMIT_ARGS:-} "$f" > "$out/$name" 2>&1 || code=$?
  [ "$code" = 0 ] || printf '\n// exit %s\n' "$code" >> "$out/$name"
}
export -f emit_one
export alice out
xargs -P "$(nproc)" -I{} bash -c 'emit_one "$1"' _ {} < "$out/.files"
echo "emitted $(wc -l < "$out/.files") files to $out"
