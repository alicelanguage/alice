#!/usr/bin/env bash
# Alice.Core.Json benchmarks (C2.1-C2.4): stage 1 per tier, DOM, on-demand, typed, serialize; System.Text.Json in the
# same process. Usage: benchmarks/core/json/run.sh [rounds] (default 3; PIN="taskset -c 7,23" by default).
# Output: results/raw-<stamp>.txt (RESULT lines of every round) and the per-case median of the rounds on stdout.
set -euo pipefail
cd "$(dirname "$0")"
ROUNDS=${1:-3}
PIN=${PIN:-taskset -c 7,23}
DATA=../data
[[ -s $DATA/twitter.json ]] || ../fetch-corpus.sh
OUT=${OUT:-/tmp/alice-json-bench}
dotnet build -c Release -o "$OUT" >/dev/null
mkdir -p results
RAW=results/raw-$(date -u +%Y%m%dT%H%M%SZ).txt
for r in $(seq "$ROUNDS"); do
  for t in avx512 avx2 vector128 scalar; do
    ALICE_JSON_TIER=$t $PIN dotnet "$OUT/alice-json-bench.dll" "$DATA" twitter canada citm_catalog | grep RESULT | grep -v bcl/ >> "$RAW"
  done
  $PIN dotnet "$OUT/alice-json-bench.dll" "$DATA" twitter canada citm_catalog github_events gsoc-2018 mesh >> "$RAW"
done
python3 - "$RAW" <<'EOF'
import sys, statistics, collections
rows = collections.defaultdict(list)
for line in open(sys.argv[1]):
    p = line.split()
    if len(p) >= 8 and p[0] == 'RESULT':
        rows[(p[2], p[3], p[1])].append((float(p[4]), float(p[5]), p[6]))
for (case, inp, eng), v in sorted(rows.items()):
    print(f"{case:15} {inp:14} {eng:30} {statistics.median(x[0] for x in v):9.4f} ms {statistics.median(x[1] for x in v)/1000:7.3f} GB/s  alloc {v[0][2]}  n={len(v)}")
EOF
echo "raw: $(pwd)/$RAW" >&2
