# Alice.Cli
Path: `src/Alice.Cli`. Thin entrypoint; no compiler logic.

| Folder | Role |
|---|---|
| `Commands/` | `CompileCommands` (build/run/check/emit/gen), `ProjectCommands` (new/add/clean/update), `TestCommand`, `FormatCommand`, `VersionCommand` |
| `Projects/` | `TomlLite`, `Manifest` (alice.toml -> `.alice/<name>.alproj`), `Workspace` (members, topo sort), `Lockfile` (alice.lock), `DotNet` |
| `Ui/`, `Localization/` | diagnostics rendering, en/pt/es strings |

| Command | Action |
|---|---|
| `alice run file.al [-- args]` | compile in memory (follows `require`) and run |
| `alice build file.al [-o out.dll] [-r ref.dll]...` | emit IL assembly + runtimeconfig |
| `alice build\|run` in a package/workspace | sync manifest(s), `dotnet build` members in dependency order |
| `alice emit file.al` | print lowered C# |
| `alice gen` | internal: called by `Alice.targets` |
| `alice new\|check\|test\|fmt\|add\|clean\|update\|version` | see `docs/tooling.md` |

Invariants:
- Generated `.alproj` lives in `<pkg>/.alice/`; `alice.toml` is the only source of truth.
- No `sln` command.
- Tests: `Tooling/ManifestTests`, `Tooling/WorkspaceTests`, `Integration/SampleTests`.

`bootstrap` removed; self-hosting deferred (ADR 0003 superseded).
