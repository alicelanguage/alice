#!/usr/bin/env bash
# Alice Core benchmarks (ADR 0036): regex and JSON, Alice vs .NET BCL vs Rust vs simdjson on one machine.
# Usage: ./run.sh [build] [json] [regex] [cold]     (no suite argument = json regex cold)
#   build  fetch the corpus if missing, build every comparator and every Alice benchmark under {regex,json}/*.al
#   json   warm in-process runs: DOM, walk, typed, serialize (GB/s, allocated bytes/call)
#   regex  warm in-process runs: regex-redux 5M, rebar-like counts
#   cold   hyperfine (one process per call, ONCE=1) on a representative subset
# Env: CPU (taskset list, default "14,30": both hyperthreads of one physical core of the forge; never a single
#      logical CPU: with ProcessorCount == 1 .NET runs JsonDocument.Parse ~3.4x slower, results/baseline-forge.md),
#      WARM_MS, WARM_N, TIMED (see baselines/*), RUNS (hyperfine runs, default 7).
# Output: results/raw/<stamp>.txt (RESULT lines), results/raw/cold-*.json, results/latest.md (table).
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$(cd ../.. && pwd)
CPU=${CPU:-14,30}
RUNS=${RUNS:-7}
PIN="taskset -c $CPU"
D=data
RS=baselines/rust/target/release/core-baselines
SJ=baselines/simdjson/out/simdjson-driver
NET="dotnet $D/out-dotnet/core-baselines.dll"
ALICE="dotnet $ROOT/src/Alice.Cli/bin/Release/net10.0/alice.dll"
JSON_FILES=(twitter canada citm_catalog github_events gsoc-2018 mesh)
TYPED=(twitter canada citm_catalog)

suites=("$@")
[[ ${#suites[@]} -eq 0 ]] && suites=(json regex cold)
has() { local s; for s in "${suites[@]}"; do [[ $s == "$1" ]] && return 0; done; return 1; }

# Alice benchmarks: every regex/*.al and json/*.al is built to $D/out-alice/<suite>-<name>.dll and run as
#   dotnet <dll> <data-dir>   printing the same RESULT lines as the comparators.
alice_benches() { local s f; for s in regex json; do for f in "$s"/*.al; do [[ -e $f ]] && echo "$f"; done; done; }

if has build; then
  [[ -s $D/twitter.json && -s $D/regexredux-input.txt ]] || ./fetch-corpus.sh
  (cd baselines/rust && RUSTFLAGS="-C target-cpu=native" cargo build --release -q)
  baselines/simdjson/build.sh
  dotnet build baselines/dotnet -c Release -o "$D/out-dotnet" -nologo -v q >/dev/null
  dotnet build "$ROOT/src/Alice.Cli" -c Release -nologo -v q >/dev/null
  mkdir -p "$D/out-alice"
  for f in $(alice_benches); do
    s=$(dirname "$f"); n=$(basename "$f" .al)
    $ALICE -q build "$f" -o "$D/out-alice/$s-$n.dll"
  done
fi

mkdir -p results/raw
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
RAW=results/raw/$STAMP.txt
{
  echo "# machine: $(lscpu | sed -n 's/^Model name: *//p') | $(nproc) cpus | $(uname -r) | dotnet $(dotnet --version) | $(rustc --version) | $(clang --version | head -1) | pinned cpu $CPU"
} > "$RAW"
run() { echo "+ $*" >&2; $PIN "$@" 2>/dev/null | grep '^RESULT' | tee -a "$RAW"; }

if has json; then
  for f in "${JSON_FILES[@]}"; do
    run $NET json-dom "$D/$f.json"
    run $RS json-dom "$D/$f.json"
  done
  run $SJ $(printf "$D/%s.json " "${JSON_FILES[@]}")
  for f in "${TYPED[@]}"; do
    run $NET json-typed "$f" "$D/$f.json"
    run $RS json-typed "$f" "$D/$f.json"
  done
  for f in $(alice_benches | grep '^json/' || true); do run dotnet "$D/out-alice/json-$(basename "$f" .al).dll" "$D"; done
fi

if has regex; then
  # one regex-redux call takes ~1 s: fewer warm-up calls
  WARM_N=${WARM_N:-3} TIMED=${TIMED:-5} run $NET regex-redux "$D/regexredux-input.txt"
  WARM_N=${WARM_N:-3} TIMED=${TIMED:-5} run $RS regex-redux "$D/regexredux-input.txt"
  for h in rebar-en-sampled rebar-sherlock; do
    run $NET rebar "$D/$h.txt"
    run $RS rebar "$D/$h.txt"
  done
  for f in $(alice_benches | grep '^regex/' || true); do run dotnet "$D/out-alice/regex-$(basename "$f" .al).dll" "$D"; done
fi

if has cold; then
  # Cold = whole process (runtime start, JIT at tier 0, one call). Same commands as above with ONCE=1.
  hyperfine -N -w 1 -r "$RUNS" --export-json "results/raw/cold-json-$STAMP.json" \
    -n "stj/twitter-dom"      "$PIN env ONCE=1 $NET json-dom $D/twitter.json" \
    -n "rust/twitter-dom"     "$PIN env ONCE=1 $RS json-dom $D/twitter.json" \
    -n "simdjson/twitter-dom" "$PIN env ONCE=1 $SJ $D/twitter.json" \
    -n "stj/twitter-typed"    "$PIN env ONCE=1 $NET json-typed twitter $D/twitter.json" \
    -n "rust/twitter-typed"   "$PIN env ONCE=1 $RS json-typed twitter $D/twitter.json" >/dev/null
  hyperfine -N -w 1 -r "$RUNS" --export-json "results/raw/cold-regex-$STAMP.json" \
    -n "dotnet/compiled/redux"       "$PIN env ONCE=1 $NET regex-redux $D/regexredux-input.txt compiled" \
    -n "dotnet/nonbacktracking/redux" "$PIN env ONCE=1 $NET regex-redux $D/regexredux-input.txt nonbacktracking" \
    -n "rust/regex/redux"            "$PIN env ONCE=1 $RS regex-redux $D/regexredux-input.txt" >/dev/null
fi

python3 - "$RAW" results/raw <<'EOF' > results/latest.md
import json, sys, glob, os
raw, rawdir = sys.argv[1], sys.argv[2]
lines = open(raw).read().splitlines()
print(lines[0].lstrip('# ').replace('machine: ', 'Machine: ') + '\n')
print('| case | input | engine | median ms | GB/s | alloc B/call | checksum |')
print('|---|---|---|---:|---:|---:|---|')
for l in lines[1:]:
    _, eng, case, inp, ms, mbs, alloc, chk = l.split()
    print(f'| {case} | {inp} | {eng} | {float(ms):.3f} | {float(mbs)/1000:.3f} | {alloc} | {chk} |')
for f in sorted(glob.glob(os.path.join(rawdir, 'cold-*.json')))[-2:]:
    print(f'\nCold (hyperfine, {os.path.basename(f)}): ')
    print('| command | mean ms | min ms |')
    print('|---|---:|---:|')
    for r in json.load(open(f))['results']:
        t = r['summary']['time_wall_clock'] if 'summary' in r else r   # hyperfine 2.x / 1.x JSON
        print(f"| {r.get('name', r['command'])} | {t['mean']*1e3:.1f} | {t['min']*1e3:.1f} |")
EOF
echo "wrote $RAW and results/latest.md" >&2
