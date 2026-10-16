def kernel(size : Int32) : Int64
  inside : Int64 = 0
  for py in 0...size
    ci = 2.0 * py / size - 1.0
    for px in 0...size
      cr = 2.0 * px / size - 1.5
      zr = 0.0
      zi = 0.0
      it = 0
      while it < 100 && zr * zr + zi * zi <= 4.0
        t = zr * zr - zi * zi + cr
        zi = 2.0 * zr * zi + ci
        zr = t
        it += 1
      end
      inside += 1 if it == 100
    end
  end
  inside
end

def measure(size : Int32, check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel(size)
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

size = 1000
check = kernel(size)
# warm-up: >= 50 calls and >= 1 s so tier-1 + PGO code replaces tier-0/OSR
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 50 || warm.Elapsed.TotalMilliseconds < 1000.0
  kernel(200)
  wn += 1
end
times = Array(Float64).new(15)
for r in 0...15
  times[r] = measure(size, check)
end
System.Array.Sort(times)
puts "RESULT mandelbrot #{times[7]} #{check}"
