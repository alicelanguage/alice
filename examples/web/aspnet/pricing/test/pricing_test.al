using Store

def test_standard_quote
  quote = Pricing.quote(1250, 4)
  assert_equal 5000, quote.subtotalCents
  assert_equal 0, quote.discountCents
  assert_equal 5000, quote.totalCents
end

def test_bulk_discount_starts_at_ten_items
  assert_equal 0, Pricing.quote(1250, 9).discountCents
  quote = Pricing.quote(1250, 10)
  assert_equal 12500, quote.subtotalCents
  assert_equal 1250, quote.discountCents
  assert_equal 11250, quote.totalCents
end

def test_discount_rounds_down_to_a_whole_cent
  quote = Pricing.quote(99, 11)
  assert_equal 108, quote.discountCents
  assert_equal 981, quote.totalCents
end
