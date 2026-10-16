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
| [concurrency/channels.al](concurrency/channels.al) | Pass work results through a channel and aggregate safely |
| [web/aspnet/](web/aspnet/README.md) | ASP.NET Core startup, routes, validation, and pricing written in Alice |

The standalone programs have no extra NuGet dependencies. The web example uses the ASP.NET Core shared framework included with the SDK.

Verify every output, the pricing tests, and live HTTP responses (Bash + curl):

```bash
bash examples/verify.sh
```

To reuse an already built CLI, set `ALICE_DLL` to its absolute `alice.dll` path. Builds for these examples use the repository's compiler and runtime.

For games and bidirectional C# interop, see [samples/](../samples/).
