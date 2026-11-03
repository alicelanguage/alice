#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
if [[ -z "${ALICE_DLL:-}" ]]; then
  dotnet build src/Alice.Cli/Alice.Cli.csproj -c Release --nologo
  ALICE_DLL="$root/src/Alice.Cli/bin/Release/net10.0/alice.dll"
fi
alice() { dotnet "$ALICE_DLL" --quiet "$@"; }
expect() {
  local file="$1" expected="$2" actual
  actual="$(alice run "$root/examples/$file")"
  actual="${actual//$'\r'/}"
  if [[ "$actual" != "$expected" ]]; then
    printf 'FAIL %s\nExpected:\n%s\nActual:\n%s\n' "$file" "$expected" "$actual" >&2
    exit 1
  fi
  printf 'PASS %s\n' "$file"
}

expect basics/hello.al $'Hello, Alice!\nsimd dot = 30.0\nsquares = [1, 4, 9, 16, 25, 36, 49, 64, 81, 100]'
expect data/invoices.al $'paid invoices=3\nrevenue cents=4250\nlarge invoice cents=3000'
expect data/json_report.al $'{"service":"alice","healthy":true,"requests":42}\nALICE: 42 requests'
expect concurrency/channels.al 'sum of squares=385'
expect data/trees.al 'depth 16, leaves sum 2147450880'
expect dotnet/library_api.al $'0,2,4,6,8,10\n{"start":2,"end":9}\n15\n3,6,9\nshape of area 9.0, grown 1\nlocked section'
[[ "$(uname)" == Linux ]] && expect interop/libc.al $'strlen("héllo") = 6\nhas PATH: true\nclock ok: true\nsorted by C: 3, 7, 19, 25, 42\nembedded NUL rejected'

# Run a package (`alice run DIR -- ARGS`) and compare its whole output.
expect_pkg() {
  local dir="$1" expected="$2" actual
  shift 2
  actual="$(alice run "$root/$dir" -- "$@")"
  actual="${actual//$'\r'/}"
  if [[ "$actual" != "$expected" ]]; then
    printf 'FAIL %s\nExpected:\n%s\nActual:\n%s\n' "$dir" "$expected" "$actual" >&2
    exit 1
  fi
  printf 'PASS %s\n' "$dir"
}
# A package's `alice test` specs.
specs() {
  if ! (cd "$root/$1" && alice test >/dev/null); then printf 'FAIL %s specs\n' "$1" >&2; exit 1; fi
  printf 'PASS %s specs\n' "$1"
}
# check NAME COMMAND...: the command must succeed.
check() {
  local name="$1"
  shift
  if ! "$@"; then printf 'FAIL %s\n' "$name" >&2; exit 1; fi
  printf 'PASS %s\n' "$name"
}

# Domain samples (docs/ROADMAP-1.0.md stream H): network, CLI, games, data, desktop, low level
expect_pkg samples/netchat $'bob   <- * set a name first: /nick NAME\nbob   <- * bob joined\nbob   <- <alice> hi bob\nbob   <- * online: alice, bob\nalice <- * bye\nbob   <- * alice left\ndemo done' demo
specs samples/netchat
expect_pkg samples/alice-grep '1' -c 'def self.walk' "$root/samples/alice-grep/src/glob.al"
specs samples/alice-grep
grep_native() { (cd samples/alice-grep && alice build --release --native >/dev/null && [[ "$(.alice/bin/native/alice-grep -c 'def self.walk' src/glob.al)" == 1 ]]); }
check 'samples/alice-grep NativeAOT' grep_native
expect_pkg samples/raylib-pong 'after 3600 frames: left 0, right 2, paddle hits 32' --simulate 3600
specs samples/raylib-pong
pong_proof() { (cd samples/raylib-pong && alice prove --check RULES.al >/dev/null); }
check 'samples/raylib-pong RULES.al proof is current' pong_proof
for game in BounceGame Breakout celeste; do check "samples/$game builds" alice build "samples/$game"; done
specs samples/Breakout
specs samples/celeste
pipeline_report() { [[ "$(alice run examples/data/pipeline)" == *'"revenueCents": 29325,'*'"topProduct": "Assam tea",'* ]]; }
check 'examples/data/pipeline report' pipeline_report
specs examples/data/pipeline
notes="$(mktemp -d)"
expect_pkg examples/ui/avalonia $'title: Notes for friend\nnotes view: 2 items, "2 notes"\nsettings view: saved=true, title: Notes for Alice\ncount visible: false\nafter restart: title: Notes for Alice, name box: Alice' --headless "$notes"
rm -rf "$notes"
specs examples/ui/avalonia
if [[ "$(uname)" == Linux ]]; then
  native="$(command -v clang >/dev/null && echo true || echo false)"
  expect_pkg samples/lowlevel "pid > 0: true
