#!/usr/bin/env bash
# Usage: benchmarks/run.sh [bench...]        (default: all)
# Builds only what changed, compiles benchmarks in parallel, runs each variant once (each program already
# reports the median of 7 timed trials) and prints every row as soon as it is measured.
set -euo pipefail
export LC_ALL=C
cd "$(dirname "$0")"
ROOT=..
OUT=out
BENCHES=(nbody binarytrees pipeline expr wordfreq mandelbrot parallel particles)
(( $# > 0 )) && BENCHES=("$@")

newer() { [[ ! -e "$1" ]] || [[ -n "$(find "${@:2}" -newer "$1" -type f -print -quit 2>/dev/null)" ]]; }

ALICE="$OUT/alice/alice.dll"
if newer "$ALICE" "$ROOT/src"; then
  echo "building alice (ReadyToRun)..." >&2
  dotnet publish "$ROOT/src/Alice.Cli" -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=true -o "$OUT/alice" -v q -nologo >/dev/null
fi
if newer "$OUT/csharp/Bench.CSharp.dll" csharp; then
  echo "building C# baselines..." >&2
  dotnet build csharp/Bench.CSharp.csproj -c Release -o "$OUT/csharp" -v q -nologo >/dev/null
fi
pids=()
for b in "${BENCHES[@]}"; do
  if newer "$OUT/$b/$b.dll" "alice/$b.al" "$ALICE"; then
    dotnet "$ALICE" -q build "alice/$b.al" -o "$OUT/$b/$b.dll" >/dev/null & pids+=($!)
  fi
done
for p in "${pids[@]}"; do wait "$p"; done

result() { "$@" | awk '/^RESULT/ {print $3, $4; f=1} END {if (!f) print "- -"}'; }
echo "| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/idiomatic | Alice/hand-opt | checksum |"
echo "|---|---:|---:|---:|---:|---:|---|"
for b in "${BENCHES[@]}"; do
  read -r a ac < <(result dotnet "$OUT/$b/$b.dll")
  read -r i ic < <(result dotnet "$OUT/csharp/Bench.CSharp.dll" "$b" idiomatic)
  read -r h hc < <(result dotnet "$OUT/csharp/Bench.CSharp.dll" "$b" handopt)
  ok="ok"
  [[ "$ac" == "$ic" && ( "$hc" == "-" || "$hc" == "$ac" ) ]] || ok="MISMATCH $ac/$ic/$hc"
  ri=$(awk -v a="$a" -v c="$i" 'BEGIN {printf "%.2f", a / c}')
  rh=$([[ $h == - ]] && echo - || awk -v a="$a" -v c="$h" 'BEGIN {printf "%.2f", a / c}')
  printf '| %s | %.1f | %.1f | %s | %s | %s | %s |\n' "$b" "$a" "$i" "$([[ $h == - ]] && echo - || printf '%.1f' "$h")" "$ri" "$rh" "$ok"
done
