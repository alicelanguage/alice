# Binary trees: object churn the GC must trace. Ownership does not apply; both sides use the GC.
class Node
  getter left : Node?
  getter right : Node?
  def initialize(@left : Node?, @right : Node?)
  end

  def check : Int32
    l = left
    r = right
    if l.nil? || r.nil?
      1
    else
      1 + l.check + r.check
    end
  end
end

def build(depth : Int32) : Node
  if depth > 0
    Node.new(build(depth - 1), build(depth - 1))
  else
    Node.new(nil, nil)
  end
end

def kernel : Int64
  total : Int64 = 0
  long_lived = build(16)
  i = 0
  while i < 256
    total += build(12).check
    i += 1
  end
  total + long_lived.check
end

def warmup
  build(8).check
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
puts "GCSTAT trees #{times[3]} #{alloc / 7} #{g0} #{g1} #{g2} #{pause} #{peak} #{check}"
