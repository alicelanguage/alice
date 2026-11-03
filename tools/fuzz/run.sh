#!/usr/bin/env bash
# Long fuzz run: restarts the fuzzer with the next seed whenever it stops early (exit 3: hung calls leaked threads)
# until the wall-clock budget is spent. Usage: tools/fuzz/run.sh parse|typed|diff MINUTES [FIRST_SEED] [OUT_DIR]
set -u
mode=$1; minutes=$2; seed=${3:-1}
root=$(cd "$(dirname "$0")/../.." && pwd)
out=${4:-$root/tools/fuzz/findings}
bin=$root/tools/fuzz/bin/Release/net10.0/alice-fuzz
[ -x "$bin" ] || dotnet build "$root/tools/fuzz" -c Release -nologo -v q >/dev/null || exit 1
end=$(( $(date +%s) + minutes * 60 ))
while [ "$(date +%s)" -lt "$end" ]; do
  left=$(( end - $(date +%s) ))
  "$bin" "$mode" --seconds "$left" --seed "$seed" --out "$out" --root "$root"
  echo "RUN-EXIT mode=$mode seed=$seed code=$?"
  seed=$(( seed + 1 ))
done
