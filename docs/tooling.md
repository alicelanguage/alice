# Tooling

## CLI

Run from source: `dotnet run --project src/Alice.Cli -- <command>`.

| Command | Action |
|---|---|
| `alice new <name> [--lib\|--game]` | create package (exe, library, or MonoGame game): `alice.toml`, `src/`, `test/`, `.gitignore`; inside a workspace it is added to `members` |
| `alice new <name> --workspace` | create workspace root (`[workspace] members = []`) |
| `alice build [dir\|files] [--release] [--native] [-o out] [-r ref.dll]` | package/workspace: MSBuild build; files: compile to IL; `--native` = NativeAOT |
| `alice run [dir\|file.al] [member] [-- args]` | build and run; workspace with several exe members: `alice run <member>` |
| `alice check` | type-check, no output |
| `alice test [files]` | build the package, run every `def test_*` in `test/*.al` |
| `alice fmt [--check] [paths]` | format `.al` files in place; `--check` lists unformatted files, exit 1 (CI) |
| `alice add <pkg> [--version v] [--path p]` | add dependency to `alice.toml` (default version `*`), regenerate, restore |
| `alice update` | delete `alice.lock`, rebuild, lock newly resolved versions |
| `alice clean [dir]` | remove `bin/` and `obj/` |
| `alice emit file.al` | print lowered C# |
| `alice version` (`-V`) | version, runtime, SIMD capabilities |

`alice gen` is internal (used by `Alice.targets`). There is no `sln` command: solutions are not needed.

Global flags: `--lang en|pt|es` (or `ALICE_LANG`), `-q`/`--quiet`, `--no-color`.

Diagnostics are rustc-style with AL codes and a `help:` hint. See [errors.md](errors.md).

Single files: `alice run main.al` / `alice build main.al` follow `require` transitively; no manifest needed.

## alice.toml

```toml
[package]
name = "demo-app"
version = "0.2.0"
kind = "exe"                 # exe | lib | game (game adds MonoGame.Framework.DesktopGL 3.8.*)
target = "net10.0"           # optional TargetFramework

[dependencies]
"Newtonsoft.Json" = "13.*"   # NuGet
geo = { path = "../geo" }    # Alice package dir (has alice.toml)
legacy = { path = "../legacy/Legacy.csproj" }   # C# project
shared = { workspace = true }   # workspace member by name

[build]
native = true                # PublishAot
```

| Rule | Detail |
|---|---|
| Generated project | `<pkg>/.alice/<name>.alproj`, rewritten from `alice.toml` on every command; do not edit |
| Outputs | `bin/`, `obj/` stay in the package dir |
| Sources | `src/**/*.al` (+ `src/**/*.cs`) |
| Path deps | Alice dirs are synced recursively; a dir without `alice.toml` uses its `*.*proj` |
| `.gitignore` | `bin/ obj/ .alice/` (written by `alice new`) |
| C# -> Alice | `<ProjectReference Include="../AliceLib/.alice/AliceLib.alproj" />` |
| Alice -> C# | `CSharpLib = { path = "../CSharpLib/CSharpLib.csproj" }` |

Tests: `Tooling/ManifestTests`.

## Workspaces

```toml
# alice.toml at the root
[workspace]
members = ["geo", "apps/*"]   # trailing /* expands to every subdir with alice.toml
```

| Behavior | Detail |
|---|---|
| `alice build` at root | builds every member, dependencies first (topological) |
| `alice run` at root | the single exe member, or `alice run <member>` |
| `{ workspace = true }` | resolves to the member's generated project; unknown name is an error |
| `alice new x` inside | registers `x` in `members` |

Tests: `Tooling/WorkspaceTests`.

## alice.lock

Exact NuGet versions from the last restore (`obj/project.assets.json`), written at the workspace root or next to a standalone package. Later builds pin them (`Version="[13.0.3]"`). `alice update` refreshes. Commit it.

## fmt

Re-indents by block structure (2 spaces), aligns `when`/`rescue` with their opener, strips trailing whitespace, collapses blank-line runs, ends with one newline. Strings and comments are untouched. Idempotent. Skips `bin/` and `obj/`. Tests: `FormatterTests`.

## Tests

