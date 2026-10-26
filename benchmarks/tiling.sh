#!/usr/bin/env bash
# Usage: benchmarks/tiling.sh
# Loop interchange + tiling (ADR 0025), before/after: each of matmul.al and transpose.al is compiled twice, with
# the loop-tile pass disabled (before) and enabled (after), and both binaries are run. CHECK lines must agree.
set -euo pipefail
export LC_ALL=C
cd "$(dirname "$0")"
OUT=out
ALICE="$OUT/alice/alice.dll"
echo "building alice..." >&2
dotnet publish ../src/Alice.Cli -c Release -r linux-x64 --self-contained false -o "$OUT/alice" -v q -nologo >/dev/null
for b in matmul transpose; do
  ALICE_DISABLE_PASSES=loop-tile dotnet "$ALICE" -q build "alice/$b.al" -o "$OUT/$b-untiled/$b.dll" >/dev/null
  dotnet "$ALICE" -q build "alice/$b.al" -o "$OUT/$b-tiled/$b.dll" >/dev/null
done
echo "| benchmark | untiled ms | tiled ms | speedup |"
echo "|---|---:|---:|---:|"
for b in matmul transpose; do
  dotnet "$OUT/$b-untiled/$b.dll" > "$OUT/$b-untiled.txt"
  dotnet "$OUT/$b-tiled/$b.dll" > "$OUT/$b-tiled.txt"
  diff <(grep CHECK "$OUT/$b-untiled.txt" | sort -u) <(grep CHECK "$OUT/$b-tiled.txt" | sort -u) >/dev/null || echo "CHECK MISMATCH in $b" >&2
  join <(grep RESULT "$OUT/$b-untiled.txt" | awk '{print $2, $3}' | sort) <(grep RESULT "$OUT/$b-tiled.txt" | awk '{print $2, $3}' | sort) |
    awk '{printf "| %s | %.1f | %.1f | %.2fx |\n", $1, $2, $3, $2 / $3}'
done
