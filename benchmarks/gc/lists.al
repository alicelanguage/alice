def evens_sum(n : Int32) : Int64
  xs = List(Int64).new
  i = 0
  while i < n
    xs << i.to_i64 if i % 2 == 0
    i += 1
  end
  t = 0_i64
  xs.each { |x| t += x }
  t
end

best = 1.0e9
total = 0_i64
g0 = GC.CollectionCount(0)
before = GC.GetTotalAllocatedBytes(true)
7.times do
  sw = System.Diagnostics.Stopwatch.start_new
  200_000.times { total += evens_sum(200) }
  best = Math.min(best, sw.Elapsed.TotalMilliseconds)
end
mb = (GC.GetTotalAllocatedBytes(true) - before) / 1048576
puts "best #{best.round(1)} ms, allocated #{mb} MiB, gen0 #{GC.CollectionCount(0) - g0} (#{total})"
