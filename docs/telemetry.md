# Telemetry

> Disabled in 0.14.0: the CLI never asks, records or sends. `alice telemetry` reports that it is disabled.

The `alice` CLI can send anonymous usage data, **only if you say yes**. It is off by default. Programs you compile with
Alice never contain telemetry and never send anything; this concerns the CLI and compiler tooling only (ADR 0031).

## Consent

Your answer lives in `telemetry.toml` in the user config directory:

| OS | Path |
|---|---|
| Linux / macOS | `$XDG_CONFIG_HOME/alice/telemetry.toml`, else `~/.config/alice/telemetry.toml` |
| Windows | `%APPDATA%\alice\telemetry.toml` |
| any | `$ALICE_CONFIG_DIR/telemetry.toml` when set |

```toml
state = "granted"        # unset | granted | denied
policy_version = 1       # the field list you agreed to; a newer policy asks again
install_id = "9f…"       # random 128-bit id, not derived from your machine; `alice telemetry reset` makes a new one
perf = false             # opt-in compile-phase timings
# endpoint = "https://…" # optional: send to your own collector
```

- `unset` (or no file) means nothing is sent.
- Alice asks once, with one paragraph, on an interactive terminal only: never when stdin/stdout are redirected, never
  in CI (`CI`, `GITHUB_ACTIONS`, `GITLAB_CI`, `TF_BUILD`, `BUILDKITE`, `CIRCLECI`, `TRAVIS`, `APPVEYOR`, `JENKINS_URL`,
  `TEAMCITY_VERSION`, `BITBUCKET_BUILD_NUMBER`, `CODEBUILD_BUILD_ID`, `DRONE`, `SEMAPHORE`, `BUILD_NUMBER`, `RUN_ID`,
  `CONTINUOUS_INTEGRATION`). The default answer is no.
- `alice self update` checks the stored answer after updating: granted or denied is kept silently; unset asks.
- `ALICE_TELEMETRY_OPTOUT=1` or `DO_NOT_TRACK=1` always win, whatever the file says.

## Commands

| Command | Effect |
|---|---|
| `alice telemetry status` | consent, whether anything would be sent, endpoint, config path |
| `alice telemetry show` | prints exactly the JSON one invocation would queue |
| `alice telemetry on` / `off` | grant / deny |
| `alice telemetry perf on` / `off` | include compile-phase timings |
| `alice telemetry reset` | new random install id |

## Fields

One event per CLI invocation. Nothing else is collected.

| Field | Example | Notes |
|---|---|---|
| `schema` | `1` | policy/format version |
| `install_id` | `"9f2c…"` (32 hex) | random, regenerable |
| `command` | `"build"`, `"self update"` | from a fixed list; arguments, paths and typos are never sent (`other`) |
| `alice_version` | `"0.14.0"` | |
| `os` | `"linux"` | `linux`, `windows`, `macos`, `other` |
| `arch` | `"x64"` | |
| `dotnet` | `"10.0.4"` | runtime version |
| `exit_code` | `0` | |
| `duration_ms` | `840` | whole command |
| `crash.type` | `"System.InvalidOperationException"` | only on an internal compiler/CLI crash; non-.NET/Alice types become `Exception` |
| `crash.stack` | `"   at Alice.Compiler.Binder.Bind(Node n)"` | exception message dropped; file paths, line numbers and frames outside `Alice.*`/`System.*`/`Microsoft.*`/`Spectre.*` removed (`at <external>`) |
| `error_codes` | `{"AL0202": 2}` | counts of diagnostic codes only; no message, source or file name |
| `phases_ms` | `{"transpile": 30, "emit": 80}` | only with `perf = true` |

Not collected: source code, file or project names, paths, user or machine names, environment variables, IP addresses.

## Sending

Events go to a local queue (`telemetry-queue.jsonl` next to `telemetry.toml`, capped at 256 KB, oldest dropped) and
are posted in batches (`{"events":[…]}`) with a 2 s timeout. The CLI waits at most 300 ms; failures are silent and
re-queued, and never change a command's output or exit code.

Endpoint resolution: `ALICE_TELEMETRY_ENDPOINT`, else `endpoint` in `telemetry.toml`, else the built-in default.
**The built-in default is currently disabled** (`TelemetrySender.ServerLive = false`), so an official build sends nothing
until a collector is deployed.

## Running the server

`tools/telemetry-server` is a self-hostable collector (ASP.NET Core minimal API + SQLite). It is a separate project, not
referenced by the CLI, compiler or runtime, and not part of `Alice.sln`; it shares the wire format through the linked
source file `src/Alice.Cli/Telemetry/TelemetryWire.cs`.

```bash
docker build -f tools/telemetry-server/Dockerfile -t alice-telemetry .
docker run -d -p 127.0.0.1:8080:8080 -v alice-telemetry:/data \
  -e ALICE_TELEMETRY_ADMIN_TOKEN=change-me alice-telemetry
```

| Env var | Default | |
|---|---|---|
| `ALICE_TELEMETRY_DB` | `/data/telemetry.db` | SQLite file |
| `ALICE_TELEMETRY_RETENTION_DAYS` | `180` | purged daily |
| `ALICE_TELEMETRY_RATE_PER_HOUR` | `600` | events per install id per hour (429 beyond) |
| `ALICE_TELEMETRY_ADMIN_TOKEN` | unset | required for `/v1/stats`; unset disables it (404) |
| `ASPNETCORE_URLS` | `http://+:8080` | |

Endpoints:

- `POST /v1/events`: the batch above. Body ≤ 512 KB, 1–100 events; unknown fields, wrong types, unsanitized stacks or
  malformed values are rejected with 400. Returns 202.
- `GET /v1/stats` with `Authorization: Bearer $ALICE_TELEMETRY_ADMIN_TOKEN`: aggregates only (event and install counts,
  commands, versions, OS/arch, .NET versions, error-code totals, crash signatures, p50/p95 duration, average phases).
- `GET /healthz`.

Privacy on the server: request logging is off, forwarded headers are not processed, and the remote address is never
read; the store has no column for it and records the receive **day** only.

TLS: the container speaks plain HTTP; put it behind a reverse proxy that terminates TLS and **does not log client IPs**
(e.g. Caddy `log` disabled for this site, or nginx `access_log off;`). Bind the container to localhost as above.

Building locally needs the ASP.NET Core shared framework (`dotnet build tools/telemetry-server`); the round-trip tests in
`tests/Alice.Tests/Integration/TelemetryServerTests.cs` link the collector core and do not need it.
