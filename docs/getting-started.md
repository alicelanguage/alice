# Getting started

Install the **.NET 10 SDK**. Use a [release archive](releases.md#install-a-release) or build from source:

```bash
git clone https://github.com/alicelanguage/alice.git
cd alice
dotnet build Alice.sln --configuration Release
```

From the repository root, run the checked-in example:

```bash
dotnet run --project src/Alice.Cli -- run examples/basics/hello.al
dotnet run --project src/Alice.Cli -- emit examples/basics/hello.al
```

The first command runs Alice; the second shows the lowered C#. For your own single-file program, save this as `hello.al` and pass its path to `run`:

```crystal
def greet(name : String) : String
  "Hello, #{name}!"
end

puts greet("Alice")
```

## Create a project

With an extracted release directory on `PATH`:

```bash
alice new shop
cd shop
alice run
alice test
alice fmt --check
```

When working from source, replace `alice` with `dotnet /absolute/path/to/alice/src/Alice.Cli/bin/Release/net10.0/alice.dll`.

Edit `src/main.al` for application code and `test/*.al` for tests. Keep dependencies in `alice.toml` and commit `alice.lock`; generated `.alice/` projects are build artifacts.

Next: [practical examples](../examples/README.md), [ASP.NET Core](../examples/web/aspnet/README.md), [language guide](language.md), [packages and workspaces](tooling.md).
