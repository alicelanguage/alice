# CSV -> aggregate -> JSON.
#   alice run examples/data/pipeline                          the bundled data/sales.csv, JSON on stdout
#   alice run examples/data/pipeline -- in.csv out.json       any file with the same header
using System.IO
require "./csv"
require "./report"

input = ARGV.size > 0 ? ARGV[0] : Path.Combine(AppContext.BaseDirectory, "data", "sales.csv")
rejected = List(String).new
sales = Pipeline.parse(Csv.records(File.ReadAllLines(input)), rejected)
json = Pipeline.json(Pipeline.report(Path.GetFileName(input), sales, rejected.to_a))
if ARGV.size > 1
  File.WriteAllText(ARGV[1], json)
  puts "wrote #{ARGV[1]}: #{sales.size} sales, #{rejected.Count} rejected"
else
  puts json
end
