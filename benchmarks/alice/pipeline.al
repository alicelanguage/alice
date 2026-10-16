def kernel(xs : Array(Float64)) : Float64
  xs.map { |x| x * 1.5 + 0.25 }.select { |x| x > 0.75 }.sum
end

def measure(xs : Array(Float64), check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = Convert.ToInt64(kernel(xs))
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

n = 10000000
xs = Array(Float64).new(n)
for i in 0...n
  xs[i] = (i % 1000) * 0.001
end
check = Convert.ToInt64(kernel(xs))
# warm-up: >= 30 calls (tier-1 promotion threshold) and >= 0.3 s
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 30 || warm.Elapsed.TotalMilliseconds < 300.0
  kernel(xs)
  wn += 1
end
times = Array(Float64).new(7)
for r in 0...7
  times[r] = measure(xs, check)
end
System.Array.Sort(times)
puts "RESULT pipeline #{times[3]} #{check}"
