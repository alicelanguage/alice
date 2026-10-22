#!/usr/bin/env bash
# Usage: benchmarks/gc/run.sh [rounds]   (default 3)
# Builds the GC workloads in Alice (inferred ownership), Alice with explicit `own`, and C#, runs each under workstation and server GC, alternating Alice and C#
# every round, and writes results/results.csv (median of the rounds) plus the charts from plot.py.
set -euo pipefail
export LC_ALL=C
cd "$(dirname "$0")"
ROUNDS=${1:-3}
BENCHES=(rows scratch frames trees)
OUT=../out/gc
ALICE=../../src/Alice.Cli/bin/Release/net10.0/alice.dll
echo "building..." >&2
dotnet build ../../src/Alice.Cli -c Release -v q -nologo >/dev/null
dotnet build csharp/Bench.Gc.CSharp.csproj -c Release -o "$OUT/csharp" -v q -nologo >/dev/null
for b in "${BENCHES[@]}"; do
  dotnet "$ALICE" -q build "alice/$b.al" -o "$OUT/$b/$b.dll" >/dev/null
  # alice-own/: the same programs with explicit `own` (only array workloads; objects are not owned)
  [[ -f "alice-own/$b.al" ]] && dotnet "$ALICE" -q build "alice-own/$b.al" -o "$OUT/own-$b/$b.dll" >/dev/null
done

raw=$(mktemp)
for ((r = 1; r <= ROUNDS; r++)); do
  for gc in 0 1; do
    for b in "${BENCHES[@]}"; do
      mode=$([[ $gc == 1 ]] && echo server || echo workstation)
      a=$(DOTNET_gcServer=$gc dotnet "$OUT/$b/$b.dll" | grep '^GCSTAT')
      c=$(DOTNET_gcServer=$gc dotnet "$OUT/csharp/Bench.Gc.CSharp.dll" "$b" | grep '^GCSTAT')
      echo "alice $mode $a" >>"$raw"
      if [[ -f "$OUT/own-$b/$b.dll" ]]; then
        o=$(DOTNET_gcServer=$gc dotnet "$OUT/own-$b/$b.dll" | grep '^GCSTAT')
        echo "alice-own $mode $o" >>"$raw"
      fi
      echo "csharp $mode $c" >>"$raw"
      echo "round $r $mode $b: alice $(cut -d' ' -f3 <<<"$a") ms, c# $(cut -d' ' -f3 <<<"$c") ms" >&2
    done
  done
done
mkdir -p results
python3 plot.py "$raw" results
rm -f "$raw"
