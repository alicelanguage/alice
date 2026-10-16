using Microsoft.AspNetCore.Builder
using Microsoft.AspNetCore.Http
using Store

record Health(status : String)
record ErrorResponse(error : String)

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
end

builder = WebApplication.CreateBuilder(ARGV)
app = builder.Build()
app.MapGet("/health", -> { Routes.health })
app.MapGet("/quote", ->(context : HttpContext) { Routes.quote(context) })
app.Run()
