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

def bottom_up(depth : Int32) : Node
  if depth > 0
    Node.new(bottom_up(depth - 1), bottom_up(depth - 1))
  else
    Node.new(nil, nil)
  end
end

def kernel(max_depth : Int32) : Int64
  min_depth = 4
  total : Int64 = 0
  total += bottom_up(max_depth + 1).check
  long_lived = bottom_up(max_depth)
  d = min_depth
  while d <= max_depth
    iters = 1 << (max_depth - d + min_depth)
    c = 0
    for i in 0...iters
      c += bottom_up(d).check
    end
    total += c
    d += 2
  end
  total += long_lived.check
  total
end

def measure(depth : Int32, check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel(depth)
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

depth = 16
check = kernel(depth)
# warm-up: >= 30 calls (tier-1 promotion threshold) and >= 0.3 s
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 30 || warm.Elapsed.TotalMilliseconds < 300.0
  kernel(10)
  wn += 1
end
times = Array(Float64).new(7)
for r in 0...7
  times[r] = measure(depth, check)
end
System.Array.Sort(times)
puts "RESULT binarytrees #{times[3]} #{check}"