```crystal
# test/greet_test.al
def test_greet
  assert_equal "Hello, Bob!", greet("Bob")
  assert greet("x").size > 0
end
```

In a package, `alice test` first builds it, then compiles each test file with the package sources against the package's resolved references (NuGet, project refs, e.g. MonoGame). Prints `test result: ok. N passed; 0 failed`.

## MSBuild SDK

| File | Role |
|---|---|
| `src/Alice.Sdk/Alice.props` | defaults: net10.0, nullable errors, TieredPGO, server + concurrent GC |
| `src/Alice.Sdk/Alice.targets` | compiles `**/*.al` (excl. `bin/`, `obj/`) in any SDK project (.alproj or .csproj) |

`dotnet build` / `dotnet run` work on the generated `.alproj`. In-repo samples import the SDK via `samples/Directory.Build.props|targets`.

## Samples (`samples/`, a workspace)

| Member | Shows |
|---|---|
| `AliceLib` | Alice library: `Shape` sum type, `Point` record, `Geometry` module |
| `AliceApp` | Alice consumes `CSharpLib` (path dep) + `AliceLib` (workspace dep) |
| `BounceGame` | minimal MonoGame game |
| `Breakout` | MonoGame Breakout with headless logic tests ([README](../samples/Breakout/README.md)) |
| `CSharpLib` | C# library (`Stats`, `Counter`); not a member |
| `CSharpApp` | C# consumes `AliceLib/.alice/AliceLib.alproj`; not a member |

Alice -> C#: `using CSharpLib`, call by declared names (`Stats.Median(xs)`). C# -> Alice: Alice types are plain CLR types. Test: `Integration/SampleTests.Interop_samples_build_and_run` (builds the workspace, runs CSharpApp and AliceApp; games not run).

## `alice test`

Finds `test/**/*.al` (or `spec/`), compiles each with the project sources (their top-level code is not run).

| Style | Syntax |
|---|---|
| RSpec | `describe`/`context`, `it "x" do ... end`, `before`/`after`/`before_all`/`after_all`, `fit`/`fdescribe` focus, `xit`/`xdescribe` skip, `it "todo"` pending, `pending`/`skip` |
| Expectations | `expect(x).to eq(y)`, `not_to`, `expect { ... }.to raise_error(typeof(E), "msg")`, `.and`/`.or` |
| Matchers | `eq be be_nil be_true be_false be_truthy be_falsy be_empty be_gt be_ge be_lt be_le be_between be_within(d).of(x) be_a be_instance_of have_size include contain_exactly start_with end_with match satisfy all respond_to raise_error` |
| xUnit | `@[Test]`, `@[Test("name")]`, `@[Cases(1, 2, 3)]` (one per row); class tests get a fresh instance plus `setup`/`teardown` |
| Legacy | `def test_*` with `assert`, `assert_equal` |

| Option | Effect |
|---|---|
| `file.al:LINE` | that example, or every example in the group declared on LINE |
| `-e TEXT` | full name contains TEXT |
| `--seed N` / `--order random` | deterministic shuffle |
| `--fail-fast` | stop at first failure |
| `--format doc\|progress\|junit` | tree (default), dots, JUnit XML for CI |
| `--profile` | 10 slowest examples |

Failures print expected/got, the Alice source line, and a `alice test file.al:LINE` rerun command. Exit code 1 on failure.

## Packages (NuGet)

Restores use `dotnet restore`, so packages live in the standard .NET global cache (`~/.nuget/packages`, or `$NUGET_PACKAGES`), shared with all C# projects.

| Command | Effect |
|---|---|
| `alice add <pkg> [--version v] [--path dir]` | add to `[dependencies]` in alice.toml and restore |
| `alice remove <pkg> [--no-restore]` | remove from alice.toml |
| `alice list` | dependencies and cache path |
| `alice search <term>` | search nuget.org |
| `alice install` | restore all dependencies (refreshes alice.lock) |
| `alice install --tool <name>` | install/update a .NET global tool |
| `alice cache [--clear]` | show or clear the shared package cache |

## REPL

`alice` (no arguments, in a terminal) or `alice repl`. Piped stdin evaluates non-interactively.

