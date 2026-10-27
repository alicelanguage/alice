# Bend-vs suite: same algorithms as ../bend/*.bend, UInt32 wrapping arithmetic, Float32 mandelbrot.
# Recursions are written plainly; Alice forks the pure recursive pairs itself (ADR 0015).
enum Tree
  Leaf(v : UInt32)
  Node(l : Tree, r : Tree)
end

def pow2(n : Int32) : UInt32
  return 1_u32 if n == 0
  a, b = pow2(n - 1), pow2(n - 1)
  a &+ b
end

def fib(n : Int32) : UInt32
  return n.to_u32 if n < 2
  a, b = fib(n - 1), fib(n - 2)
  a &+ b
end

def gen(d : Int32, x : UInt32) : Tree
  return Tree::Leaf(x) if d == 0
  a, b = gen(d - 1, x &* 2_u32), gen(d - 1, x &* 2_u32 &+ 1_u32)
  Tree::Node(a, b)
end

def total(t : Tree) : UInt32
  case t
  when Tree::Leaf(v) then v
  when Tree::Node(l, r)
    a, b = total(l), total(r)
    a &+ b
  end
end

def solve(all : UInt32, cols : UInt32, d1 : UInt32, d2 : UInt32) : UInt32
  return 1_u32 if cols == all
  place(all & ~(cols | d1 | d2), all, cols, d1, d2)
end

def place(avail : UInt32, all : UInt32, cols : UInt32, d1 : UInt32, d2 : UInt32) : UInt32
  return 0_u32 if avail == 0_u32
  bit = avail & (0_u32 &- avail)
  a, b = solve(all, cols | bit, (d1 | bit) << 1, (d2 | bit) >> 1), place(avail ^ bit, all, cols, d1, d2)
  a &+ b
end

def iter(cr : Float32, ci : Float32) : UInt32
  zr = 0.0_f32
  zi = 0.0_f32
  i = 0_u32
  while i != 256_u32
    zr2 = zr * zr
    zi2 = zi * zi
    break if zr2 + zi2 > 4.0_f32
    zi = 2.0_f32 * zr * zi + ci
    zr = zr2 - zi2 + cr
    i += 1_u32
  end
  i
end

def row(y : UInt32, w : UInt32) : UInt32
  fw = w.to_f32
  ci = y.to_f32 * (2.0_f32 / fw) - 1.0_f32
  dx = 3.0_f32 / fw
  cr = -2.0_f32
  acc = 0_u32
  x = 0_u32
  while x != w
    acc = acc &+ iter(cr, ci)
    cr = cr + dx
    x += 1_u32
  end
  acc
end

def rows(d : Int32, y : UInt32, w : UInt32) : UInt32
  return row(y, w) if d == 0
  a, b = rows(d - 1, y &* 2_u32, w), rows(d - 1, y &* 2_u32 &+ 1_u32, w)
  a &+ b
end

def steps(x0 : UInt32) : UInt32
  x = x0
  s = 0_u32
  while x > 1_u32 && s != 1000_u32
    x = x.even? ? x >> 1 : x &* 3_u32 &+ 1_u32
    s += 1_u32
  end
  s
end

def range(d : Int32, i : UInt32) : UInt32
  return steps(i &+ 1_u32) if d == 0
  a, b = range(d - 1, i &* 2_u32), range(d - 1, i &* 2_u32 &+ 1_u32)
  a &+ b
end

n = ARGV[1].to_i
r = case ARGV[0]
    when "pow2" then pow2(n)
    when "fib" then fib(n)
    when "tree" then total(gen(n, 1_u32))
    when "nqueens" then solve((1_u32 << n) &- 1_u32, 0_u32, 0_u32, 0_u32)
    when "mandel" then rows(n, 0_u32, 1_u32 << n)
    when "collatz" then range(n, 0_u32)
    else 0_u32
    end
puts r
