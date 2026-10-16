# Alice.Cli
Path: `src/Alice.Cli`. Thin entrypoint; no compiler logic.

| Command | Action |
|---|---|
| `alice run file.al [-- args]` | compile in memory and run |
| `alice build file.al [-o out.dll] [-r ref.dll]...` | emit IL assembly + runtimeconfig |
| `alice build file.al --native` | NativeAOT executable |
| `alice emit file.al` | print lowered C# |
| `alice new\|check\|test\|fmt\|add\|clean\|sln\|version` | see `docs/tooling.md` |

`bootstrap` removed; self-hosting deferred (ADR 0003 superseded).
