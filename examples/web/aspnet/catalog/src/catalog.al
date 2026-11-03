# The product catalog: an EF Core DbContext and the rules for adding products and quoting orders.
# Nothing here knows about HTTP, so `alice test` checks it against an in-memory database.
using Microsoft.EntityFrameworkCore

namespace Store
  class Product
    property id : Int32 = 0
    property name : String = ""
    property priceCents : Int32 = 0
  end

  # JSON bodies: `{"name":"Tea","priceCents":450}` and `{"productId":1,"quantity":10}`
  record NewProduct(name : String?, priceCents : Int32)
  record OrderLine(productId : Int32, quantity : Int32)

  enum Outcome
    Ok(product : Product)
    Invalid(reason : String)
    NotFound
  end

  enum QuoteOutcome
    Priced(quote : Quote)
    Rejected(reason : String)
    Unknown
  end

  class CatalogDb < DbContext
    property products : DbSet(Product)               # EF fills DbSet properties

    def initialize(options : DbContextOptions(CatalogDb))
      super(options)
    end
  end

  # Registered as a scoped service: one per request, sharing that request's CatalogDb.
  class Catalog
    MAX_NAME = 60
    MAX_PRICE_CENTS = 1000000

    def initialize(@db : CatalogDb)
    end

    def all : Array(Product)
      @db.products.OrderBy(->(p : Product) { p.id }).ToArray()
    end

    def find(id : Int32) : Outcome
      product = @db.products.Find(id)
      product.nil? ? Outcome::NotFound : Outcome::Ok(product.not_nil!)
    end

    def self.check(input : NewProduct) : String?
      name = input.name
      return "name is required" if name.nil? || name.strip.size == 0
      return "name is longer than #{MAX_NAME} characters" if name.not_nil!.size > MAX_NAME
      return "priceCents must be 0..#{MAX_PRICE_CENTS}" if input.priceCents < 0 || input.priceCents > MAX_PRICE_CENTS
      nil
    end

    def add(input : NewProduct) : Outcome
      problem = Catalog.check(input)
      return Outcome::Invalid(problem.not_nil!) if !problem.nil?
      product = Product.new
      product.name = input.name.not_nil!.strip
      product.priceCents = input.priceCents
      @db.products.Add(product)
      @db.SaveChanges()
      Outcome::Ok(product)
    end

    def quote(line : OrderLine) : QuoteOutcome
      return QuoteOutcome::Rejected("quantity must be 1..1000") if line.quantity < 1 || line.quantity > 1000
      product = @db.products.Find(line.productId)
      return QuoteOutcome::Unknown if product.nil?
      QuoteOutcome::Priced(Pricing.quote(product.not_nil!.priceCents, line.quantity))
    end

    # Fills an empty catalog with two products (the example's starting data).
    def seed
      if !@db.products.Any()
        add(NewProduct.new("Assam tea", 450))
        add(NewProduct.new("Sencha tea", 1250))
      end
    end
  end
end
