#!/usr/bin/env bash
# Build compiler, Alice programs and C# baselines; run each benchmark variant in its own process.
set -euo pipefail
export LC_ALL=C
cd "$(dirname "$0")"
ROOT=..
OUT=out
BENCHES=(nbody binarytrees pipeline expr wordfreq mandelbrot parallel particles)

dotnet build "$ROOT/src/Alice.Cli" -c Release -v q -nologo >/dev/null
ALICE="$ROOT/src/Alice.Cli/bin/Release/net10.0/alice.dll"
dotnet build csharp/Bench.CSharp.csproj -c Release -o "$OUT/csharp" -v q -nologo >/dev/null
for b in "${BENCHES[@]}"; do
  dotnet "$ALICE" build "alice/$b.al" -o "$OUT/$b/$b.dll" >/dev/null
done

# RESULT <name> <ms> <checksum> -> "ms checksum"; "-" when the variant does not exist.
result() { "$@" | awk '/^RESULT/ {print $3, $4; f=1} END {if (!f) print "- -"}'; }

echo "| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/C# idiomatic | checksum |"
echo "|---|---:|---:|---:|---:|---|"
for b in "${BENCHES[@]}"; do
  read -r a ac < <(result dotnet "$OUT/$b/$b.dll")
  read -r i ic < <(result dotnet "$OUT/csharp/Bench.CSharp.dll" "$b" idiomatic)
  read -r h hc < <(result dotnet "$OUT/csharp/Bench.CSharp.dll" "$b" handopt)
  ok="ok"
  [[ "$ac" == "$ic" && ( "$hc" == "-" || "$hc" == "$ac" ) ]] || ok="MISMATCH $ac/$ic/$hc"
  ratio=$(awk -v a="$a" -v c="$i" 'BEGIN {printf "%.2f", a / c}')
  printf '| %s | %.1f | %.1f | %s | %s | %s |\n' "$b" "$a" "$i" \
    "$( [[ $h == - ]] && echo - || printf '%.1f' "$h")" "$ratio" "$ok"
done
