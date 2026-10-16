# Alice

A .NET language with Crystal/Ruby syntax. Ergonomic like Ruby, safe like Rust, fast like C, parallel like Elixir. Full C#/.NET interop. Compiles to IL via Roslyn; NativeAOT optional. Self-hosting.

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

- [Getting started](docs/getting-started.md)
- [Language guide](docs/language.md)
- [Architecture](archicture/README.md)
- [Changelog](CHANGELOG.md)

Version: 0.10.0.
