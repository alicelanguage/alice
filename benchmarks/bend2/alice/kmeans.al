# kmeans: k-means with 64 random restarts, 20 Lloyd rounds over 2^SIZE prng points, K = 8
# (port of bend/bench/runtime/kmeans). Same point hash, branchless argmin, 64-point leaf chunks
# with 24 scalar accumulators, stats tree per chunk merged by szip, and centroid update as main.c.
# The stats tree is an enum (GC objects), like Bend's `St` data type. Two fork trees, as in Bend:
# rbatch over the restarts and pfold over the point chunks.
enum St
  Sl(sx : UInt32, sy : UInt32, n : UInt32)
  Sn(l : St, r : St)
end

def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def min2(a : UInt32, b : UInt32) : UInt32
  a < b ? a : b
end

def dst(x : UInt32, y : UInt32, c : UInt32, k : UInt32) : UInt32
  cx = c & 65535_u32
  cy = c >> 16
  dx = x < cx ? cx &- x : x &- cx
  dy = y < cy ? cy &- y : y &- cy
  ((dx &* dx &+ dy &* dy) << 3) | k
end

def szip(a : St, b : St) : St
  case a
  when St::Sl(ax, ay, an)
    case b
    when St::Sl(bx, by, bn) then St::Sl(ax &+ bx, ay &+ by, an &+ bn)
    else a
    end
  when St::Sn(al, ar)
    case b
    when St::Sn(bl, br)
      l, r = szip(al, bl), szip(ar, br)
      St::Sn(l, r)
    else a
    end
  end
end

# one leaf chunk: 64 points, each assigned to its nearest centroid, 8 x {sx, sy, n} in scalars
def chunk(i : UInt32, c0 : UInt32, c1 : UInt32, c2 : UInt32, c3 : UInt32,
          c4 : UInt32, c5 : UInt32, c6 : UInt32, c7 : UInt32) : St
  s0x = 0_u32
  s0y = 0_u32
  s0n = 0_u32
  s1x = 0_u32
  s1y = 0_u32
  s1n = 0_u32
  s2x = 0_u32
  s2y = 0_u32
  s2n = 0_u32
  s3x = 0_u32
  s3y = 0_u32
  s3n = 0_u32
  s4x = 0_u32
  s4y = 0_u32
  s4n = 0_u32
  s5x = 0_u32
  s5y = 0_u32
  s5n = 0_u32
  s6x = 0_u32
  s6y = 0_u32
  s6n = 0_u32
  s7x = 0_u32
  s7y = 0_u32
  s7n = 0_u32
  j = 64_u32
  while j != 0_u32
    hh = prng((i &+ j) &* 2654435761_u32)
    xx = hh & 1023_u32
    yy = (hh >> 16) & 1023_u32
    bk = min2(min2(min2(dst(xx, yy, c0, 0_u32), dst(xx, yy, c1, 1_u32)),
                   min2(dst(xx, yy, c2, 2_u32), dst(xx, yy, c3, 3_u32))),
              min2(min2(dst(xx, yy, c4, 4_u32), dst(xx, yy, c5, 5_u32)),
                   min2(dst(xx, yy, c6, 6_u32), dst(xx, yy, c7, 7_u32)))) & 7_u32
    m0 = bk == 0_u32 ? 1_u32 : 0_u32
    m1 = bk == 1_u32 ? 1_u32 : 0_u32
    m2 = bk == 2_u32 ? 1_u32 : 0_u32
    m3 = bk == 3_u32 ? 1_u32 : 0_u32
    m4 = bk == 4_u32 ? 1_u32 : 0_u32
    m5 = bk == 5_u32 ? 1_u32 : 0_u32
    m6 = bk == 6_u32 ? 1_u32 : 0_u32
    m7 = bk == 7_u32 ? 1_u32 : 0_u32
    s0x = s0x &+ m0 &* xx
    s0y = s0y &+ m0 &* yy
    s0n = s0n &+ m0
    s1x = s1x &+ m1 &* xx
    s1y = s1y &+ m1 &* yy
    s1n = s1n &+ m1
    s2x = s2x &+ m2 &* xx
    s2y = s2y &+ m2 &* yy
    s2n = s2n &+ m2
    s3x = s3x &+ m3 &* xx
    s3y = s3y &+ m3 &* yy
    s3n = s3n &+ m3
    s4x = s4x &+ m4 &* xx
    s4y = s4y &+ m4 &* yy
    s4n = s4n &+ m4
    s5x = s5x &+ m5 &* xx
    s5y = s5y &+ m5 &* yy
    s5n = s5n &+ m5
    s6x = s6x &+ m6 &* xx
    s6y = s6y &+ m6 &* yy
    s6n = s6n &+ m6
    s7x = s7x &+ m7 &* xx
    s7y = s7y &+ m7 &* yy
    s7n = s7n &+ m7
    j = j &- 1_u32
  end
  # leaves bound to locals first: `St::Sn(St::Sl(x), ...)` (an enum constructor call as an argument
  # of another) does not parse (AL0101 "expected type"), see README compiler issues
  q0 = St::Sl(s0x, s0y, s0n)
  q1 = St::Sl(s1x, s1y, s1n)
  q2 = St::Sl(s2x, s2y, s2n)
  q3 = St::Sl(s3x, s3y, s3n)
  q4 = St::Sl(s4x, s4y, s4n)
  q5 = St::Sl(s5x, s5y, s5n)
  q6 = St::Sl(s6x, s6y, s6n)
  q7 = St::Sl(s7x, s7y, s7n)
  n01 = St::Sn(q0, q1)
  n23 = St::Sn(q2, q3)
  n45 = St::Sn(q4, q5)
  n67 = St::Sn(q6, q7)
  n03 = St::Sn(n01, n23)
  n47 = St::Sn(n45, n67)
  St::Sn(n03, n47)
