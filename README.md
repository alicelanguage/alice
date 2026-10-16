# Alice

A .NET language with Crystal/Ruby syntax. Ergonomic like Ruby, safe like Rust, fast like C, parallel like Elixir. Full C#/.NET interop. Pure C# compiler emits optimized IL via Roslyn; NativeAOT optional.

```crystal
class Greeter
  def initialize(@name : String)
  end

  def greet : String
    "Hello, #{@name}!"
  end
end

[1, 2, 3].each { |i| puts Greeter.new("Alice #{i}").greet }
```

```bash
dotnet run --project src/Alice.Cli -- run hello.al     # single file (follows require)
alice new shop --workspace && cd shop
alice new core --lib && alice new app                  # auto-registered members
alice run app                                          # builds deps first, writes alice.lock
cd app && alice add Newtonsoft.Json --version 13.*     # edits app/alice.toml
alice test && alice fmt --check
```

```toml
# app/alice.toml
[package]
name = "app"
kind = "exe"          # exe | lib | game

[dependencies]
core = { workspace = true }
"Newtonsoft.Json" = "13.*"
```

Highlights:

- Ruby syntax: blocks, `&:sym`, heredocs, `%w[]`, regex literals, endless `def`, `attr_accessor`, `include`/`extend` mixins, `require`.
- Crystal types: inference, sum types + exhaustive `case` (AL0201), records, generics with constraints.
- Safety: null-safety errors, `val`, compile-time data-race errors (AL0202).
- Speed: fused, vectorized (`Vector<T>`), SIMT and auto-parallel loops; `@[SoA]` stores with pass fusion; NativeAOT.
- Tooling: cargo-style `alice.toml`, workspaces, `alice.lock`, `fmt`, `test`; two-way C# interop, MonoGame games ([Breakout](samples/Breakout/README.md)).

Benchmarks on .NET 10 ([run 5](benchmarks/README.md)): Alice beats idiomatic C# on every benchmark and is within ~10% of expert hand-optimized C#.

| benchmark | Alice ms | C# idiomatic ms | C# hand-opt ms |
|---|---:|---:|---:|
| particles | 2.3 | 139.2 | 2.3 |
| pipeline | 2.1 | 31.3 | 2.0 |
| wordfreq | 5.4 | 89.4 | 6.7 |
| mandelbrot | 11.0 | 120.8 | 10.4 |
| expr | 11.2 | 11.4 | 10.2 |
| parallel | 39.9 | 45.1 | 39.2 |
| nbody | 55.5 | 61.4 | 52.5 |
| binarytrees | 93.3 | 166.0 | 95.3 |

- [Getting started](docs/getting-started.md)
- [Language guide](docs/language.md)
- [Performance model](docs/performance.md)
- [Tooling](docs/tooling.md)
- [Error codes](docs/errors.md)
- [Benchmarks](benchmarks/README.md)
- [Architecture](archicture/README.md)
- [Changelog](CHANGELOG.md)

Version: 0.10.0.
