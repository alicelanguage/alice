# Stream fusion (ADR 0024): take(n), to_a and an impure-free chain over a range, each one loop.
def head(xs : Array(Int32), n : Int32) : Int64
  xs.map { |x| x.to_i64 * 3 }.select { |x| x.odd? }.take(n).sum
end

def squares(xs : Array(Int32)) : Int32
  xs.map { |x| x + 1 }.map { |x| x * 2 }.to_a.size
end

def pairs(n : Int32) : Int64
  (0...n).map { |i|
    p = [i.to_i64, i.to_i64 + 1]
    p[0] * p[1]
  }.reject { |x| x % 3 == 0 }.sum
end

def kernel(xs : Array(Int32)) : Int64
  head(xs, xs.size / 2) + squares(xs) + pairs(xs.size)
end

def measure(xs : Array(Int32), check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel(xs)
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

n = 2000000
xs = Array(Int32).new(n)
for i in 0...n
  xs[i] = i % 1000
end
check = kernel(xs)
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
puts "RESULT pipeline_take #{times[3]} #{check}"