strlen(\"wonderland\") = 10
qsort: 3 7 19 25 42
env_length(HOME) == strlen: true
env_length(unset) = -1
malloc'd squares 0..999: 332833500
dot = 600000.0 (managed 600000.0), native library loaded: $native
index tree: 100000 nodes, height 39, in order: true, has 923271: true, has -1: false"
  specs samples/lowlevel
fi

alice build examples/web/aspnet --release
specs examples/web/aspnet/pricing
specs examples/web/aspnet/catalog
api_checks() { [[ "$(dotnet examples/web/aspnet/apitests/bin/Release/net10.0/ApiTests.dll | tail -1)" == '14 passed, 0 failed' ]]; }
check 'ASP.NET Core: 14 WebApplicationFactory and WebSocket checks' api_checks

log="$(mktemp)"
server_pid=''
cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -f "$log"
}
trap cleanup EXIT
dotnet examples/web/aspnet/api/bin/Release/net10.0/Api.dll --urls http://127.0.0.1:0 >"$log" 2>&1 &
server_pid=$!
url=''
for ((attempt = 0; attempt < 100; attempt++)); do
  url="$(sed -n 's/.*Now listening on: \(http:\/\/127\.0\.0\.1:[0-9]*\).*/\1/p' "$log" | head -1)"
  if [[ -n "$url" ]]; then break; fi
  if ! kill -0 "$server_pid" 2>/dev/null; then cat "$log" >&2; exit 1; fi
  sleep 0.1
done
if [[ -z "$url" ]]; then cat "$log" >&2; exit 1; fi

[[ "$(curl --fail --silent --show-error "$url/health")" == '{"status":"ok"}' ]]
[[ "$(curl --fail --silent --show-error "$url/quote?unitPriceCents=1250&quantity=4")" == '{"subtotalCents":5000,"discountCents":0,"totalCents":5000}' ]]
[[ "$(curl --fail --silent --show-error "$url/quote?unitPriceCents=1250&quantity=10")" == '{"subtotalCents":12500,"discountCents":1250,"totalCents":11250}' ]]
[[ "$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$url/quote?unitPriceCents=1250&quantity=0")" == '400' ]]
[[ "$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$url/quote?unitPriceCents=2147483647&quantity=1000")" == '400' ]]
[[ "$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$url/quote?unitPriceCents=abc&quantity=10")" == '400' ]]
[[ "$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$url/quote")" == '400' ]]
json=(-H 'Content-Type: application/json')
[[ "$(curl --fail --silent --show-error "$url/products/2")" == '{"id":2,"name":"Sencha tea","priceCents":1250}' ]]
[[ "$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$url/products/99")" == '404' ]]
[[ "$(curl --silent --show-error -D - "${json[@]}" -d '{"name":"Oolong","priceCents":900}' "$url/products" | tr -d '\r' | grep -i -E '^(HTTP|location)|^\{')" == $'HTTP/1.1 201 Created\nLocation: /products/3\n{"id":3,"name":"Oolong","priceCents":900}' ]]
[[ "$(curl --silent --show-error -o /dev/null -w '%{http_code}' "${json[@]}" -d '{"name":"","priceCents":900}' "$url/products")" == '400' ]]
[[ "$(curl --fail --silent --show-error "${json[@]}" -d '{"productId":2,"quantity":10}' "$url/orders/quote")" == '{"subtotalCents":12500,"discountCents":1250,"totalCents":11250}' ]]
[[ "$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$url/ws")" == '400' ]]
printf 'PASS ASP.NET Core: health, standard quote, bulk discount, invalid quantity, invalid price, malformed input, missing input, products, create, order quote, websocket path over plain http\n'
