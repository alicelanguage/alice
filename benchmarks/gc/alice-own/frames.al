# Double-buffered 1D stencil over 4 MiB frames: every frame allocates a large-object-heap array.
def step(n : Int32, frames : Int32) : Int64
  own cur = Array(Int32).new(n)
  for i in 0...n
    cur[i] = i & 0xFFFF
  end
  for f in 0...frames
    own nxt = Array(Int32).new(n)
    i = 1
    while i < n - 1
      nxt[i] = (cur[i - 1] + cur[i] + cur[i + 1] + f) & 0xFFFF
      i += 1
    end
    cur = nxt
  end
  total : Int64 = 0
  for v in cur
    total += v
  end
  total
end

def kernel : Int64
  step(1 << 20, 120)
end

def warmup
  step(1024, 4)
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
puts "GCSTAT frames #{times[3]} #{alloc / 7} #{g0} #{g1} #{g2} #{pause} #{peak} #{check}"
