#!/usr/bin/env bash
# Downloads the public JSON corpus (simdjson-data jsonexamples) and generates the regex-redux input
# (Benchmarks Game fasta, 5,000,000) into benchmarks/core/data/ (gitignored).
# Usage: ./fetch-corpus.sh [fasta-n]   (default 5000000; set FORCE=1 to re-download)
set -euo pipefail
cd "$(dirname "$0")"
N=${1:-5000000}
DATA=data
mkdir -p "$DATA"
# The corpus lives in simdjson/simdjson-data (moved out of the main repo). Pinned commit for
# reproducibility; override with SIMDJSON_DATA_REF=master.
REF=${SIMDJSON_DATA_REF:-4197c425e857f0ec38e89822fdd0bd9ea21f4daf}
BASE="https://raw.githubusercontent.com/simdjson/simdjson-data/$REF/jsonexamples"
for f in twitter.json canada.json citm_catalog.json github_events.json gsoc-2018.json mesh.json; do
  if [[ -s "$DATA/$f" && -z "${FORCE:-}" ]]; then continue; fi
  echo "fetch $f" >&2
  curl -fsSL --retry 3 -o "$DATA/$f.part" "$BASE/$f"
  mv "$DATA/$f.part" "$DATA/$f"
done
# rebar haystacks (BurntSushi/rebar, pinned) for the rebar-like regex patterns.
REBAR=${REBAR_REF:-463d00f31887e84c38467805b9e3122c314b9521}
for p in opensubtitles/en-sampled.txt sherlock.txt; do
  f="rebar-$(basename "$p")"
  if [[ -s "$DATA/$f" && -z "${FORCE:-}" ]]; then continue; fi
  echo "fetch $f" >&2
  curl -fsSL --retry 3 -o "$DATA/$f.part" "https://raw.githubusercontent.com/BurntSushi/rebar/$REBAR/benchmarks/haystacks/$p"
  mv "$DATA/$f.part" "$DATA/$f"
done
# regex-redux input: output of the Benchmarks Game fasta program for N (5M -> ~50 MB).
FASTA="$DATA/fasta-$N.txt"
if [[ ! -s "$FASTA" || -n "${FORCE:-}" ]]; then
  cc -O2 -o "$DATA/fasta" tools/fasta.c
  echo "generate $FASTA" >&2
  "$DATA/fasta" "$N" > "$FASTA.part"
  mv "$FASTA.part" "$FASTA"
fi
ln -sf "fasta-$N.txt" "$DATA/regexredux-input.txt"
# Checksums of what we measured (sha256 + size), so results can name their inputs.
(cd "$DATA" && sha256sum *.json rebar-*.txt "fasta-$N.txt" > SHA256SUMS && wc -c *.json rebar-*.txt "fasta-$N.txt") >&2
