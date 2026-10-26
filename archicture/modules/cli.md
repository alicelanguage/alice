# Alice.Cli
Path: `src/Alice.Cli`. Thin entrypoint; no compiler logic.

| Folder | Role |
|---|---|
| `Commands/` | `CompileCommands` (build/run/check/emit/gen/explain/prove), `ProjectCommands` (new/add/clean/update), `PackageCommands` (remove/search/list/install/cache), `PublishCommands` (pack/publish), `TestCommand`, `FormatCommand`, `GuideCommand` (guide/agents), `ToolchainCommands`, `SelfCommands`, `DoctorCommand`, `CompletionsCommand`, `VersionCommand` |
| `Projects/` | `TomlLite`, `Manifest` (alice.toml -> `.alice/<name>.alproj`), `Workspace` (members, topo sort), `Lockfile` (alice.lock), `DotNet` |
| `Toolchains/` | `ToolchainVersion`, `Platform` (RIDs, archive names), `ReleaseFeed` (GitHub releases), `ToolchainHome` (~/.alice layout, overrides), `Installer` (verify + atomic extract), `UpdateNotifier` |
| `Repl/` | `ReplCommand`, `ReplSession`, `LineEditor` (hints, search, menu), `Highlighter`, `ReplHelp` |
| `Proof/` | `SmtProver` and the `Z3/` C-API binding for `alice prove --smt` (ADR 0022) |
| `Lsp/` | `alice lsp` language server (ADR 0011) |
| `Ui/`, `Localization/` | diagnostics rendering, en/pt/es strings |

| Command | Action |
|---|---|
| `alice run file.al [-- args]` | compile in memory (follows `require`) and run |
| `alice build file.al [-o out.dll] [-r ref.dll]...` | emit IL assembly + runtimeconfig |
| `alice build\|run` in a package/workspace | sync manifest(s), `dotnet build` members in dependency order |
| `alice emit file.al` | print lowered C# |
| `alice gen` | internal: called by `Alice.targets` |
| `alice toolchain\|self\|doctor\|completions` | toolchains, updates, diagnostics (ADR 0010) |
| `alice prove [--check] [--smt]` | prove `rule`s, write/verify `PROOF.al` |
| `alice new [--lib\|--tool\|--game\|--wasm]\|check\|test\|fmt\|add\|clean\|update\|version\|explain\|guide\|pack\|publish` | see `docs/tooling.md` |

Invariants:
- Generated `.alproj` lives in `<pkg>/.alice/`; `alice.toml` is the only source of truth.
- No `sln` command.
- `alice +VERSION`/overrides re-exec the selected toolchain with `ALICE_TOOLCHAIN_ACTIVE` set (no loops).
- Tests: `Tooling/ToolchainTests`, `Tooling/ToolchainCliTests`, `Tooling/ReplEditorTests`, `Tooling/ManifestTests`, `Tooling/WorkspaceTests`, `Integration/SampleTests`.

`bootstrap` removed; self-hosting deferred (ADR 0003 superseded).
