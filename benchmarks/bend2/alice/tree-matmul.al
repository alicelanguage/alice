# tree-matmul, flat shape: the same rounds, generators, block multiply and Freivalds check
# as main.bend, with each (always complete) quad-tree stored as its leaves in Z order
# (quadrants a, b, c, d = top-left, top-right, bottom-left, bottom-right) and each vector
# as its leaves in order. mul and mvm recurse on quadrants exactly like the tree code;
# blocks of 16x16 leaves use a direct kernel (the same u32 ring, so the same sums).
# Every method returns a fresh array: all stay pure and the batch tree forks.
# Big (7, 9, 384): 3797651056.
# Sum-type (ADT) shape of this workload: tree-matmul-adt.al.

def zidx(i : Int32, j : Int32, k : Int32) : Int32
  z = 0
  b = k - 1
  while b >= 0
    z = z * 4 + ((i >> b) & 1) * 2 + ((j >> b) & 1)
    b -= 1
  end
  z
end

# gen(d, s): leaf p's seed follows its quadrant path (digits of p in base 4, root first)
def gen(d : Int32, s : UInt32) : Array(UInt32)
  n = 1 << (2 * d)
  m = Array(UInt32).new(n)
  for p in 0...n
    x = s
    b = d - 1
    while b >= 0
      q = (p >> (2 * b)) & 3
      x = case q
          when 0 then x &* 1664525_u32 &+ 1_u32
          when 1 then x &* 214013_u32 &+ 3_u32
          when 2 then x &* 16843009_u32 &+ 5_u32
          else x &* 48271_u32 &+ 7_u32
          end
      b -= 1
    end
    m[p] = x % 100_u32
  end
  m
end

def vgen(d : Int32, s : UInt32) : Array(UInt32)
  n = 1 << d
  v = Array(UInt32).new(n)
  for p in 0...n
    x = s
    b = d - 1
    while b >= 0
      x = ((p >> b) & 1) == 0 ? x &* 1664525_u32 &+ 1_u32 : x &* 214013_u32 &+ 3_u32
      b -= 1
    end
    v[p] = (x &* 2654435761_u32) % 100_u32 &+ 1_u32
  end
  v
end

# block product for k <= 4 (16x16 leaves): Z order in and out, row-major inside
def mul_kernel(a : Array(UInt32), ao : Int32, b : Array(UInt32), bo : Int32, k : Int32) : Array(UInt32)
  w = 1 << k
  am = Array(UInt32).new(w * w)
  bm = Array(UInt32).new(w * w)
  cm = Array(UInt32).new(w * w)
  for i in 0...w
    for j in 0...w
      z = zidx(i, j, k)
      am[i * w + j] = a[ao + z]
      bm[i * w + j] = b[bo + z]
    end
  end
  for i in 0...w
    for l in 0...w
      x = am[i * w + l]
      for j in 0...w
        cm[i * w + j] = cm[i * w + j] &+ x &* bm[l * w + j]
      end
    end
  end
  r = Array(UInt32).new(w * w)
  for i in 0...w
    for j in 0...w
      r[zidx(i, j, k)] = cm[i * w + j]
    end
  end
  r
end

# C = A * B on the 4^k-leaf blocks a[ao..] and b[bo..]: 8 products, 4 adds per node
def mul(a : Array(UInt32), ao : Int32, b : Array(UInt32), bo : Int32, k : Int32) : Array(UInt32)
  return mul_kernel(a, ao, b, bo, k) if k <= 4
  q = 1 << (2 * (k - 1))
  p0, q0 = mul(a, ao, b, bo, k - 1), mul(a, ao + q, b, bo + 2 * q, k - 1)
  p1, q1 = mul(a, ao, b, bo + q, k - 1), mul(a, ao + q, b, bo + 3 * q, k - 1)
  p2, q2 = mul(a, ao + 2 * q, b, bo, k - 1), mul(a, ao + 3 * q, b, bo + 2 * q, k - 1)
  p3, q3 = mul(a, ao + 2 * q, b, bo + q, k - 1), mul(a, ao + 3 * q, b, bo + 3 * q, k - 1)
  r = Array(UInt32).new(4 * q)
  for t in 0...q
    r[t] = p0[t] &+ q0[t]
    r[q + t] = p1[t] &+ q1[t]
    r[2 * q + t] = p2[t] &+ q2[t]
    r[3 * q + t] = p3[t] &+ q3[t]
  end
  r
end

# [[a, b], [c, d]] * (l, h) = (a*l + b*h, c*l + d*h) on the 4^k block m[mo..], vector v[vo..]
def mvm(m : Array(UInt32), mo : Int32, v : Array(UInt32), vo : Int32, k : Int32) : Array(UInt32)
  w = 1 << k
  r = Array(UInt32).new(w)
  if k <= 3
    for i in 0...w
      acc = 0_u32
      for l in 0...w
        acc = acc &+ m[mo + zidx(i, l, k)] &* v[vo + l]
      end
      r[i] = acc
    end
    return r
  end
  q = 1 << (2 * (k - 1))
  h = w / 2
  t0, t1 = mvm(m, mo, v, vo, k - 1), mvm(m, mo + q, v, vo + h, k - 1)
  t2, t3 = mvm(m, mo + 2 * q, v, vo, k - 1), mvm(m, mo + 3 * q, v, vo + h, k - 1)
  for i in 0...h
    r[i] = t0[i] &+ t1[i]
    r[h + i] = t2[i] &+ t3[i]
  end
  r
end

def round_run(d : Int32, s : UInt32) : UInt32
  a = gen(d, s &+ 1_u32)
  b = gen(d, s &+ 2_u32)
  r = vgen(d, s &+ 3_u32)
  c = mul(a, 0, b, 0, d)
  k = 0_u32
  for x in c
    k = k &+ x
  end
  t1 = mvm(c, 0, r, 0, d)
  u = mvm(b, 0, r, 0, d)
  t2 = mvm(a, 0, u, 0, d)
  dif = 0_u32
  for i in 0...t1.size
    dif = dif | (t1[i] ^ t2[i])
  end
  (k ^ (s &* 2654435761_u32)) &+ (dif == 0_u32 ? 1_u32 : 0_u32)
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
