# Alice examples

Small programs you can run, inspect, and adapt. Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

Run these commands from the repository root:

```bash
dotnet run --project src/Alice.Cli -- run examples/data/invoices.al
dotnet run --project src/Alice.Cli -- emit examples/data/invoices.al
```

| Example | What it does |
|---|---|
| [basics/hello.al](basics/hello.al) | Classes, interpolation, SIMD dot product, parallel map |
| [data/invoices.al](data/invoices.al) | Filter paid invoices and sum revenue in integer cents |
| [data/json_report.al](data/json_report.al) | Serialize a record and read JSON with `System.Text.Json` |
| [data/efcore/](data/efcore) | Entity Framework Core + SQLite: a `DbContext` in Alice, LINQ translated to SQL |
| [data/trees.al](data/trees.al) | Build and fold a sum-type tree; both recursions fork automatically |
| [concurrency/channels.al](concurrency/channels.al) | Pass work results through a channel and aggregate safely |
| [dotnet/library_api.al](dotnet/library_api.al) | Iterators, events, delegates, default interface methods, method references, JSON attributes, `lock` |
| [interop/libc.al](interop/libc.al) | Call C directly: UTF-8 strings, string returns, `out` structs, an Alice comparator for `qsort` (Linux) |
| [ui/avalonia/](ui/avalonia) | A XAML window (Avalonia) with its code-behind written in Alice, run headless |
| [tools/wand/](tools/wand) | A `rails generate`-style CLI packaged as a .NET tool (`alice pack`, `dotnet tool install`) |
| [web/aspnet/](web/aspnet/README.md) | ASP.NET Core startup, routes, validation, and pricing written in Alice |

The standalone programs have no extra NuGet dependencies. The web example uses the ASP.NET Core shared framework included with the SDK.

Verify every output, the pricing tests, and live HTTP responses (Bash + curl):

```bash
bash examples/verify.sh
```

To reuse an already built CLI, set `ALICE_DLL` to its absolute `alice.dll` path. Builds for these examples use the repository's compiler and runtime.

For games and bidirectional C# interop, see [samples/](../samples/).
