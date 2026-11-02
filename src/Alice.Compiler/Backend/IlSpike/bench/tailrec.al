# il-spike micro for optimization (b): self-recursive tail calls (none exist in the bend2 workloads).
# collatz(n, acc) recurses in tail position; sum_to(n, acc) recurses n deep (stack overflow unless the call is a jump).
def collatz(n : UInt64, acc : UInt64) : UInt64
  return acc if n == 1_u64
  collatz(n % 2_u64 == 0_u64 ? n / 2_u64 : 3_u64 &* n &+ 1_u64, acc &+ 1_u64)
end

def sum_to(n : Int64, acc : Int64) : Int64
  return acc if n == 0_i64
  sum_to(n - 1_i64, acc &+ n)
end

def run(k : UInt64) : UInt64
  t = 0_u64
  i = 1_u64
  while i < k
    t = t &+ collatz(i, 0_u64)
    i = i &+ 1_u64
  end
  t
end

k = ARGV.size > 0 ? ARGV[0].to_i : 1000000
deep = ARGV.size > 1 ? ARGV[1].to_i : 0
puts run(k.to_u64)
puts sum_to(deep.to_i64, 0_i64) if deep > 0
