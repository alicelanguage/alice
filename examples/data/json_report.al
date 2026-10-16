using System.Text.Json

# Use the .NET JSON API directly, without a wrapper or extra package.
record Report(service : String, healthy : Bool, requests : Int32)

report = Report.new("alice", true, 42)
json = JsonSerializer.Serialize(report)
puts json

document = JsonDocument.Parse(json)
begin
  root = document.RootElement
  name = root.GetProperty("service").ToString()
  count = root.GetProperty("requests").GetInt32()
  puts "#{name.ToUpper()}: #{count} requests"
ensure
  document.Dispose()
end
