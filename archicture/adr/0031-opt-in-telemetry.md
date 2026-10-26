# ADR 0031: Opt-in CLI telemetry

Status: Accepted (0.14.0)

We had no data on which commands are used, which errors people hit, or how the compiler crashes in the wild.

Decision:

- Telemetry covers the `alice` CLI and compiler tooling only. Nothing is ever added to compiled programs.
- Off by default. Consent (`unset`/`granted`/`denied` + policy version + random install id) is stored in the user
  config dir (`telemetry.toml`). Only `granted` under the current policy version sends; bumping the policy asks again.
- Ask once, one paragraph, default "no", only on an interactive terminal and never in CI. `alice self update` keeps a
  stored answer silently and asks only when unset.
- `ALICE_TELEMETRY_OPTOUT=1` / `DO_NOT_TRACK=1` always win. `alice telemetry on|off|status|show|reset|perf`.
- Fields are a fixed list (docs/telemetry.md): command name from a whitelist, versions, OS/arch, exit code, duration,
  sanitized crash type/stack, AL/CS code counts, opt-in phase timings. No paths, source, names or IPs.
- Sending is batched, async, 2 s timeout, at most 300 ms of waiting, failures silent; local queue capped at 256 KB.
- The default endpoint is a placeholder guarded by `ServerLive = false` until a deployment is confirmed.
- `tools/telemetry-server`: self-hostable ASP.NET Core + SQLite collector with strict validation (shared linked
  `TelemetryWire.cs`), per-install-id rate limit, 180-day retention, aggregate-only `/v1/stats` behind an admin token.
  Outside `Alice.sln` so the main build needs no ASP.NET Core.

Tests: `Tooling/TelemetryTests` (consent, env overrides, CI, sanitization, update prompt, queue cap),
`Integration/TelemetryServerTests` (client -> collector round trip; nothing personal stored).