end

def pfold(d : Int32, i : UInt32, c0 : UInt32, c1 : UInt32, c2 : UInt32, c3 : UInt32,
          c4 : UInt32, c5 : UInt32, c6 : UInt32, c7 : UInt32) : St
  return chunk(i, c0, c1, c2, c3, c4, c5, c6, c7) if d == 0
  a, b = pfold(d - 1, i, c0, c1, c2, c3, c4, c5, c6, c7), pfold(d - 1, i &+ (64_u32 << (d - 1)), c0, c1, c2, c3, c4, c5, c6, c7)
  szip(a, b)
end

# new centroid from a stats leaf: sum / count, an empty cluster keeps the old one
def cnew(s : St, old : UInt32) : UInt32
  case s
  when St::Sl(sx, sy, n)
    n == 0_u32 ? old : (sx / n) | ((sy / n) << 16)
  else old
  end
end

# the stats leaf at path p (3 bits, left = 0) of a complete Sn^3 tree; nil off the shape
def leaf(t : St, p : Int32, depth : Int32) : St?
  return t if depth == 0
  case t
  when St::Sn(l, r) then leaf((p >> (depth - 1)) & 1 == 0 ? l : r, p, depth - 1)
  else nil
  end
end

def mix(c : Array(UInt32)) : UInt32
  m = c[0]
  k = 1
  while k < 8
    m = m &* 2654435761_u32 &+ c[k]
    k += 1
  end
  m
end

# one restart: 8 centroids from the restart index, 20 Lloyd rounds, mixed checksum
def rst(r : UInt32, d : Int32) : UInt32
  c = Array(UInt32).new(8)
  k = 0
  while k < 8
    hh = prng(12345_u32 &+ r &* 8_u32 &+ (k + 1).to_u32)
    c[k] = (hh & 1023_u32) | (((hh >> 16) & 1023_u32) << 16)
    k += 1
  end
  round = 0
  while round < 20
    t = pfold(d - 6, 0_u32, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7])
    # step.fin: only a complete Sn^3 tree updates the centroids (it always is one)
    full = true
    k = 0
    while k < 8
      full = false if leaf(t, k, 3) == nil
      k += 1
    end
    if full
      k = 0
      while k < 8
        q = leaf(t, k, 3)
        c[k] = cnew(q.not_nil!, c[k]) if q != nil
        k += 1
      end
    end
    round += 1
  end
  mix(c)
end

def rbatch(b : Int32, r : UInt32, d : Int32) : UInt32
  return rst(r, d) if b == 0
  x, y = rbatch(b - 1, r, d), rbatch(b - 1, r &+ (1_u32 << (b - 1)), d)
  x &+ y
end

size = ARGV.size > 0 ? ARGV[0].to_i : 19
puts rbatch(6, 0_u32, size)
