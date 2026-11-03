# kmeans, CM shape (ADR 0039; not the same algorithm as alice/kmeans.al): the same 64 restarts x 20 Lloyd
# rounds over 2^SIZE prng points, K = 8, same point hash, branchless argmin and centroid update, but one round
# is a counted loop whose body is a combine: per point, its centroid index b and three
# `t[b] = t[b] &+ v` updates (sum x, sum y, count). The compiler lowers it to the runtime kernels
# (the map pass materializes b, x, y; Cm.ScatterAdd / Cm.Histogram merge private tables in a fixed order),
# instead of the 1:1 port's 64-point leaf chunks with 24 scalar accumulators and stats trees.
# Restarts still fork (rbatch). Big (19): 1616398086.

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

def nearest(xx : UInt32, yy : UInt32, c : Array(UInt32)) : Int32
  (min2(min2(min2(dst(xx, yy, c[0], 0_u32), dst(xx, yy, c[1], 1_u32)),
             min2(dst(xx, yy, c[2], 2_u32), dst(xx, yy, c[3], 3_u32))),
        min2(min2(dst(xx, yy, c[4], 4_u32), dst(xx, yy, c[5], 5_u32)),
             min2(dst(xx, yy, c[6], 6_u32), dst(xx, yy, c[7], 7_u32)))) & 7_u32).to_i32
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
  np = 1 << d
  sx = Array(UInt32).new(8)
  sy = Array(UInt32).new(8)
  sn = Array(UInt32).new(8)
  round = 0
  while round < 20
    k = 0
    while k < 8
      sx[k] = 0_u32
      sy[k] = 0_u32
      sn[k] = 0_u32
      k += 1
    end
    # points 1 .. 2^d (Bend's leaf chunks cover i + 1 .. i + 64)
    for p in 0...np
      hh = prng((p + 1).to_u32 &* 2654435761_u32)
      xx = hh & 1023_u32
      yy = (hh >> 16) & 1023_u32
      b = nearest(xx, yy, c)
      sx[b] = sx[b] &+ xx
      sy[b] = sy[b] &+ yy
      sn[b] = sn[b] &+ 1_u32
    end
    k = 0
    while k < 8
      c[k] = (sx[k] / sn[k]) | ((sy[k] / sn[k]) << 16) if sn[k] != 0_u32
      k += 1
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
