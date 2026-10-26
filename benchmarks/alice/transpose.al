# Loop tiling benchmark (ADR 0025): out-of-place transpose, tiled by the compiler.
# `ALICE_DISABLE_PASSES=loop-tile` at compile time gives the untiled baseline.
def transpose(a : Array(Float64), t : Array(Float64), n : Int32)
  for i in 0...n
    for j in 0...n
      t[j * n + i] = a[i * n + j]
    end
  end
end

def measure(n : Int32, a : Array(Float64), t : Array(Float64)) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  for rep in 0...10
    transpose(a, t, n)
  end
  ms = sw.Elapsed.TotalMilliseconds / 10.0
  raise "wrong" if t[1 * n + 3] != a[3 * n + 1]
  ms
end

[2048, 4096].each do |n|
  a = Array(Float64).new(n * n)
  for i in 0...n * n
    a[i] = i * 0.5
  end
  t = Array(Float64).new(n * n)
  measure(n, a, t)
  times = Array(Float64).new(5)
  for r in 0...5
    times[r] = measure(n, a, t)
  end
  System.Array.Sort(times)
  puts "RESULT transpose#{n} #{times[2]}"
end
