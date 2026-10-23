# the fork/join example of docs/fork-join.md; benchmarks/inet compares it with an interaction-net reducer
def pow2(n : Int32) : Int64
  return 1_i64 if n == 0
  a, b = pow2(n - 1), pow2(n - 1)
  a + b
end

def plain(n : Int32) : Int64
  return 1_i64 if n == 0
  plain(n - 1) + plain(n - 1)
end

n = ARGV.size > 0 ? ARGV[0].to_i : 26
best = 1.0e9
bestp = 1.0e9
6.times do
  t = System.Diagnostics.Stopwatch.start_new
  r = pow2(n)
  best = Math.min(best, t.Elapsed.TotalMilliseconds)
  t = System.Diagnostics.Stopwatch.start_new
  r2 = plain(n)
  bestp = Math.min(bestp, t.Elapsed.TotalMilliseconds)
  raise "wrong" if r != r2
end
puts "pow2(#{n}) forked #{best.round(1)} ms, serial #{bestp.round(1)} ms"
