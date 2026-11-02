#!/usr/bin/env bash
# Clones simdjson (pinned tag) into data/simdjson and builds driver.cpp over its single-header amalgamation.
# No cmake needed. Output: baselines/simdjson/out/simdjson-driver
set -euo pipefail
cd "$(dirname "$0")"
TAG=${SIMDJSON_TAG:-v3.10.1}
SRC=../../data/simdjson
if [[ ! -d "$SRC" ]]; then git clone -q --depth 1 --branch "$TAG" https://github.com/simdjson/simdjson "$SRC"; fi
mkdir -p out
clang++ -std=c++17 -O3 -march=native -DNDEBUG -I "$SRC/singleheader" driver.cpp "$SRC/singleheader/simdjson.cpp" -o out/simdjson-driver
echo "built out/simdjson-driver ($TAG)" >&2
