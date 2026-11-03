using Microsoft.AspNetCore.Builder
using Microsoft.AspNetCore.Http
using Microsoft.EntityFrameworkCore
using Microsoft.Extensions.DependencyInjection
using Store

record Health(status : String)
record ErrorResponse(error : String)

# Outcome and QuoteOutcome come from the Catalog assembly. Destructuring a variant of a sum type declared in
# another Alice assembly (`when Outcome::Ok(product)`) does not compile yet, so these routes match the variant
# and read its field through `as`.
module Routes
  def self.health : IResult
    Results.Ok(Health.new("ok"))
  end

  def self.quote(context : HttpContext) : IResult
    unit_price_cents = 0
    quantity = 0
    price_text = context.Request.Query["unitPriceCents"].ToString()
    quantity_text = context.Request.Query["quantity"].ToString()
    if !Int32.TryParse(price_text, out unit_price_cents) || !Int32.TryParse(quantity_text, out quantity)
      return Results.BadRequest(ErrorResponse.new("Provide integer unitPriceCents and quantity query parameters."))
    end
    # Bound input before Int32 arithmetic. Amounts are integer cents.
    if unit_price_cents < 0 || unit_price_cents > 1000000 || quantity < 1 || quantity > 1000
      return Results.BadRequest(ErrorResponse.new("Use price 0..1000000 cents and quantity 1..1000."))
    end
    Results.Ok(Pricing.quote(unit_price_cents, quantity))
  end

  # GET /products: every product, by id
  def self.products(catalog : Catalog) : IResult
    Results.Ok(catalog.all)
  end

  # GET /products/{id}: 200 with the product, or 404
  def self.product(id : Int32, catalog : Catalog) : IResult
    found = catalog.find(id)
    case found
    when Outcome::Ok then Results.Ok(found.as(Outcome::Ok).product)
    else Results.NotFound(ErrorResponse.new("no product #{id}"))
    end
  end

  # POST /products with a JSON body: 201 with a Location header, or 400
  def self.add(input : NewProduct, catalog : Catalog) : IResult
    added = catalog.add(input)
    case added
    when Outcome::Ok
      product = added.as(Outcome::Ok).product
      Results.Created("/products/#{product.id}", product)
    when Outcome::Invalid then Results.BadRequest(ErrorResponse.new(added.as(Outcome::Invalid).reason))
    else Results.StatusCode(500)
    end
  end

  # POST /orders/quote with {"productId":1,"quantity":10}
  def self.order_quote(line : OrderLine, catalog : Catalog) : IResult
    priced = catalog.quote(line)
    case priced
    when QuoteOutcome::Priced then Results.Ok(priced.as(QuoteOutcome::Priced).quote)
    when QuoteOutcome::Rejected then Results.BadRequest(ErrorResponse.new(priced.as(QuoteOutcome::Rejected).reason))
    else Results.NotFound(ErrorResponse.new("no product #{line.productId}"))
    end
  end
end

builder = WebApplication.CreateBuilder(ARGV)
# Dependency injection: one CatalogDb (EF Core, in memory) and one Catalog per request.
builder.Services.AddDbContext<CatalogDb>(->(options : DbContextOptionsBuilder) { options.UseInMemoryDatabase("catalog") })
builder.Services.AddScoped<Catalog>()
app = builder.Build()

using scope = app.Services.CreateScope()
  scope.ServiceProvider.GetRequiredService<Catalog>().seed
end

app.UseWebSockets()
app.MapGet("/health", -> { Routes.health })
app.MapGet("/quote", ->(context : HttpContext) { Routes.quote(context) })
# Handlers with route, body or service parameters are method groups (`->Routes.product`): ASP.NET binds
# parameters by name, and a method keeps its parameter names (a lambda's are renamed in the generated C#).
app.MapGet("/products", ->Routes.products)
app.MapGet("/products/{id}", ->Routes.product)
app.MapPost("/products", ->Routes.add)
app.MapPost("/orders/quote", ->Routes.order_quote)
app.Map("/ws", ->(context : HttpContext) { Echo.serve(context) })
app.Run()
