# Getting started

Requires .NET 10 SDK. No native toolchain.

```bash
dotnet build Alice.sln
dotnet test
dotnet run --project src/Alice.Cli -- run hello.al
dotnet run --project src/Alice.Cli -- emit hello.al            # show lowered C#
dotnet run --project src/Alice.Cli -- build hello.al --native  # NativeAOT
```

`hello.al`:

```crystal
puts "Hello, #{"Alice"}!"
```
