enum Expr
  Num(v : Int64)
  Add(a : Expr, b : Expr)
  Mul(a : Expr, b : Expr)
  Neg(e : Expr)
end

def eval(e : Expr) : Int64
  case e
  when Expr::Num(v) then v
  when Expr::Add(a, b) then eval(a) &+ eval(b)
  when Expr::Mul(a, b) then eval(a) &* eval(b)
  when Expr::Neg(x) then 0_i64 &- eval(x)
  end
end

def build(d : Int32, seed : Int32) : Expr
  if d == 0
    Expr::Num(seed % 7 + 1)
  else
    k = seed % 5
    s1 = (seed * 31 + 7) % 1000003
    s2 = (seed * 17 + 3) % 1000003
    if k == 0
      Expr::Neg(build(d - 1, s1))
    elsif k == 1
      Expr::Mul(build(d - 1, s1), build(d - 1, s2))
    else
      Expr::Add(build(d - 1, s1), build(d - 1, s2))
    end
  end
end

def kernel(tree : Expr) : Int64
  acc : Int64 = 0
  for i in 0...10
    acc = acc &+ eval(tree)
  end
  acc
end

def measure(tree : Expr, check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel(tree)
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

tree = build(20, 12345)
check = kernel(tree)
# warm-up: >= 30 calls (tier-1 promotion threshold) and >= 0.3 s
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 30 || warm.Elapsed.TotalMilliseconds < 300.0
  kernel(tree)
  wn += 1
end
times = Array(Float64).new(7)
for r in 0...7
  times[r] = measure(tree, check)
end
System.Array.Sort(times)
puts "RESULT expr #{times[3]} #{check}"
