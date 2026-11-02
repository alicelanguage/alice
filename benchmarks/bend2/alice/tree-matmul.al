# tree-matmul: Bend 2 runtime suite (bench/runtime/tree-matmul), 1:1 with main.c / main.bend.
# Sum-type (ADT) 1:1 port (Workstream A gate, ADR 0039); the fastest shape is idiomatic/tree-matmul.al.
# 384 rounds (live leaves of a 2^9 batch fork tree): generate two depth-7 quad-tree
# matrices and a vector, C = A*B by recursive block multiply (8 products, 4 adds per
# node), checksum C, Freivalds-verify C*r == A*(B*r); wrapping u32 throughout.
# Bend's 4- and 8-way parallel lets are written as two-call forks (pairs); only the
# batch tree forks in practice (the cutoff is reached above the rounds).
# Idiomatic shape: sum type nodes on the GC heap. Big (7, 9, 384): 3797651056.
enum Mat
  Lf(v : UInt32)
  Qd(a : Mat, b : Mat, c : Mat, d : Mat)
end

enum Vec
  Vl(v : UInt32)
  Vn(l : Vec, r : Vec)
end

def gen(d : Int32, s : UInt32) : Mat
  return Mat::Lf(s % 100_u32) if d == 0
  a, b = gen(d - 1, s &* 1664525_u32 &+ 1_u32), gen(d - 1, s &* 214013_u32 &+ 3_u32)
  c, e = gen(d - 1, s &* 16843009_u32 &+ 5_u32), gen(d - 1, s &* 48271_u32 &+ 7_u32)
  Mat::Qd(a, b, c, e)
end

def vgen(d : Int32, s : UInt32) : Vec
  return Vec::Vl((s &* 2654435761_u32) % 100_u32 &+ 1_u32) if d == 0
  l, r = vgen(d - 1, s &* 1664525_u32 &+ 1_u32), vgen(d - 1, s &* 214013_u32 &+ 3_u32)
  Vec::Vn(l, r)
end

def add(a : Mat, b : Mat) : Mat
  case a
  when Mat::Lf(x)
    case b
    when Mat::Lf(y) then Mat::Lf(x &+ y)
    when Mat::Qd(b0, b1, b2, b3) then Mat::Lf(0_u32)
    end
  when Mat::Qd(a0, a1, a2, a3)
    case b
    when Mat::Lf(y) then Mat::Lf(0_u32)
    when Mat::Qd(b0, b1, b2, b3)
      r0, r1 = add(a0, b0), add(a1, b1)
      r2, r3 = add(a2, b2), add(a3, b3)
      Mat::Qd(r0, r1, r2, r3)
    end
  end
end

def mul(a : Mat, b : Mat) : Mat
  case a
  when Mat::Lf(x)
    case b
    when Mat::Lf(y) then Mat::Lf(x &* y)
    when Mat::Qd(b0, b1, b2, b3) then Mat::Lf(0_u32)
    end
  when Mat::Qd(a0, a1, a2, a3)
    case b
    when Mat::Lf(y) then Mat::Lf(0_u32)
    when Mat::Qd(b0, b1, b2, b3)
      p0, q0 = mul(a0, b0), mul(a1, b2)
      p1, q1 = mul(a0, b1), mul(a1, b3)
      p2, q2 = mul(a2, b0), mul(a3, b2)
      p3, q3 = mul(a2, b1), mul(a3, b3)
      c0, c1 = add(p0, q0), add(p1, q1)
      c2, c3 = add(p2, q2), add(p3, q3)
      Mat::Qd(c0, c1, c2, c3)
    end
  end
end

def vadd(x : Vec, y : Vec) : Vec
  case x
  when Vec::Vl(a)
    case y
    when Vec::Vl(b) then Vec::Vl(a &+ b)
    when Vec::Vn(c, d) then Vec::Vl(0_u32)
    end
  when Vec::Vn(a, b)
    case y
    when Vec::Vl(c) then Vec::Vl(0_u32)
    when Vec::Vn(c, d)
      l, r = vadd(a, c), vadd(b, d)
      Vec::Vn(l, r)
    end
  end
end

def mvm(m : Mat, v : Vec) : Vec
  case m
  when Mat::Lf(x)
    case v
    when Vec::Vl(y) then Vec::Vl(x &* y)
    when Vec::Vn(l, h) then Vec::Vl(0_u32)
    end
  when Mat::Qd(a, b, c, d)
    case v
    when Vec::Vl(y) then Vec::Vl(0_u32)
    when Vec::Vn(l, h)
      t0, t1 = mvm(a, l), mvm(b, h)
      t2, t3 = mvm(c, l), mvm(d, h)
      top, bot = vadd(t0, t1), vadd(t2, t3)
      Vec::Vn(top, bot)
    end
  end
end

def cksum(m : Mat) : UInt32
  case m
  when Mat::Lf(x) then x
  when Mat::Qd(a, b, c, d)
    p, q = cksum(a), cksum(b)
    r, s = cksum(c), cksum(d)
    ((p &+ q) &+ r) &+ s
  end
end

def vdif(x : Vec, y : Vec) : UInt32
  case x
  when Vec::Vl(a)
    case y
    when Vec::Vl(b) then a ^ b
    when Vec::Vn(c, d) then 1_u32
    end
  when Vec::Vn(a, b)
    case y
    when Vec::Vl(c) then 1_u32
    when Vec::Vn(c, d)
      l, r = vdif(a, c), vdif(b, d)
      l | r
    end
  end
end

def round_run(d : Int32, s : UInt32) : UInt32
  a = gen(d, s &+ 1_u32)
  b = gen(d, s &+ 2_u32)
  r = vgen(d, s &+ 3_u32)
  c = mul(a, b)
  k = cksum(c)
  t1 = mvm(c, r)
  t2 = mvm(a, mvm(b, r))
  (k ^ (s &* 2654435761_u32)) &+ (vdif(t1, t2) == 0_u32 ? 1_u32 : 0_u32)
end

def batch(bg : Int32, i : UInt32, n : UInt32, m : UInt32, d : Int32) : UInt32
  if bg == 0
    pp = (i &* 2654435761_u32) & m
    return pp < n ? round_run(d, pp &* 2654435761_u32) : 0_u32
  end
  x, y = batch(bg - 1, i, n, m, d), batch(bg - 1, i &+ (1_u32 << (bg - 1)), n, m, d)
  x &+ y
end

size = ARGV.size > 0 ? ARGV[0].to_i : 7
blog = 9
puts batch(blog, 0_u32, 384_u32, (1_u32 << blog) &- 1_u32, size)