| Input | Effect |
|---|---|
| `expr` | evaluates and prints `value :: Type`; the value is `ans` next time |
| multi-line `def`/`class`/`do` blocks | continue with `⋮` until the block is closed |
| `?` (empty line) | help mode: `?puts`, `?Math.Sqrt`, `?String`, `?def` |
| `;` | shell mode |
| `]` | package mode: `add`, `remove`, `list`, `search`, `install` |
| Backspace (empty line) | back to `alice>` |
| `:reset` `:load F` `:save F` `:emit CODE` `:time EXPR` `:help` `:quit` | session commands |
| `:type EXPR` `:history` `:clear` | static type, inputs so far, clear screen |
| ↑↓ | history (`~/.alice_history`); with text typed, only entries starting with it (Julia) |
| → / End / Ctrl-E at line end | accept the grey history hint |
| Ctrl-R | reverse incremental search (Ctrl-R again: older; Enter accepts; Esc cancels) |
| Tab | complete names, keywords, members (`Math.Sq`), `:commands`; several matches show a column menu |
| Alt/Ctrl ←→, Alt-B/F, Ctrl-W/K/U, Alt-D | word motion and kills |
| Ctrl-C Ctrl-L Ctrl-D | clear line, clear screen, exit |

Input is highlighted as you type (keywords, types, strings with `#{}`, numbers, symbols, comments) and the bracket
matching the cursor is underlined. Continuation lines are indented automatically; `end`/`else`/`when` dedent.

Each input compiles together with the earlier ones: definitions persist, earlier statements re-run with output
muted, so values keep static types. Side effects of earlier inputs (files, network) therefore repeat; use `:reset`.
Inside a project the REPL compiles against its packages.

## Toolchains

Installed with the [install scripts](releases.md#install), Alice manages its own versions (rustup-style):

```bash
alice toolchain available          # releases on GitHub (stable and nightly)
alice toolchain install nightly    # or stable, or 0.10.1; --no-default keeps the current default
alice toolchain default 0.10.1
alice toolchain pin 0.10.1         # writes ./alice-toolchain; applies to this directory tree
alice +nightly run app.al          # one-off
alice toolchain show               # active version and why
alice self update                  # newest release on your channel; --nightly switches
alice doctor                       # .NET SDK, PATH, toolchains, overrides, updates
eval "$(alice completions bash)"   # zsh, fish, pwsh too
```

Layout: `~/.alice/toolchains/VERSION/`, `~/.alice/bin/alice` (shim to the default), `~/.alice/settings.json`.
Selection order: `alice +VERSION`, `ALICE_TOOLCHAIN`, nearest `alice-toolchain` file, default. `ALICE_HOME` moves
the root; `ALICE_RELEASES_REPO` points at a fork; `GITHUB_TOKEN` raises API limits. Once a day a background
check caches the newest stable release and the next command prints a one-line notice; `ALICE_NO_UPDATE_CHECK=1`
or `CI` disables it.


## Editor support

Alice 0.11.1+ includes `alice lsp --stdio`. Install Alice using the normal installer,
then install the matching extension from the [extensions repository](https://github.com/alicelanguage/extensions).
VS Code and VSCodium use the same platform-specific VSIX; Zed uses the Rust/WASI
extension. The CLI must be on PATH (or configure its absolute path in the editor).

Features include diagnostics, completion, .NET members, automatic `using` edits,
hover and XML docs, definitions, symbols, signature help and formatting. NuGet IDs
and stable versions are suggested inside `[dependencies]` in `alice.toml` and
`PackageReference` in `.alproj`. Generated projects remain disposable: edit
`alice.toml`, then run `alice install` or build to refresh restored references.
C# project APIs are read from their built assemblies, including XML docs beside
the DLL. Reference assemblies are read without locking DLLs; builds refresh the editor metadata. Open Alice source buffers override disk contents for project checking.

The server checks edits after 180 ms and checks generated C# on open/save without
emitting assemblies. VS Code settings `alice.debounceMs`, `alice.deepChecks`, and
`alice.nugetCompletion` control these behaviors. For any LSP client, pass the same
keys without the `alice.` prefix in `initializationOptions`. Disable NuGet
completion for fully offline editing. Requests time out after two seconds and
successful results are cached for ten minutes. The LSP uses stdout exclusively
for framed protocol messages and stderr for server errors.
