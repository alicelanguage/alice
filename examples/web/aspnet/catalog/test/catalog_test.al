using Microsoft.EntityFrameworkCore
using Store

# Each test gets its own in-memory database, named after the test.
def catalog(name : String) : Catalog
  options = DbContextOptionsBuilder(CatalogDb).new.UseInMemoryDatabase(name).Options
  Catalog.new(CatalogDb.new(options))
end

def test_seed_adds_two_products_once
  c = catalog("seed")
  c.seed
  c.seed
  assert_equal 2, c.all.size
  assert_equal "Assam tea", c.all[0].name
end

def test_add_trims_the_name_and_assigns_an_id
  c = catalog("add")
  case c.add(NewProduct.new("  Oolong  ", 900))
  when Outcome::Ok(product)
    assert_equal "Oolong", product.name
    assert product.id > 0
  else assert false, "expected the product to be added"
  end
end

def test_add_rejects_bad_input
  c = catalog("reject")
  assert_equal Outcome::Invalid("name is required"), c.add(NewProduct.new(nil, 100))
  assert_equal Outcome::Invalid("name is required"), c.add(NewProduct.new("   ", 100))
  assert_equal Outcome::Invalid("priceCents must be 0..1000000"), c.add(NewProduct.new("Gold tea", 1000001))
  assert_equal Outcome::Invalid("name is longer than 60 characters"), c.add(NewProduct.new("x" * 61, 1))
  assert_equal 0, c.all.size
end

def test_find_reports_missing_products
  c = catalog("find")
  assert_equal Outcome::NotFound, c.find(42)
end

def test_quote_uses_the_pricing_rules
  c = catalog("quote")
  c.seed
  sencha = c.all[1].id
  assert_equal QuoteOutcome::Priced(Quote.new(12500, 1250, 11250)), c.quote(OrderLine.new(sencha, 10))
  assert_equal QuoteOutcome::Unknown, c.quote(OrderLine.new(999, 1))
  assert_equal QuoteOutcome::Rejected("quantity must be 1..1000"), c.quote(OrderLine.new(sencha, 0))
end
