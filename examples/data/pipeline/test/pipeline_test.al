require "../src/csv"
require "../src/report"
using System.Text.Json

def test_quoted_fields
  assert_equal "a|b, c|say \"hi\"|", Csv.fields("a,\"b, c\",\"say \"\"hi\"\"\",").not_nil!.join("|")
  assert Csv.fields("a,\"open").nil?
end

def test_a_quoted_line_break_stays_in_its_field
  records = Csv.records(["h1,h2", "1,\"two", "lines\"", "", "3,4"])
  assert_equal 3, records.size
  assert_equal "two\nlines", records[1][1]
end

def test_money_is_whole_cents
  assert_equal 450_i64, Money.parse_cents("4.50") || -1_i64
  assert_equal 1200_i64, Money.parse_cents("12") || -1_i64
  assert_equal 50_i64, Money.parse_cents("0.5") || -1_i64
  assert Money.parse_cents("1.234").nil?
  assert Money.parse_cents("-1.00").nil?
  assert Money.parse_cents("abc").nil?
  assert_equal "29.05", Money.format(2905_i64)
end

def test_bad_rows_are_reported_not_counted
  rejected = List(String).new
  records = Csv.records(["date,region,product,quantity,unit_price", "2026-01-01,North,Tea,2,1.00", "2026-01-02,North,Tea,zero,1.00", "2026-01-03,,Tea,1,1.00", "2026-01-04,North,Tea,1,1.999"])
  sales = Pipeline.parse(records, rejected)
  assert_equal 1, sales.size
  assert_equal "record 3: 2026-01-02,North,Tea,zero,1.00|record 4: 2026-01-03,,Tea,1,1.00|record 5: 2026-01-04,North,Tea,1,1.999", rejected.join("|")
end

def test_wrong_header_is_an_error
  begin
    Pipeline.parse(Csv.records(["when,where", "x,y"]), List(String).new)
    assert false, "expected an error"
  rescue e : ArgumentException
    assert e.Message.includes?("expected the header")
  end
end

def test_report_totals_and_json_shape
  sales = [Sale.new("2026-01-05", "North", "Tea", 2, 450_i64), Sale.new("2026-02-01", "North", "Pot", 1, 2400_i64), Sale.new("2026-02-03", "East", "Tea", 1, 450_i64)]
  report = Pipeline.report("t.csv", sales, Array(String).new(0))
  assert_equal 3750_i64, report.revenueCents
  assert_equal "Pot", report.topProduct
  assert_equal "East North", report.regions.map { |r| r.region }.join(" ")
  assert_equal 1650_i64, report.regions[1].averageOrderCents
  assert_equal "2026-01:900 2026-02:2850", report.months.map { |m| "#{m.month}:#{m.revenueCents}" }.join(" ")
  doc = JsonDocument.Parse(Pipeline.json(report))
  assert_equal 3750, doc.RootElement.GetProperty("revenueCents").GetInt32()
  assert_equal "North", doc.RootElement.GetProperty("regions")[1].GetProperty("region").GetString()
end

# Every amount up to 1000.00 formats and parses back to itself. (A RULES.al rule would prove it, but string
# methods are not yet accepted as pure, so the loop runs here instead.)
def test_cents_round_trip_for_every_amount_to_1000
  for c in 0..100000
    assert_equal c.to_i64, Money.parse_cents(Money.format(c.to_i64)) || -1_i64
  end
end
