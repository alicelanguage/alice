# editdist: Levenshtein distance by DP with two rolling rows over 2^DEPTH pairs of 256-symbol
# sequences (port of bend/bench/runtime/editdist). Same xorshift streams, min chain and checksum
# as main.c; the batch tree forks like Bend's `x y = batch(q, p) batch(q, p + 2^q)`.
def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def pair(p : UInt32) : UInt32
  a = Array(UInt32).new(256)
  b = Array(UInt32).new(256)
  s = (p &+ 1_u32) &* 2654435761_u32
  t = s
  u = s &* 340573321_u32
  k = 0
  while k < 256
    t = prng(t)
    a[k] = t & 3_u32
    u = prng(u)
    b[k] = u & 3_u32
    k += 1
  end
  # Both rows live in one array (prev at po, cur at co). Swapping two array locals
  # (`tmp = prev; prev = cur; cur = tmp`) makes pair impure to the fork gate, so batch would
  # not fork (see README, compiler issues).
  rows = Array(UInt32).new(514)
  po = 0
  co = 257
  k = 0
  while k <= 256
    rows[k] = k.to_u32
    k += 1
  end
  i = 0
  while i < 256
    ai = a[i]
    rows[co] = (i + 1).to_u32
    j = 0
    while j < 256
      cost = ai != b[j] ? 1_u32 : 0_u32
      up = rows[po + j + 1] &+ 1_u32
      left = rows[co + j] &+ 1_u32
      m = up < left ? up : left
      dg = rows[po + j] &+ cost
      rows[co + j + 1] = m < dg ? m : dg
      j += 1
    end
    t2 = po
    po = co
    co = t2
    i += 1
  end
  dist = rows[po + 256]
  (dist &* 2654435761_u32) ^ (p &+ 1_u32)
end

def batch(d : Int32, p : UInt32) : UInt32
  return pair(p) if d == 0
  x, y = batch(d - 1, p), batch(d - 1, p &+ (1_u32 << (d - 1)))
  x &+ y
end

k = ARGV.size > 0 ? ARGV[0].to_i : 15
puts batch(k, 0_u32)
