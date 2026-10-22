# A temporary buffer per call: many short-lived small arrays.
def window_score(src : Array(Int32), start : Int32, w : Int32) : Int32
  buf = Array(Int32).new(w)
  score = 0
  unchecked    # same wrapping arithmetic as the C# baseline
    for k in 0...w
      buf[k] = (src[start + k] * 7 + k) & 1023
    end
    for k in 1...w
      d = buf[k] - buf[k - 1]
      score += d < 0 ? -d : d
    end
  end
  score
end

def source(n : Int32) : Array(Int32)
  xs = Array(Int32).new(n)
  s = 7
  for i in 0...n
    s = (s * 1103 + 12345) & 0xFFFF
    xs[i] = s
  end
  xs
end

def kernel : Int64
  src = source(1_000_000)
  total : Int64 = 0
  s = 0
  while s < 300_000
    total += window_score(src, s * 3, 512)
    s += 1
  end
  total
end

def warmup
  window_score(source(1024), 0, 512)
end

def measure(check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

check = kernel
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 30 || warm.Elapsed.TotalMilliseconds < 300.0
  warmup
  wn += 1
end
System.GC.Collect
g0 = System.GC.CollectionCount(0)
g1 = System.GC.CollectionCount(1)
g2 = System.GC.CollectionCount(2)
alloc = System.GC.GetTotalAllocatedBytes(true)
pause = System.GC.GetTotalPauseDuration.TotalMilliseconds
times = Array(Float64).new(7)
for r in 0...7
  times[r] = measure(check)
end
alloc = System.GC.GetTotalAllocatedBytes(true) - alloc
pause = System.GC.GetTotalPauseDuration.TotalMilliseconds - pause
g0 = System.GC.CollectionCount(0) - g0
g1 = System.GC.CollectionCount(1) - g1
g2 = System.GC.CollectionCount(2) - g2
peak = System.Diagnostics.Process.GetCurrentProcess.PeakWorkingSet64
System.Array.Sort(times)
puts "GCSTAT scratch #{times[3]} #{alloc / 7} #{g0} #{g1} #{g2} #{pause} #{peak} #{check}"
