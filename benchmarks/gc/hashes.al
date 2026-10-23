def histogram_peak(n : Int32) : Int32
  counts = Dictionary(Int32, Int32).new
  i = 0
  while i < n
    k = (i * 7919) % 64
    counts[k] = counts.fetch(k, 0) + 1
    i += 1
  end
  best = 0
  counts.each { |k, v| best = v if v > best }
  best
end

best = 1.0e9
total = 0
g0 = GC.CollectionCount(0)
before = GC.GetTotalAllocatedBytes(true)
7.times do
  sw = System.Diagnostics.Stopwatch.start_new
  100_000.times { total += histogram_peak(256) }
  best = Math.min(best, sw.Elapsed.TotalMilliseconds)
end
mb = (GC.GetTotalAllocatedBytes(true) - before) / 1048576
puts "best #{best.round(1)} ms, allocated #{mb} MiB, gen0 #{GC.CollectionCount(0) - g0} (#{total})"
