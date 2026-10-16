#!/usr/bin/env bash
# Build compiler, Alice programs and C# baselines; run each benchmark variant in its own process.
set -euo pipefail
export LC_ALL=C
cd "$(dirname "$0")"
ROOT=..
OUT=out
# Usage: benchmarks/run.sh [bench...]   (default: all); ROUNDS=N (default 3)
BENCHES=(nbody binarytrees pipeline expr wordfreq mandelbrot parallel particles)
(( $# > 0 )) && BENCHES=("$@")

dotnet build "$ROOT/src/Alice.Cli" -c Release -v q -nologo >/dev/null
ALICE="$ROOT/src/Alice.Cli/bin/Release/net10.0/alice.dll"
dotnet build csharp/Bench.CSharp.csproj -c Release -o "$OUT/csharp" -v q -nologo >/dev/null
for b in "${BENCHES[@]}"; do
  dotnet "$ALICE" build "alice/$b.al" -o "$OUT/$b/$b.dll" >/dev/null
done

# RESULT <name> <ms> <checksum> -> "ms checksum"; "-" when the variant does not exist.
result() { "$@" | awk '/^RESULT/ {print $3, $4; f=1} END {if (!f) print "- -"}'; }

# Variants run interleaved for ROUNDS rounds (default 3) and report the median: thermal throttling or
# background load then hits all variants alike instead of whichever benchmark happens to be running.
ROUNDS="${ROUNDS:-3}"
declare -A T C
for ((r = 0; r < ROUNDS; r++)); do
  for b in "${BENCHES[@]}"; do
    read -r ms cs < <(result dotnet "$OUT/$b/$b.dll");                              T[$b,a]+="$ms "; C[$b,a]=$cs
    read -r ms cs < <(result dotnet "$OUT/csharp/Bench.CSharp.dll" "$b" idiomatic); T[$b,i]+="$ms "; C[$b,i]=$cs
    read -r ms cs < <(result dotnet "$OUT/csharp/Bench.CSharp.dll" "$b" handopt);   T[$b,h]+="$ms "; C[$b,h]=$cs
  done
done
med() { [[ "$1" == -* ]] && { echo -; return; }; tr ' ' '\n' <<<"$1" | grep -v '^$' | sort -g | awk '{v[NR]=$1} END {printf "%.1f", (NR % 2) ? v[(NR+1)/2] : (v[NR/2] + v[NR/2+1]) / 2}'; }
spread() { tr ' ' '\n' <<<"$1" | grep -v '^$' | sort -g | awk 'NR==1 {lo=$1} {hi=$1} END {printf "%.0f%%", lo > 0 ? 100 * (hi - lo) / lo : 0}'; }

echo "Median of $ROUNDS interleaved rounds; spread = (max - min) / min of the Alice runs."
echo
echo "| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms | Alice/C# idiomatic | Alice/C# hand-opt | spread | checksum |"
echo "|---|---:|---:|---:|---:|---:|---:|---|"
for b in "${BENCHES[@]}"; do
  a=$(med "${T[$b,a]}"); i=$(med "${T[$b,i]}"); h=$(med "${T[$b,h]}")
  ok="ok"
  [[ "${C[$b,a]}" == "${C[$b,i]}" && ( "${C[$b,h]}" == "-" || "${C[$b,h]}" == "${C[$b,a]}" ) ]] || ok="MISMATCH ${C[$b,a]}/${C[$b,i]}/${C[$b,h]}"
  ri=$(awk -v a="$a" -v c="$i" 'BEGIN {printf "%.2f", a / c}')
  rh=$([[ $h == - ]] && echo - || awk -v a="$a" -v c="$h" 'BEGIN {printf "%.2f", a / c}')
  printf '| %s | %s | %s | %s | %s | %s | %s | %s |\n' "$b" "$a" "$i" "$h" "$ri" "$rh" "$(spread "${T[$b,a]}")" "$ok"
done
