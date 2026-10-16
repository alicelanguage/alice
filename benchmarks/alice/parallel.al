def kernel(xs : Array(Float64)) : Float64
  ys : Array(Float64) = Par.Map(xs, ->(x : Float64) { Math.Sqrt(x) * Math.Sin(x) + Math.Cos(x * 0.5) })
  ys.sum
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
  xs[i] = i * 0.0001
end
check = Convert.ToInt64(kernel(xs))
# warm-up: >= 50 calls and >= 1 s so tier-1 + PGO code replaces tier-0/OSR
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 50 || warm.Elapsed.TotalMilliseconds < 1000.0
  kernel(xs)
  wn += 1
end
times = Array(Float64).new(15)
for r in 0...15
  times[r] = measure(xs, check)
end
System.Array.Sort(times)
puts "RESULT parallel #{times[7]} #{check}"
