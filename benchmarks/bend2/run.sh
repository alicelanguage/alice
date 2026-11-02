#!/usr/bin/env bash
# Bend 2 runtime suite: Alice vs C, cold-process wall time (hyperfine -N, median of RUNS, one warm-up),
# serial (1 core) and parallel (all cores). Workloads are discovered from alice/*.al; each needs c/<name>.c.
# Usage: ./run.sh [build] [names...]   results -> results/<name>.json, summary -> results/summary.md
set -euo pipefail
cd "$(dirname "$0")"
RUNS=${RUNS:-5}
CORE=${CORE:-0}            # the core serial runs are pinned to
ROOT=$(cd ../.. && pwd)
ALICE="$ROOT/src/Alice.Cli/bin/Release/net10.0/alice.dll"

build=0
if [[ "${1:-}" == build ]]; then build=1; shift; fi
if (($#)); then NAMES=("$@"); else NAMES=(); for f in alice/*.al; do NAMES+=("$(basename "$f" .al)"); done; fi

if ((build)); then
  dotnet build "$ROOT/src/Alice.Cli" -c Release -v q -nologo >/dev/null
  for n in "${NAMES[@]}"; do
    (cd alice && dotnet "$ALICE" -q build "$n.al" -o "out/$n.dll")
    # optional second Alice column: idiomatic/<name>.al, the fastest shape (ADR 0039)
    if [[ -f "idiomatic/$n.al" ]]; then (cd idiomatic && dotnet "$ALICE" -q build "$n.al" -o "out/$n.dll"); fi
    # serial: Bend's C twin as is (the _OPENMP blocks compile out); parallel: the same file with clang -fopenmp
    clang -O3 -march=native "c/$n.c" -o "c/${n}_serial"
    clang -O3 -march=native -fopenmp "c/$n.c" -o "c/${n}_omp"
  done
fi

mkdir -p results
ONE="taskset -c $CORE"
: > results/checksums.tsv.new
[[ -f results/checksums.tsv ]] && grep -v -E "^($(IFS='|'; echo "${NAMES[*]}"))	" results/checksums.tsv >> results/checksums.tsv.new || true
for n in "${NAMES[@]}"; do
  # one checksum per configuration; report.py checks they agree
  idio=()
  if [[ -f "idiomatic/out/$n.dll" ]]; then
    idio=("idiom/1|$ONE env DOTNET_PROCESSOR_COUNT=1 dotnet idiomatic/out/$n.dll" "idiom/N|dotnet idiomatic/out/$n.dll")
  fi
  for cfg in "alice/1|$ONE env DOTNET_PROCESSOR_COUNT=1 dotnet alice/out/$n.dll" "alice/N|dotnet alice/out/$n.dll" \
             "c/1|$ONE c/${n}_serial" "c/N|c/${n}_omp" "${idio[@]}"; do
    printf "%s\t%s\t%s\n" "$n" "${cfg%%|*}" "$(${cfg#*|} | tail -1)" >> results/checksums.tsv.new
  done
  hargs=(); for c in "${idio[@]}"; do hargs+=(-n "${c%%|*}" "${c#*|}"); done
  hyperfine -N -w 1 -r "$RUNS" --export-json "results/$n.json" \
    -n "alice/1" "$ONE env DOTNET_PROCESSOR_COUNT=1 dotnet alice/out/$n.dll" \
    -n "alice/N" "dotnet alice/out/$n.dll" \
    -n "c/1"     "$ONE c/${n}_serial" \
    -n "c/N"     "c/${n}_omp" "${hargs[@]}" >/dev/null
  echo "done $n" >&2
done
mv results/checksums.tsv.new results/checksums.tsv
# .NET startup floor: the first workload found in alice/ at its smallest input (argument 0)
FIRST=$(basename "$(ls alice/*.al | head -1)" .al)
hyperfine -N -w 1 -r "$RUNS" --export-json results/startup.json   -n "alice/1" "$ONE env DOTNET_PROCESSOR_COUNT=1 dotnet alice/out/$FIRST.dll 0" -n "alice/N" "dotnet alice/out/$FIRST.dll 0" >/dev/null
echo "$FIRST" > results/startup.txt
nproc > results/cores.txt
python3 report.py
