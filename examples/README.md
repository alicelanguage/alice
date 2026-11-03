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
| [data/pipeline/](data/pipeline/README.md) | CSV to typed records to totals per region and month to JSON, in integer cents |
| [data/trees.al](data/trees.al) | Build and fold a sum-type tree; both recursions fork automatically |
| [concurrency/channels.al](concurrency/channels.al) | Pass work results through a channel and aggregate safely |
| [dotnet/library_api.al](dotnet/library_api.al) | Iterators, events, delegates, default interface methods, method references, JSON attributes, `lock` |
| [interop/libc.al](interop/libc.al) | Call C directly: UTF-8 strings, string returns, `out` structs, an Alice comparator for `qsort` (Linux) |
| [gpu/ilgpu/](gpu/ilgpu) | A GPU kernel written in Alice, compiled by ILGPU for CUDA, OpenCL or the CPU |
| [ui/avalonia/](ui/avalonia/README.md) | An Avalonia app: XAML views, MVVM view models and commands, two views, JSON settings, run headless |
| [tools/wand/](tools/wand) | A `rails generate`-style CLI packaged as a .NET tool (`alice pack`, `dotnet tool install`) |
| [web/aspnet/](web/aspnet/README.md) | ASP.NET Core minimal API: routes, JSON bodies, validation, DI, EF Core in memory, WebSocket echo, `WebApplicationFactory` checks |

The standalone `.al` programs have no NuGet dependencies. The web example uses the ASP.NET Core shared framework included with the SDK, plus EF Core InMemory and Mvc.Testing from NuGet; the package examples restore their NuGet packages on first build.

Verify every output, the specs of each package, the domain samples in [`samples/`](../samples/), and live HTTP responses (Bash + curl):

```bash
bash examples/verify.sh
```

To reuse an already built CLI, set `ALICE_DLL` to its absolute `alice.dll` path. Builds for these examples use the repository's compiler and runtime.

For games (MonoGame, raylib), a TCP chat, a grep CLI, low-level code and bidirectional C# interop, see [samples/](../samples/).
