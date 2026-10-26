<p align="center">
  <img src="assets/brand/alice-logo-dark.svg" alt="Alice" width="560">
</p>

<p align="center"><b>Curiouser and faster.</b><br>Write it like Ruby. Run it like C. Ship it on all of .NET.</p>

<p align="center">
  <a href="https://alice-lang.org">Website</a> ·
  <a href="https://playground.alice-lang.org">Playground</a> ·
  <a href="https://alice-lang.org/documentation">Docs</a> ·
  <a href="https://github.com/alicelanguage/alice/releases">Releases</a>
</p>

---

Alice is a programming language for .NET. You write blocks, `case/in` and DSLs; the compiler infers static
types, rejects data races, and turns your loops into vectorized, parallel, allocation-free code. Every .NET
library and NuGet package works as is.

```ruby
record struct Vec(x : Float64, y : Float64)
  def +(o : Vec) : Vec = Vec.new(x + o.x, y + o.y)
end

total = (1..1_000_000).map { |i| i.to_i64 * i }.select { |x| x % 2 == 0 }.sum   # one fused, parallel loop
puts "total=#{total}"
```

## Why Alice

**1. It runs fast.** `map/select/sum` chains fuse into one loop, loops vectorize, and proven-pure work spreads
across cores. Arrays the compiler owns go back to a pool instead of the GC. Alice beats idiomatic C# on 6 of 7
benchmarks in the repo suite and matches C on the 5 C benchmarks ([benchmarks](benchmarks/README.md)).

| benchmark | Alice | idiomatic C# | hand-optimized C# |
|---|---:|---:|---:|
| pipeline | 2.0 ms | 37.7 ms | 2.1 ms |
| mandelbrot | 9.7 ms | 104.5 ms | 8.7 ms |
| wordfreq | 6.9 ms | 80.5 ms | 5.3 ms |
| nbody | 47.8 ms | 53.3 ms | 48.9 ms |

**2. It's parallel, without threads.** No locks or thread pools to manage. Independent recursive calls fork,
independent loop iterations run in chunks, `gpu for` compiles to ILGPU kernels. The compiler parallelizes
only code it has proven pure.

```ruby
a, b = sum_tree(n - 1), sum_tree(n - 1)   # forks near the root, plain calls deeper down

par for y in 0...h                         # parallel, or compile error AL0209
  for x in 0...w
    img[y * w + x] = shade(x, y)
  end
end
```

**3. It blocks mistakes.** Nil-safety, immutable `val`, and **data races are compile errors** (AL0202).
When you want a guarantee, state it and the compiler proves it or stops: `pure def`, `safe def`, `own`,
`borrow`. Write the properties your code must never break in `RULES.al` and `alice prove` checks every case,
or the whole Int32/Int64 range with `--smt`:

```ruby
rule clamp_stays_in_range(x in -1000..1000, lo in -50..50)
  c = clamp(x, lo, lo + 10)
  c >= lo && c <= lo + 10
end
```

`alice prove --check` fails CI when a rule breaks and prints the counterexample.

**4. It's all of .NET.** Generics, interfaces, `async/await`, LINQ, spans, ASP.NET, MonoGame, raylib, EF Core,
Avalonia, WebAssembly. C# projects reference Alice libraries and the other way round. Alice compiles to C#,
then Roslyn compiles that to IL; errors still point at your `.al` lines.

**5. It's built for agents too.** `alice guide` ships the language reference inside the CLI, and
`alice agents --write` adds Alice rules to your `AGENTS.md`, so an AI agent checks and proves its changes.

## Made with Alice

| [Celeste Classic](samples/celeste) | [Ray tracer](samples/raytracer) |
|---|---|
| [![Celeste Classic in Alice](docs/media/celeste.gif)](samples/celeste) | [![ray tracer](docs/media/raytracer-orbit.gif)](samples/raytracer) |
| **[Breakout](samples/Breakout)** | **[Mandelbrot zoom](samples/fractal)** |
| [![Breakout in Alice](docs/media/breakout.gif)](samples/Breakout) | [![fractal](docs/media/fractal-zoom.gif)](samples/fractal) |

All 100% Alice. Celeste Classic is an unofficial educational rewrite of the game by Maddy Thorson and Noel
Berry ([credits](samples/celeste/README.md)). More in [`samples/`](samples) and [`examples/`](examples).

## Get started

```bash
curl -fsSL https://alice-lang.org/install.sh | sh     # Linux, macOS
irm https://alice-lang.org/install.ps1 | iex          # Windows PowerShell
```

The installer verifies its SHA-256 and fetches the .NET 10 SDK if you need it. Then:

```bash
alice                      # REPL
alice new app && cd app    # project (alice.toml)
alice run                  # run it
alice add Newtonsoft.Json  # NuGet package
alice test                 # specs and tests
alice agents --write       # teach your AI agent Alice
```

Or try it with no install in the [playground](https://playground.alice-lang.org). Editor support:
`alice lsp --stdio` and the [VS Code / Zed extensions](https://github.com/alicelanguage/extensions).
Toolchains, nightly and building from source: [docs/tooling.md](docs/tooling.md), [docs/releases.md](docs/releases.md).

## Learn more

[Language](https://alice-lang.org/documentation) ·
[Performance](https://alice-lang.org/documentation/guides/max-performance/) ·
[Rules and proofs](https://alice-lang.org/documentation/guides/rules-and-proofs/) ·
[Production readiness](docs/production.md) ·
[Architecture](archicture/) ·
[Changelog](CHANGELOG.md)

Alice is young. It is tested heavily, but expect rough edges, and please report bugs.

## License

Code: [Apache License 2.0](LICENSE). The Alice name, rabbit logo and brand images are © 2026 Julia Klee, all
rights reserved: use them to refer to Alice, not for commercial exploitation ([details](assets/brand/LICENSE.md)).
