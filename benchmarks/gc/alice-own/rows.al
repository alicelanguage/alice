# Edit distance with a fresh row per outer iteration: the dynamic-programming pattern ownership recycles.
def seq(n : Int32, seed : Int32) : Array(Int32)
  xs = Array(Int32).new(n)
  s = seed
  for i in 0...n
    s = (s * 1103 + 12345) & 0xFFFF
    xs[i] = s & 3
  end
  xs
end

def dist(a : Array(Int32), b : Array(Int32)) : Int32
  m = b.size
  own previous = Array(Int32).new(m + 1)
  for j in 0..m
    previous[j] = j
  end
  for i in 1..a.size
    own row = Array(Int32).new(m + 1)
    row[0] = i
    for j in 1..m
      cost = a[i - 1] == b[j - 1] ? 0 : 1
      row[j] = Math.Min(Math.Min(row[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost)
    end
    previous = row
  end
  previous[m]
end

def kernel : Int64
  total : Int64 = 0
  k = 0
  while k < 4
    total += dist(seq(2000, k + 1), seq(2000, k + 7))
    k += 1
  end
  total
end

def warmup
  dist(seq(64, 1), seq(64, 2))
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
puts "GCSTAT rows #{times[3]} #{alloc / 7} #{g0} #{g1} #{g2} #{pause} #{peak} #{check}"
