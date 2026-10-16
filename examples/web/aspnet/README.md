# ASP.NET Core + Alice

A quote API written in Alice: application startup, HTTP routes, validation, JSON responses, and pricing rules. It calls ASP.NET Core directly through .NET interop.

| File | Role |
|---|---|
| [alice.toml](alice.toml) | Workspace containing the API and pricing library |
| [api/src/main.al](api/src/main.al) | Start the server, map endpoints, validate requests |
| [api/alice.toml](api/alice.toml) | Executable package with a workspace dependency on Pricing |
| [api/Directory.Build.props](api/Directory.Build.props) | Reference the ASP.NET Core shared framework |
| [pricing/src/pricing.al](pricing/src/pricing.al) | Quote record and a 10% bulk discount |
| [pricing/test/pricing_test.al](pricing/test/pricing_test.al) | Pricing, discount threshold, and cent rounding tests |

From the repository root, with .NET 10 SDK:

```bash
dotnet run --project src/Alice.Cli -- build examples/web/aspnet
dotnet run --project src/Alice.Cli -- run examples/web/aspnet/api -- --urls http://127.0.0.1:5080
```

In another terminal:

```bash
curl http://127.0.0.1:5080/health
# {"status":"ok"}

curl 'http://127.0.0.1:5080/quote?unitPriceCents=1250&quantity=10'
# {"subtotalCents":12500,"discountCents":1250,"totalCents":11250}
```

Prices use integer cents. The API accepts prices from 0 to 1,000,000 cents and quantities from 1 to 1,000; missing, malformed, and out-of-range inputs return HTTP 400. Orders of 10 or more items receive 10% off, rounded down to a whole cent.

Run Alice's pricing tests:

```bash
cd examples/web/aspnet/pricing
dotnet run --project ../../../../src/Alice.Cli -- test
```

From the repository root, `bash examples/verify.sh` also builds the host and checks its live HTTP responses on an automatically assigned local port.

Edit `alice.toml` and `.al` files; `.alice/` projects are generated. The example uses the repository's Alice SDK settings in [../../Directory.Build.props](../../Directory.Build.props) and [../../Directory.Build.targets](../../Directory.Build.targets). Import equivalent settings when moving it outside this checkout.
