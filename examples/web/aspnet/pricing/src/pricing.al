namespace Store
  record Quote(subtotalCents : Int32, discountCents : Int32, totalCents : Int32)

  module Pricing
    # Caller supplies a nonnegative price and a positive, bounded quantity.
    # Whole-cent arithmetic: a 10% bulk discount rounds down.
    def self.quote(unit_price_cents : Int32, quantity : Int32) : Quote
      subtotal = unit_price_cents * quantity
      discount = quantity >= 10 ? subtotal / 10 : 0
      Quote.new(subtotal, discount, subtotal - discount)
    end
  end
end
