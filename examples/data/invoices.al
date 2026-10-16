# Integer cents keep this small billing report exact.
record Invoice(customer : String, total_cents : Int32, paid : Bool)

invoices = [
Invoice.new("Ada", 1000, true),
Invoice.new("Lin", 250, true),
Invoice.new("Maya", 900, false),
Invoice.new("Sam", 3000, true)
]

paid = invoices.select { |invoice| invoice.paid }
revenue = paid.sum { |invoice| invoice.total_cents }
large = paid.select { |invoice| invoice.total_cents >= 2000 }

puts "paid invoices=#{paid.size}"
puts "revenue cents=#{revenue}"
puts "large invoice cents=#{large.sum { |invoice| invoice.total_cents }}"
