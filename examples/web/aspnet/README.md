# ASP.NET Core + Alice

A small store API written in Alice, on ASP.NET Core minimal APIs: routing, JSON in and out, validation,
dependency injection, EF Core (in-memory database), a WebSocket echo, and tests that start the real application
with `WebApplicationFactory`. Alice calls ASP.NET Core directly through .NET interop; there is no wrapper.

| Package | Role |
|---|---|
| [pricing/](pricing/src/pricing.al) | `Pricing.quote`: integer cents, a 10% bulk discount from 10 items (library, `alice test`) |
| [catalog/](catalog/src/catalog.al) | `Product`, `CatalogDb` (an EF Core `DbContext`), and `Catalog`: add, find, quote rules (library, `alice test` on an in-memory database) |
| [api/](api/src/main.al) | The web host: DI registrations, routes, [api/src/echo.al](api/src/echo.al) WebSocket echo |
| [apitests/](apitests/src/checks.al) | 14 HTTP and WebSocket checks against the running app |
| [alice.toml](alice.toml) | The workspace: `alice build` here builds all four, dependencies first |

`api/Directory.Build.props` and `apitests/Directory.Build.props` add the ASP.NET Core shared framework
(`<FrameworkReference Include="Microsoft.AspNetCore.App" />`).

## Endpoints

| Request | Response |
|---|---|
| `GET /health` | `200 {"status":"ok"}` |
| `GET /quote?unitPriceCents=1250&quantity=10` | `200 {"subtotalCents":12500,"discountCents":1250,"totalCents":11250}`; `400` for missing, malformed or out-of-range input |
| `GET /products` | `200 [{"id":1,"name":"Assam tea","priceCents":450},{"id":2,"name":"Sencha tea","priceCents":1250}]` (seeded at startup) |
| `GET /products/2` | `200 {"id":2,"name":"Sencha tea","priceCents":1250}`; `404 {"error":"no product 99"}` |
| `POST /products` `{"name":"Oolong","priceCents":900}` | `201`, `Location: /products/3`, `{"id":3,"name":"Oolong","priceCents":900}`; `400 {"error":"name is required"}` |
| `POST /orders/quote` `{"productId":2,"quantity":10}` | `200` with the quote; `404` for an unknown product; `400` for quantity outside 1..1000 or malformed JSON |
| `GET /ws` (WebSocket) | sends every text message back; plain HTTP gets `400` |

Dependency injection: `AddDbContext<CatalogDb>` with `UseInMemoryDatabase("catalog")` and `AddScoped<Catalog>()`.
A handler that declares a `Catalog` parameter gets the request's instance.

## Run

From the repository root, with the .NET 10 SDK (it includes ASP.NET Core):

```bash
dotnet run --project src/Alice.Cli -- build examples/web/aspnet
dotnet run --project src/Alice.Cli -- run examples/web/aspnet/api -- --urls http://127.0.0.1:5080
```

In another terminal:

```bash
curl http://127.0.0.1:5080/products/2
# {"id":2,"name":"Sencha tea","priceCents":1250}

curl -i -X POST -H 'Content-Type: application/json' -d '{"name":"Oolong","priceCents":900}' http://127.0.0.1:5080/products
# HTTP/1.1 201 Created
# Location: /products/3
# {"id":3,"name":"Oolong","priceCents":900}

curl -X POST -H 'Content-Type: application/json' -d '{"productId":2,"quantity":10}' http://127.0.0.1:5080/orders/quote
# {"subtotalCents":12500,"discountCents":1250,"totalCents":11250}
```

Prices are integer cents. The query-string quote accepts prices from 0 to 1,000,000 cents and quantities from 1 to
1,000. Orders of 10 or more items get 10% off, rounded down to a whole cent.

## Test

```bash
cd examples/web/aspnet
alice build
dotnet apitests/bin/Debug/net10.0/ApiTests.dll
(cd pricing && alice test)
(cd catalog && alice test)
```

`ApiTests` prints one `PASS` line per check and ends with `14 passed, 0 failed` (exit code 1 if any fails).
`WebApplicationFactory(Health)` runs `api`'s own startup on an in-memory `TestServer`; `Health` is just a type
from the `Api` assembly, which the factory uses to find its entry point. From the repository root,
`bash examples/verify.sh` also starts the real server on a free port and checks the responses with curl.

## How the code is laid out

- Rules that do not need HTTP live in libraries (`pricing`, `catalog`), so `alice test` checks them fast.
- Route handlers parse input, call a rule, and pick the HTTP result.
- Handlers that take route values, a JSON body or services are method groups (`app.MapGet("/products/{id}", ->Routes.product)`):
  ASP.NET binds parameters by name, and a method keeps its parameter names.

## Known gaps (worked around here)

These are compiler or tool limits found while writing this example. Each one has a workaround in the code:

- **A lambda's parameters are renamed in the generated C#** (`id` becomes `id_5`). ASP.NET binds by name, so
  `->(id : Int32, c : Catalog) { ... }` on `/products/{id}` gives `400`. Use a method group, as above.
- **Destructuring a variant of a sum type declared in another Alice assembly does not compile**
  (`when Outcome::Ok(product)` gives `AL0303 undefined name 'product'`). The routes match the variant and read
  its field with `as`: `when Outcome::Ok then found.as(Outcome::Ok).product`.
- **`alice test` cannot run code that needs ASP.NET Core**: it loads tests into the compiler's own process,
  which has only the base framework. The API checks are therefore a program (`apitests`), not `alice test` specs.
- **`factory.Server` (the `TestServer`) and `factory.UseKestrel()` crash the compiler** (it reflects over ASP.NET
  reference assemblies). The WebSocket check starts `Api.dll` as a real Kestrel process instead.
- **The result of `await context.WebSockets.AcceptWebSocketAsync()` was typed `dynamic`**, which turned
  `received.Count` into a method call that fails at run time. The echo declares `socket : WebSocket`.
- **Every Alice assembly emits a `Program` class**, so a package that depends on another sees two (`CS0436`).
  `catalog`, `api` and `apitests` turn that warning off in their `Directory.Build.props`.

## Moving it out of this checkout

Edit `alice.toml` and `.al` files; the `.alice/` projects are generated. The example uses the repository's Alice
SDK settings in [../../Directory.Build.props](../../Directory.Build.props) and
[../../Directory.Build.targets](../../Directory.Build.targets): import equivalent settings, and keep the
framework references, when you copy it elsewhere.
