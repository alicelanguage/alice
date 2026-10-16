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
dotnet run --project src/Alice.Cli -- run hello.al
```

Highlights: sum types + exhaustive `case`, records, generics with constraints, `async`/`await`, compile-time data-race errors (AL0202), `@[SoA]` columnar stores. The compiler fuses, vectorizes (`Vector<T>`) and auto-parallelizes pure loops.

Benchmarks vs idiomatic C# on .NET 10 ([run 3](benchmarks/README.md)):

| benchmark | Alice ms | C# ms | ratio |
|---|---:|---:|---:|
| pipeline | 2.0 | 28.1 | 0.07 |
| particles | 25.3 | 159.1 | 0.16 |
| mandelbrot | 21.8 | 118.5 | 0.18 |
| wordfreq | 27.1 | 120.8 | 0.22 |
| binarytrees | 103.7 | 182.2 | 0.57 |
| nbody | 50.3 | 47.6 | 1.06 |

- [Getting started](docs/getting-started.md)
- [Language guide](docs/language.md)
- [Performance model](docs/performance.md)
- [Tooling](docs/tooling.md)
- [Error codes](docs/errors.md)
- [Benchmarks](benchmarks/README.md)
- [Architecture](archicture/README.md)
- [Changelog](CHANGELOG.md)

Version: 0.10.0.
