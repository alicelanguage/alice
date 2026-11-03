# CSV rows -> typed sales -> totals per region and per month -> JSON (System.Text.Json).
using System.Text.Json
using System.Text.Json.Serialization

record Sale(date : String, region : String, product : String, quantity : Int32, unitCents : Int64)

record RegionTotal(region : String, orders : Int32, items : Int32, revenueCents : Int64, averageOrderCents : Int64)
record MonthTotal(month : String, revenueCents : Int64)
record Report(source : String, orders : Int32, revenueCents : Int64, topProduct : String, regions : Array(RegionTotal), months : Array(MonthTotal), rejected : Array(String))

module Money
  # "4.50" -> 450, "12" -> 1200, "0.5" -> 50. Amounts are whole cents in Int64; no floating point.
  def self.parse_cents(text : String) : Int64?
    parts = text.strip.split(".")
    return nil if parts.size > 2 || parts[0].size == 0 || !parts[0].chars.all? { |c| c >= '0' && c <= '9' }
    cents = parts.size == 2 ? parts[1] : "0"
    return nil if cents.size == 0 || cents.size > 2 || !cents.chars.all? { |c| c >= '0' && c <= '9' }
    Int64.Parse(parts[0]) * 100 + Int64.Parse(cents.ljust(2, "0"))
  end

  def self.format(cents : Int64) : String
    "#{cents / 100}.#{(cents % 100).ToString().rjust(2, "0")}"
  end
end

module Pipeline
  HEADER : Array(String) = ["date", "region", "product", "quantity", "unit_price"]

  # A row that does not fit is kept out of the totals and reported with its line number.
  def self.parse(records : Array(Array(String)), rejected : List(String)) : Array(Sale)
    raise ArgumentException.new("expected the header #{HEADER.join(",")}") if records.size == 0 || records[0].join(",") != HEADER.join(",")
    sales = List(Sale).new
    for n in 1...records.size
      r = records[n]
      quantity = 0
      cents = r.size == 5 ? Money.parse_cents(r[4]) : nil
      if r.size != 5 || r[1].strip.size == 0 || !Int32.TryParse(r[3], out quantity) || quantity <= 0 || cents.nil? || r[0].size != 10
        rejected.Add("record #{n + 1}: #{r.join(",")}")
      else
        sales.Add(Sale.new(r[0], r[1].strip, r[2].strip, quantity, cents || 0_i64))   # not nil here: checked above
      end
    end
    sales.to_a
  end

  def self.revenue(s : Sale) : Int64
    s.unitCents * s.quantity
  end

  def self.report(source : String, sales : Array(Sale), rejected : Array(String)) : Report
    regions = sales.group_by { |s| s.region }.map do |region, rows|
      total = rows.map { |s| Pipeline.revenue(s) }.sum
      RegionTotal.new(region, rows.size, rows.sum { |s| s.quantity }, total, total / rows.size)
    end
    months = sales.group_by { |s| s.date[0, 7] }.map { |month, rows| MonthTotal.new(month, rows.map { |s| Pipeline.revenue(s) }.sum) }
    by_product = sales.group_by { |s| s.product }.map { |product, rows| {product, rows.map { |s| Pipeline.revenue(s) }.sum} }
    top = by_product.size == 0 ? "" : by_product.sort_by { |p| {-p[1], p[0]} }.first[0]
    Report.new(source, sales.size, sales.map { |s| Pipeline.revenue(s) }.sum, top,
      regions.sort_by { |r| r.region }.to_a, months.sort_by { |m| m.month }.to_a, rejected)
  end

  def self.json(report : Report) : String
    options = JsonSerializerOptions.new
    options.WriteIndented = true
    JsonSerializer.Serialize(report, options)
  end
end
