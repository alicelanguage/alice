<p align="center">
  <img src="assets/brand/alice-logo-dark.svg" alt="Alice" width="560">
</p>

<p align="center"><b>Curiouser and faster.</b> Write it like Ruby, run it like C, on all of .NET.</p>

---

Alice is a programming language for .NET. You write Ruby/Crystal-style code (blocks, `case/in`, DSLs, `yield`,
`instance_eval`), the compiler infers static types, rejects data races, and lowers your loops into vectorized,
parallel, allocation-free C# — which Roslyn compiles to IL. Every .NET library and NuGet package works as is.

```ruby
record struct Vec(x : Float64, y : Float64)
  def +(o : Vec) : Vec = Vec.new(x + o.x, y + o.y)
end

total = (1..1_000_000).map { |i| i.to_i64 * i }.select { |x| x % 2 == 0 }.sum   # one fused, parallel loop
puts "total=#{total}"

describe "Vec" do
  it "adds" do
    expect(Vec.new(1.0, 2.0) + Vec.new(3.0, 4.0)).to eq(Vec.new(4.0, 6.0))
  end
end
```

## What it looks like

| [Celeste Classic (`samples/celeste`)](samples/celeste) | Ray tracer (`samples/raytracer`) |
|---|---|
| [![Celeste Classic gameplay in Alice](docs/media/celeste.gif)](samples/celeste) | ![raytracer](docs/media/raytracer-orbit.gif) |
| **[Breakout (`samples/Breakout`)](samples/Breakout)** | **Mandelbrot zoom (`samples/fractal`)** |
| [![Breakout gameplay in Alice with MonoGame](docs/media/breakout.gif)](samples/Breakout) | ![fractal](docs/media/fractal-zoom.gif) |
| **The REPL (`alice`)** | **[Playground](https://playground.alice-lang.org)** |
| ![repl](docs/media/repl.svg) | Edit and run Alice in your browser; inspect HIR, MIR, C# and IL at [playground.alice-lang.org](https://playground.alice-lang.org) |

All of these are 100% Alice. The ray tracer renders 960×540 with shadows, mirrors and 4× supersampling in ~0.25 s;
the 60-frame fractal zoom renders in ~1.5 s (i5-1235U).

Celeste Classic is an unofficial educational rewrite of the game by Maddy Thorson and Noel Berry;
see the sample's [run instructions and original credits](samples/celeste/README.md).

## Install and first steps

```bash
# Linux and macOS
curl -fsSL https://alice-lang.org/install.sh | sh
```

```powershell
# Windows (PowerShell; no administrator rights, no Visual Studio)
irm https://alice-lang.org/install.ps1 | iex
```

The installer picks the build for your platform from [Releases](https://github.com/alicelanguage/alice/releases),
verifies its SHA-256, installs the .NET 10 SDK per-user if it is missing, and puts `alice` on your PATH.
Options: `sh -s -- --nightly`, `--version 0.11.0`, `--no-modify-path`, `--no-dotnet` (PowerShell: `-Version`, `-NoDotnet`, …).

```bash
alice                      # REPL: highlighting, Tab completion, ↑ history search, ?help ;shell ]packages
alice run hello.al         # run a file
alice new app && cd app    # project (alice.toml)
alice add Newtonsoft.Json  # NuGet package (shared .NET cache)
alice test                 # specs and tests
alice doctor               # check .NET, PATH, toolchains
```

Several versions side by side, rustup-style:

```bash
alice toolchain install nightly     # stable, nightly or a version
alice toolchain default 0.11.0
alice toolchain pin 0.11.0          # per project (writes ./alice-toolchain)
alice +nightly run app.al           # one-off
alice self update                   # update; a notice appears when a new release is out
```

Details: [releases and installers](docs/releases.md), [toolchains](docs/tooling.md#toolchains).
Build from source instead:

```bash
git clone https://github.com/alicelanguage/alice && cd alice
dotnet build src/Alice.Cli -c Release
alias alice="dotnet $PWD/src/Alice.Cli/bin/Release/net10.0/alice.dll"
```

## Editor support

Alice 0.11.1 includes `alice lsp --stdio`. The [Rust editor extensions](https://github.com/alicelanguage/extensions)
add completion, diagnostics, .NET/NuGet assistance, automatic imports, hover,
formatting, highlighting and icons to VS Code, VSCodium and Zed. Install the
matching VSIX or use Zed's dev extension installer. See [tooling](docs/tooling.md#editor-support).

## How it works

```
.al source ─▶ Lexer/Parser ─▶ AST ─▶ Binder (types, races, purity) ─▶ HIR
          ─▶ Lowering: iterators → loops, fusion, SIMD, auto-parallel, SoA, FMA ─▶ MIR
          ─▶ C# (with #line → your .al lines) ─▶ Roslyn ─▶ IL  (+ optional C via clang for @[Native])
```

| Part | Where | What it does |
|---|---|---|
| Syntax | `src/Alice.Compiler/Syntax` | Ruby/Crystal grammar: blocks, `do…end`, heredocs, `%w`, `case/in` patterns, endless defs |
| Binder | `src/Alice.Compiler/Semantics` | type inference, generics, nil-safety, **data-race errors (AL0202)**, purity analysis |
| Lowering | `src/Alice.Compiler/Lowering` | `map/select/sum` chains → one loop; vectorization; parallel loops only when proven pure; SoA layout; FMA |
| Backend | `src/Alice.Compiler/Backend` | C# emitter with `#line`, Roslyn compile, error translation to Alice terms, C emitter for `@[Native]` |
| Runtime | `src/Alice.Runtime` | Ruby-style Prelude, SIMD, sparse matrices, `Par`, channels/actors, spec framework, REPL host |
| CLI | `src/Alice.Cli` | `alice` command, projects (`alice.toml`, workspaces, `alice.lock`), REPL, packages, localized output (en/pt/es) |

## What you can do

- **Everything .NET**: namespaces, generics, interfaces, events, `async/await`, `for await`, LINQ, spans, NuGet, ASP.NET,
  MonoGame, raylib. C# projects can reference Alice libraries and vice versa (`samples/CSharpApp`).
- **Ruby ergonomics**: blocks, `yield`, `instance_eval`/`instance_exec`, class-body macros, `define_method`,
  `method_missing`, `send`, `@@class_vars`, `attr_accessor`, monkey patching, `Comparable`/`Enumerable`, `case/in`.
- **Safety**: static types everywhere, nil-safety, immutable `val`, compile-time data-race detection.
- **Speed**: auto-vectorized and auto-parallel loops, fused pipelines, SoA records, FMA contraction, `@[Native]`
  functions compiled by clang. See [benchmarks](benchmarks/README.md): Alice beats idiomatic C# on all 8 and
  matches or beats C on the 5 C benchmarks.
- **Tooling**: REPL with help/shell/package modes, RSpec + xUnit style tests, formatter, readable errors
  (`AL0303 undefined name 'x'`, never raw C#), "did you mean" everywhere, NuGet via `add/remove/search/list`.

### Mutability

Local bindings are mutable by default; use `val` when a binding must not be reassigned. For fields, `getter` exposes
read-only access and `property` exposes read-write access. `val` prevents rebinding, but does not make a referenced
object deeply immutable.

```ruby
count = 1       # mutable binding
val limit = 10  # cannot be reassigned
```

## Samples

| Sample | Shows |
|---|---|
| [`samples/life`](samples/life) | Game of Life, terminal animation, frame export |
| [`samples/raytracer`](samples/raytracer) | operator overloading on value types, parallel rendering, image output |
| [`samples/fractal`](samples/fractal) | parallel Mandelbrot zoom video |
| [`samples/wonderland`](samples/wonderland) | text adventure written as a Ruby-style DSL |
| [`samples/celeste`](samples/celeste) | Celeste Classic rewritten in Alice (raylib), 28 tests |
| [`samples/Breakout`](samples/Breakout), [`BounceGame`](samples/BounceGame) | MonoGame games |
| [`samples/AliceLib`](samples/AliceLib) + [`CSharpApp`](samples/CSharpApp) | C# ⇄ Alice interop |
| [`examples/`](examples) | basics, data/JSON, channels, ASP.NET API |

## Docs

[Language](https://alice-lang.org/documentation) · [Tooling & REPL](https://alice-lang.org/documentation) · [Errors](https://alice-lang.org/documentation) ·
[Performance](https://alice-lang.org/documentation) · [Architecture](https://alice-lang.org/documentation) · [Changelog](https://alice-lang.org/documentation)

## Roadmap

- Incremental compilation and a language server (editor completion, go-to-definition).
- Broader race analysis (prove disjoint `y * w + x` writes so more loops parallelize safely).
- GPU offload for `Par` kernels; more `@[Native]` coverage (structs, strings).
- Refinements, `ObjectSpace`, `class_eval` with strings.
- NativeAOT single-file `alice` binary; package registry for Alice libraries.

## License

Code: [Apache License 2.0](LICENSE). The Alice name, rabbit logo and brand images are © 2026 Julia Klee, all
rights reserved — use them to refer to Alice, not for commercial exploitation ([details](assets/brand/LICENSE.md)).
