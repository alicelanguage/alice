# Alice

**Ruby's rhythm. Crystal-style types. The .NET ecosystem.**

[![Tests](https://github.com/kleeedolinux/alice/actions/workflows/ci.yml/badge.svg)](https://github.com/kleeedolinux/alice/actions/workflows/ci.yml)
[![Release](https://github.com/kleeedolinux/alice/actions/workflows/release.yml/badge.svg)](https://github.com/kleeedolinux/alice/actions/workflows/release.yml)

Alice is a statically typed language for .NET. Write expressive `.al` programs with blocks, records and pattern matching; use .NET libraries and share ordinary CLR types with C#.

```crystal
record Item(name : String, cents : Int32, quantity : Int32)

items = [
  Item.new("Coffee", 450, 2),
  Item.new("Tea", 300, 1)
]

val total = items.map { |item| item.cents * item.quantity }.sum
puts "Total: #{total} cents"   # Total: 1200 cents
```

| What makes it Alice | What you get |
|---|---|
| Expressive syntax | Implicit returns, string interpolation, blocks, `&:method`, mixins and `require` |
| Types with less ceremony | Type inference, generics, records and sum types with exhaustive matching |
| Safety at compile time | Null-safety errors, immutable `val` bindings and data-race diagnostics (`AL0202`) |
| Optimizing compiler | Iterator fusion, SIMD, structure-of-arrays and automatic parallelization when proven pure |
| Concurrency | `spawn`, `async`/`await`, channels, actors, mutexes and atomics |
| Native to .NET | BCL and NuGet libraries, C# interop, ASP.NET Core hosts and MonoGame |
| Project tooling | `alice.toml`, workspaces, dependency locks, formatting and tests |

Alice lowers through MIR to C#, then uses Roslyn to emit .NET IL. The compiler and runtime are written in C#. Inspect the generated C# with `alice emit`.

## Start here

Requires the **.NET 10 SDK**.

```bash
git clone https://github.com/kleeedolinux/alice.git
cd alice
dotnet build Alice.sln --configuration Release
dotnet src/Alice.Cli/bin/Release/net10.0/alice.dll run examples/basics/hello.al
```

Run any CLI command from this checkout with `dotnet run --project src/Alice.Cli -- <command>`. With a [release archive](https://github.com/kleeedolinux/alice/releases), extract the entire archive and add its directory to `PATH` to use `alice` directly. See [installation and checksums](docs/releases.md).

```bash
alice new shop
cd shop
alice run
alice test
alice fmt --check
```

`alice.toml` is the source of truth. The CLI generates MSBuild projects under `.alice/`.

```toml
[package]
name = "shop"
kind = "exe"

[dependencies]
"Newtonsoft.Json" = "13.*"
```

## Learn by building

| Example | Explore |
|---|---|
| [Hello](examples/basics/hello.al) | Classes, interpolation, blocks and SIMD |
| [Practical examples](examples/README.md) | Data processing, JSON, concurrency and expected output |
| [ASP.NET Core API](examples/web/aspnet/README.md) | Alice HTTP routes, validation and pricing logic |
| [C# interop](samples/AliceApp/src/main.al) | Alice calling C#; [C# calling Alice](samples/CSharpApp/Program.cs) |
| [Breakout](samples/Breakout/README.md) | A complete MonoGame game with headless logic tests |

## Performance you can inspect

Alice optimizes eligible idiomatic code: fuse iterator chains, vectorize arithmetic and parallelize pure work. Results depend on the workload and hardware; parallel floating-point reductions can change addition order.

The recorded [C# comparison (run 5)](benchmarks/README.md#run-5--vs-expert-hand-optimized-c-fusion-simt-load-balancing-2026-10-16) includes idiomatic and hand-optimized baselines, checksums and methodology. For example, the pipeline measured **2.1 ms** in Alice, **31.3 ms** in idiomatic C# and **2.0 ms** in hand-optimized C# on the documented laptop. Reproduce with `benchmarks/run.sh`.

## Documentation

[Getting started](docs/getting-started.md) · [Language](docs/language.md) · [Tooling](docs/tooling.md) · [Error codes](docs/errors.md) · [Performance](docs/performance.md) · [Architecture](archicture/README.md)

## Development

**0.10.0 · in development.** See the [changelog](CHANGELOG.md) and [contributing guide](CONTRIBUTING.md).

```bash
dotnet build Alice.sln --configuration Release
dotnet test Alice.sln --configuration Release
bash examples/verify.sh
```

CI runs the test suite and example checks. The [release workflow](docs/releases.md) tests and packages platform binaries before publishing a tagged release.
