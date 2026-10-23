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

alice build examples/web/aspnet --release
(cd examples/web/aspnet/pricing && alice test)

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
printf 'PASS ASP.NET Core: health, standard quote, bulk discount, invalid quantity, invalid price, malformed input, missing input\n'
