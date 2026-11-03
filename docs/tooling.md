# Tooling

## CLI

Run from source: `dotnet run --project src/Alice.Cli -- <command>`.

| Command | Action |
|---|---|
| `alice new <name> [--lib\|--tool\|--game\|--wasm]` | create package (exe, library with `naming = "dotnet"`, .NET tool, MonoGame game, or [WebAssembly](#webassembly-kind--wasm) package): `alice.toml`, `src/`, `test/`, `.gitignore`; inside a workspace it is added to `members` |
| `alice new <name> --workspace` | create workspace root (`[workspace] members = []`) |
| `alice build [dir\|files] [--release] [--native] [--strict] [--prove\|--prove=smt] [-o out] [-r ref.dll]` | package/workspace: MSBuild build; files: compile to IL; `--native` = NativeAOT; `--strict` = [strict typing](language.md#strict-typing-al0220) for this build; `--prove` = then `alice prove --check` when the package has a `RULES.al` ([below](#proving-as-part-of-the-build)) |
| `alice run [dir\|file.al] [member] [--strict] [-- args]` | build and run; workspace with several exe members: `alice run <member>` |
| `alice check [--strict]` | type-check, no output; `--strict`: anything not statically typed is AL0220 |
| `alice test [files]` | build the package, run every `def test_*` in `test/*.al` |
| `alice fmt [--check] [paths]` | format `.al` files in place; `--check` lists unformatted files, exit 1 (CI) |
| `alice add <pkg> [--version v] [--path p]` | add dependency to `alice.toml` (default version `*`), regenerate, restore |
| `alice update` | delete `alice.lock`, rebuild, lock newly resolved versions |
| `alice clean [dir]` | remove `bin/` and `obj/` |
| `alice emit [--strict] file.al` | print lowered C# |
| `alice explain [files]` | show which optimizations each method received (parallel loops, stack arrays, presized collections, shared immutable objects, ...) |
| `alice prove [files] [--check] [--smt] [--timeout s] [--format text\|json]` | prove every `rule` (usually in `RULES.al`) and write `PROOF.al`; `--check` verifies only (CI); `--smt` proves rules over whole Int32/Int64 ranges with Z3; `--timeout` limits each rule; `--format json` prints a machine-readable summary ([rules and proofs](#rules-and-proofs-alice-prove)) |
| `alice guide [topic]` | print the Alice guide (`guide/GUIDE.md`); topics: `performance`, `proofs`, `interop` |
| `alice agents [--write]` | print the Alice block for a project's `AGENTS.md`; `--write` adds it to `./AGENTS.md` |
| `alice repl` (or plain `alice`) | interactive REPL ([below](#repl)) |
| `alice lsp [--stdio]` | language server ([editor support](#editor-support)) |
| `alice toolchain ...`, `alice self update` (`alice upgrade`), `alice doctor`, `alice completions <shell>` | [toolchains](#toolchains) |
| `alice version` (`-V`) | version, runtime, SIMD capabilities |

`alice gen` is internal (used by `Alice.targets`). There is no `sln` command: solutions are not needed.

Global flags: `--lang en|pt|es` (or `ALICE_LANG`), `-q`/`--quiet`, `--no-color`.

Diagnostics are rustc-style with AL codes and a `help:` hint. See [errors.md](errors.md).

Single files: `alice run main.al` / `alice build main.al` follow `require` transitively; no manifest needed.

`alice run file.al` caches the compiled image in `$XDG_CACHE_HOME/alice/run` (default `~/.cache/alice/run`). The key
covers every source and `require`d file, the references and the compiler build, so an unchanged program skips
transpiling and the Roslyn compile: a hello world goes from 1.2 s to 0.13 s on the second run. Programs with
`@[Native]` functions are not cached. `ALICE_NO_CACHE=1` turns the cache off; deleting the directory clears it.
The directory is owner-only (0700 on Unix) and every image carries an HMAC with a per-install key (`.key`, 0600);
a modified image, or one in a directory other users can write to, is a miss and gets recompiled.

## alice.toml

```toml
[package]
name = "demo-app"
version = "0.2.0"
kind = "exe"                 # exe | lib | tool | game (adds MonoGame.Framework.DesktopGL 3.8.*) | wasm (browser)
target = "net10.0"           # optional TargetFramework

[dependencies]
"Newtonsoft.Json" = "13.*"   # NuGet
geo = { path = "../geo" }    # Alice package dir (has alice.toml)
legacy = { path = "../legacy/Legacy.csproj" }   # C# project
shared = { workspace = true }   # workspace member by name

[build]
native = true                # PublishAot (identical method bodies folded, AOT analyzers on)
ready_to_run = true          # `alice build --release` also publishes precompiled code to bin/r2r (or: alice build --r2r)
threads = false              # single-threaded target: parallel loops, pipelines and forks compile to serial code (wasm default)
strict = true                # strict typing: an inferred `dynamic` (not written as `Any`) is error AL0220 (or: --strict)
prove = true                 # with a RULES.al, `alice build` also runs `alice prove --check` ("smt" adds --smt; or: --prove)

[runtime]
gc = "auto"                  # default: server GC if the program runs in parallel, else workstation; or workstation | server | low-memory
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
| `src/Alice.Sdk/Alice.props` | defaults: net10.0, nullable errors, TieredPGO, overflow checks, workstation concurrent GC (`[runtime] gc` in alice.toml) |
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
| `alice pack [dir] [-o out]` | build a `.nupkg` (lib or tool) into `bin/package/` |
| `alice publish [dir] [--source url\|dir] [--api-key k] [--dry-run] [--dotnet-push]` | pack and push; default source nuget.org, key from `NUGET_API_KEY` (preferred: an argument is visible in the process list) or `--api-key`. Feeds are pushed to by alice over https with the key in a header; an existing version is skipped. `--dotnet-push` uses `dotnet nuget push`, which takes the key as an argument |

### Publishing

```toml
[package]
name = "wand"
id = "Acme.Wand"             # NuGet id, default: name
version = "1.0.0"
kind = "tool"                # lib: a library; tool: a .NET tool (dotnet tool install -g Acme.Wand)
command = "wand"             # tool command, default: name
description = "Scaffolding for Acme apps"
authors = ["Ada"]
license = "Apache-2.0"       # SPDX expression
repository = "https://github.com/acme/wand"
tags = ["cli"]               # "alice" is always added
naming = "dotnet"            # public API in .NET style for C#/F# callers: total_area -> TotalArea, empty? -> IsEmpty (ADR 0019)
```

A library package bundles `Alice.Runtime.dll` next to its assembly, so C# projects consume it with a plain `PackageReference`. Games are not packable. Tests: `Tooling/ManifestTests`, `Integration/PackageTests`.

## WebAssembly (`kind = "wasm"`)

An Alice program that runs in the browser, with no hand-written project file:

```bash
alice new web --wasm     # src/main.al, wwwroot/index.html, wwwroot/main.js
cd web
alice run                # builds, then runs the bundle under Node (same .NET WebAssembly runtime)
alice build --release    # dist/wwwroot: the whole static site, for any static host
```

The browser runs .NET on one thread, so a wasm package compiles parallel loops, pipelines and forks to their serial
form (a `par for` that is not provably independent is still an error). `--release` trims fully, enables wasm SIMD and
uses invariant globalization (no ICU download; set `[runtime] globalization = "culture"` to keep culture rules), and
compiles ahead of time to WebAssembly when the `wasm-tools` workload is installed (`dotnet workload install wasm-tools`).
`--native` does not apply to wasm packages.

Mark methods `@[JSExport]` to call them from JavaScript, and declare `@[JSImport("globalThis.console.log")]`
`partial def`s to call JavaScript from Alice (`using System.Runtime.InteropServices.JavaScript`):

```ruby
module App
  @[JSExport]
  def self.greet(who : String) : String
    "Hello, #{who}!"
  end
end
```

```js
const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
const app = await getAssemblyExports(getConfig().mainAssemblyName);
app.App.greet('browser');
await runMain();          // the program's top-level code
```

`wwwroot/` is copied to the site root. Release sites are trimmed (a hello world is about 2 MB over the wire with
Brotli); `[runtime] globalization = "invariant"` drops the ICU data (about 2.5 MB uncompressed). No workload is
needed: the browser runtime comes from NuGet. Threads, files and native libraries are not available in the browser.

## Rules and proofs (`alice prove`)

A `rule name(x in lo..hi, ...) ... end` is a pure method returning `Bool`; `alice prove` runs it on every case of its
finite domain (at most 2^32 cases, AL0214) in parallel, and writes `PROOF.al`, a certificate holding a SHA-256 of
every source. `alice prove --check` fails when a rule fails or the sources changed since `PROOF.al` was written:
run it in CI and commit `PROOF.al`.

A failing rule prints one line with the first failing input in domain order and what the rule did on it:

```text
rule percent_in_range FAILS: part = 0, total = 0 -> raised DivideByZeroException
```

`-> false` means the rule returned false; `-> raised X` that it threw. The same input fails on every run and machine.

| Flag | Effect |
|---|---|
| `--check` | prove again, compare with `PROOF.al`, write nothing: exit 1 when a rule fails, is not proven, or `PROOF.al` is missing or stale |
| `--smt` | Z3 instead of enumeration where the rule translates ([below](#whole-ranges-with-z3---smt)) |
| `--timeout s` | time limit per rule, in seconds (`--timeout 30`, `--timeout=0.5`). A rule that runs out is `not proven` (exit 1), never proven; with `--smt` it is also Z3's limit per query (default 20 s) |
| `--format json` | only a JSON summary on stdout, rules in name order, for CI and agents (shape below) |

Exit code: 0 when every rule is proven (and, with `--check`, `PROOF.al` is current), 1 otherwise, 2 for a usage
error. Rules are always listed in ordinal name order, in the table, the JSON and `PROOF.al`.

```json
{
  "ok": false,
  "proof": "current",
  "rules": [
    {
      "rule": "percent_in_range",
      "status": "fails",
      "engine": "enumeration",
      "cases": 1002001,
      "ms": 6.3,
      "counterexample": { "part": 0, "total": 0 },
      "result": "raised DivideByZeroException",
      "message": "rule percent_in_range FAILS: part = 0, total = 0 -> raised DivideByZeroException"
    }
  ]
}
```

`proof` is `current` or `stale` with `--check`, `written` without it. `status` is `proven`, `fails` or `unknown`
(`reason` says why: a time limit, or a rule `--smt` cannot translate that is too large to enumerate); `engine` is
`enumeration` or `smt` (`cases` is then null: every input of the domain).

### Proving as part of the build

`alice build --prove`, or `prove = true` under `[build]` in `alice.toml`, runs `alice prove --check` in the package
directory after a successful build when the package has a `RULES.al`, and the build fails when a rule fails or
`PROOF.al` is stale. CI then gates on the rules with no extra step. `--prove=smt` / `prove = "smt"` adds `--smt`.
In a workspace each member with a `RULES.al` is proven after it builds. A package without `RULES.al` builds as usual.

### Whole ranges with Z3 (`--smt`)

`alice prove --smt` (ADR [0022](../archicture/adr/0022-smt-proofs.md)) translates each rule to bit-vector formulas and
asks Z3 for a counterexample, so a rule may take whole ranges (`x : Int32`, `x : Int64`). It covers integer
arithmetic with Alice's semantics (checked overflow, floored `/` and `%`, wrapping `&+ &- &*`, checked `to_X` and
wrapping `to_X!` conversions), `if`/ternaries, early `return`, locals, `divmod`, `Math.Min/Max` and inlined
non-recursive pure methods. Rules with loops, recursion, objects or floats fall back to enumeration. Every Z3
counterexample is run again through the compiled rule before it is printed: it must return false or raise there,
otherwise the rule FAILS there with `internal error: solver verdict contradicted by compiled rule` (a bug to report).
Every Z3 proof is cross-checked the same way on 64 points of the domain (bounds, then seeded random points); a point
where the compiled rule returns false or raises makes the rule FAIL with the same internal error, never proven.

How `alice prove --smt` finds Z3: it loads Z3's C library at run time (no NuGet package, any CPU architecture), and
tries in order:

1. `ALICE_Z3`, a full path to the library (`ALICE_Z3=/opt/z3/bin/libz3.so`, `ALICE_Z3=C:\z3\bin\libz3.dll`);
2. the system library by name: `libz3.so`, then `libz3.so.4` on Linux; `libz3.dylib`, `/opt/homebrew/lib/libz3.dylib`,
   `/usr/local/lib/libz3.dylib` on macOS; `libz3.dll`, then `z3.dll` on Windows (the usual DLL search: the
   application directory, then `PATH`).

Install it with `pacman -S z3`, `apt install libz3-4`, `brew install z3`, or on Windows unzip a release from
[github.com/Z3Prover/z3/releases](https://github.com/Z3Prover/z3/releases) and point `ALICE_Z3` at `bin\libz3.dll`
(CI does that, checksum-pinned, in `.github/workflows/ci.yml`). Without Z3, `--smt` is a usage error and plain
`alice prove` is unaffected. The SMT tests return early without Z3; `ALICE_REQUIRE_Z3=1` (set in CI) makes a missing
Z3 a test failure. Guide: [guide/PROOFS.md](../guide/PROOFS.md).

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
Selection order: `alice +VERSION`, `ALICE_TOOLCHAIN`, nearest `alice-toolchain` file, default. An `alice-toolchain`
file holds an exact version (`0.10.1`, `0.11.0-nightly.20261016`) or `stable`/`nightly`; anything else, a path
included, is an error, and only toolchains under `~/.alice/toolchains` run. `ALICE_HOME` moves
the root; `ALICE_RELEASES_REPO` points at a fork; `GITHUB_TOKEN` raises API limits and is sent only to
`api.github.com`. Archives are downloaded only over https from GitHub. Once a day a background
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

To build editor packages locally, clone the extensions repository into `extensions/`
beside `Alice.sln` if it is not already present. Requires Node.js 22+, Rust and Python 3:

```sh
git clone https://github.com/alicelanguage/extensions.git extensions
npm install
npm run package:vsix                # Host VS Code/VSCodium package
rustup target add wasm32-wasip2     # Once, before building Zed
npm run package:zed                 # Zed source/WASI archive
npm run package                    # Both packages
```

Artifacts are written to `extensions/dist/`. `npm run build` and `npm test` run the
.NET build and tests; `build:extensions`, `test:extensions` and `check:extensions`
operate on the adapters. The extensions checkout also supports its own root npm commands.
