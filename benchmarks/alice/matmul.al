# Loop tiling benchmark (ADR 0025): the textbook i-j-k multiply. The compiler interchanges it to i-k-j and
# tiles it; `ALICE_DISABLE_PASSES=loop-tile` at compile time gives the untiled baseline.
def matmul(a : Array(Float64), b : Array(Float64), c : Array(Float64), n : Int32)
  for i in 0...n
    for j in 0...n
      for k in 0...n
        c[i * n + j] += a[i * n + k] * b[k * n + j]
      end
    end
  end
end

def fill(n : Int32, seed : Int32) : Array(Float64)
  xs = Array(Float64).new(n)
  for i in 0...n
    xs[i] = ((i * 7 + seed) % 13) * 0.125 - 0.75
  end
  xs
end

def measure(n : Int32, a : Array(Float64), b : Array(Float64)) : Float64
  c = Array(Float64).new(n * n)
  sw = System.Diagnostics.Stopwatch.StartNew
  matmul(a, b, c, n)
  ms = sw.Elapsed.TotalMilliseconds
  s = 0.0
  for i in 0...n * n
    s += c[i] * ((i % 17) + 1)
  end
  puts "CHECK #{n} #{s}"
  ms
end

[512, 1024].each do |n|
  a = fill(n * n, 1)
  b = fill(n * n, 5)
  measure(n, a, b)
  times = Array(Float64).new(5)
  for r in 0...5
    times[r] = measure(n, a, b)
  end
  System.Array.Sort(times)
  puts "RESULT matmul#{n} #{times[2]}"
end
