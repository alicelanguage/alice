# Parallel pipeline: map-filter-reduce fuses into one parallel loop
def main
  sw = System.Diagnostics.Stopwatch.StartNew
  sum = (0...10_000_000)
    .map { |n| n * n }
    .filter { |n| n % 3 == 0 }
    .sum
  puts "sum = #{sum} in #{sw.ElapsedMilliseconds} ms"
end
