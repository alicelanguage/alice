#!/usr/bin/env bash
# Bend vs Alice vs C / Rust / C#: cold-process wall time (hyperfine, median of RUNS) for serial (1 core) and parallel (12 threads).
# Usage: ./run.sh [build]   results -> results/*.json, summary -> results/summary.md (via report.py)
set -euo pipefail
cd "$(dirname "$0")"
RUNS=${RUNS:-7}
ROOT=$(cd ../.. && pwd)
WORK=("pow2 28" "fib 38" "tree 23" "nqueens 14" "mandel 11" "collatz 22")

if [[ "${1:-}" == build ]]; then
  (cd bend && for f in pow2 fib tree nqueens mandel collatz; do bend $f.bend -o $f; done)
  (cd c && clang -O3 -march=native -ffp-contract=off bench.c -o bench_serial && gcc -O3 -march=native -ffp-contract=off -fopenmp bench.c -o bench_omp)
  (cd rust && RUSTFLAGS="-C target-cpu=native" cargo build --release --offline -q)
  (cd csharp && dotnet build -c Release -o out -v q -nologo >/dev/null)
  dotnet build "$ROOT/src/Alice.Cli" -c Release -v q -nologo >/dev/null
  (cd alice && dotnet "$ROOT/src/Alice.Cli/bin/Release/net10.0/alice.dll" -q build bench.al -o out/bench.dll)
fi

mkdir -p results
ONE="taskset -c 2"
for w in "${WORK[@]}"; do
  set -- $w; name=$1; n=$2
  hyperfine -N -w 1 -r "$RUNS" --export-json "results/$name.json" \
    -n "bend/1"    "$ONE bend/$name $n +RTS --threads 1" \
    -n "bend/12"   "bend/$name $n" \
    -n "alice/1"   "$ONE env DOTNET_PROCESSOR_COUNT=1 dotnet alice/out/bench.dll $name $n" \
    -n "alice/12"  "dotnet alice/out/bench.dll $name $n" \
    -n "c/1"       "$ONE c/bench_serial $name $n" \
    -n "c/12"      "c/bench_omp $name $n" \
    -n "rust/1"    "$ONE rust/target/release/bench $name $n" \
    -n "rust/12"   "env PAR=1 rust/target/release/bench $name $n" \
    -n "csharp/1"  "$ONE env PAR=0 DOTNET_PROCESSOR_COUNT=1 dotnet csharp/out/Bench.dll $name $n" \
    -n "csharp/12" "env PAR=1 dotnet csharp/out/Bench.dll $name $n" >/dev/null
  echo "done $name" >&2
done
# startup floor: trivially small input
hyperfine -N -w 1 -r "$RUNS" --export-json results/startup.json \
  -n bend "bend/pow2 1" -n alice "dotnet alice/out/bench.dll pow2 1" -n c "c/bench_serial pow2 1" \
  -n rust "rust/target/release/bench pow2 1" -n csharp "dotnet csharp/out/Bench.dll pow2 1" >/dev/null
# peak RSS, one run per configuration
: > results/rss.tsv
for w in "${WORK[@]}"; do
  set -- $w; name=$1; n=$2
  for cfg in "bend/1|$ONE bend/$name $n +RTS --threads 1" "bend/12|bend/$name $n" \
             "alice/1|$ONE env DOTNET_PROCESSOR_COUNT=1 dotnet alice/out/bench.dll $name $n" "alice/12|dotnet alice/out/bench.dll $name $n" \
             "c/1|$ONE c/bench_serial $name $n" "c/12|c/bench_omp $name $n" \
             "rust/1|$ONE rust/target/release/bench $name $n" "rust/12|env PAR=1 rust/target/release/bench $name $n" \
             "csharp/1|$ONE env PAR=0 DOTNET_PROCESSOR_COUNT=1 dotnet csharp/out/Bench.dll $name $n" "csharp/12|env PAR=1 dotnet csharp/out/Bench.dll $name $n"; do
    kb=$( { /usr/bin/time -f "%M" ${cfg#*|} >/dev/null; } 2>&1 | tail -1 )
    printf "%s\t%s\t%s\n" "$name" "${cfg%%|*}" "$kb" >> results/rss.tsv
  done
done
python3 report.py
