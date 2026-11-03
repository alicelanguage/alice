#!/usr/bin/env bash
# Differential harness for the binder's flow checks (D1.12): compiles every .al under samples/, benchmarks/,
# examples/ (and PROOF.al, RULES.al) with `alice check` and ALICE_FLOW_ORACLE=OUT, so each compile logs the binder's
# AL0309/AL0310/AL0300-overflow diagnostics next to the Roslyn diagnostics they replace (Diagnostics/FlowOracle.cs).
#
#   tools/flow-oracle.sh OUT [ALICE]          corpus only
#   TESTS=1 tools/flow-oracle.sh OUT          also every program compiled by the test suite (tests/Alice.Tests)
#
# OUT/log.tsv has one line per compile ("same" or "DIFF", miss = Roslyn only, extra = binder only); OUT/HASH/ holds
# the sources and both diagnostic lists of each differing compile. Acceptance: no DIFF line.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?usage: tools/flow-oracle.sh OUT [ALICE]}"
alice="${2:-$root/src/Alice.Cli/bin/Release/net10.0/alice}"
[ -x "$alice" ] || { echo "no alice at $alice (dotnet build Alice.sln -c Release)" >&2; exit 2; }
mkdir -p "$out"
out="$(cd "$out" && pwd)"
export ALICE_TELEMETRY=0 DOTNET_CLI_TELEMETRY_OPTOUT=1 ALICE_FLOW_ORACLE="$out"
cd "$root"
{ find samples benchmarks examples -name '*.al' -type f -not -path '*/bin/*' -not -path '*/obj/*'; ls PROOF.al RULES.al 2>/dev/null; } | LC_ALL=C sort > "$out/.files"
check_one() { (cd "$(dirname "$1")" && "$alice" check "$(basename "$1")" > /dev/null 2>&1) || true; }
export -f check_one
export alice
xargs -P "$(nproc)" -I{} bash -c 'check_one "$1"' _ {} < "$out/.files"
if [ "${TESTS:-0}" = 1 ]; then
  dotnet test tests/Alice.Tests -c Release --no-build -nologo -v q > "$out/tests.txt" 2>&1 || true
fi
total=$(wc -l < "$out/log.tsv")
diff=$(grep -c '^DIFF' "$out/log.tsv" || true)
echo "flow oracle: $total compiles, $diff differ ($(wc -l < "$out/.files") corpus files${TESTS:+ + test suite})"
