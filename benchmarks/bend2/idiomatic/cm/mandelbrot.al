# mandelbrot, CM shape (ADR 0039; not the same algorithm as alice/mandelbrot.al): the same 4096x4096
# escape-time render (signed 8.8 fixed point, ITERS branchless iterations) and the same checksum, with every
# pixel computed once instead of twice. One counted loop over the pixels is a combine with three targets: a
# 65536-bin histogram of escape counts, and per bucket the wrapping sum of the position hash and of the escape
# counts. A prefix sum of the histogram is the CDF; the 8-bucket LUT is read off it (remap), and the recolor
# sum follows from the per-bucket sums by distributivity (mod 2^32):
#   sum_i (q[b(i)] * w(i) + it(i)) = sum_b q[b] * W[b] + sum_b IT[b].
# Big (18, 51): 3101455856.

# arithmetic shift right by 8 over two's-complement UInt32
def asr8(v : UInt32) : UInt32
  (v.to_i32_b >> 8).to_u32_b
end

def mit(tn : UInt32, cr : UInt32, ci : UInt32) : UInt32
  zr = 0_u32
  zi = 0_u32
  esc = 0_u32
  it = 0_u32
  n = tn
  while n != 0_u32
    r2 = asr8(zr &* zr)
    i2 = asr8(zi &* zi)
    e2 = esc | (r2 &+ i2 > 1024_u32 ? 1_u32 : 0_u32)
    nzr = r2 &- i2 &+ cr
    nzi = asr8(2_u32 &* (zr &* zi)) &+ ci
    zr = e2 == 0_u32 ? nzr : zr
    zi = e2 == 0_u32 ? nzi : zi
    it = it &+ (e2 == 0_u32 ? 1_u32 : 0_u32)
    esc = e2
    n = n &- 1_u32
  end
  it
end

def pix(id : UInt32, tn : UInt32) : UInt32
  cr = (id & 4095_u32) &* 768_u32 / 4096_u32 &- 512_u32
  ci = (id >> 12) &* 768_u32 / 4096_u32 &- 384_u32
  mit(tn, cr, ci)
end

def bkt(it : UInt32, tn : UInt32) : UInt32
  bq = it &* 8_u32 / tn
  bq > 7_u32 ? 7_u32 : bq
end

def rend(hd : Int32, tn : UInt32) : UInt32
  np = 1 << (hd + 6)
  hist = Array(UInt32).new(65536)
  wsum = Array(UInt32).new(8)
  isum = Array(UInt32).new(8)
  # one pass over the pixels: a combine (histogram + two scatter-adds)
  for q in 0...np
    id = q.to_u32
    it = pix(id, tn)
    e = (it & 65535_u32).to_i32
    b = bkt(it, tn).to_i32
    hist[e] = hist[e] &+ 1_u32
    wsum[b] = wsum[b] &+ (id &* 2654435761_u32 &+ 1_u32)
    isum[b] = isum[b] &+ it
  end
  # CDF of the escape counts: a scan
  cdf = Array(UInt32).new(65536)
  acc = 0_u32
  for t in 0...65536
    acc = acc &+ hist[t]
    cdf[t] = acc
  end
  # remap: bucket k's cumulative count is the CDF at the last escape count of a bucket <= k
  c = Array(UInt32).new(8)
  k = 0
  while k < 8
    v = 0_u32
    t = 0
    while t <= tn.to_i32
      v = cdf[t] if bkt(t.to_u32, tn) <= k.to_u32
      t += 1
    end
    c[k] = v
    k += 1
  end
  m = 0_u32
  r = 0_u32
  k = 0
  while k < 8
    qk = c[k] &* 255_u32 / c[7]
    m = k == 0 ? qk : m &* 2654435761_u32 &+ qk
    r = r &+ qk &* wsum[k] &+ isum[k]
    k += 1
  end
  m &* 2654435761_u32 &+ r
end

hd = ARGV.size > 0 ? ARGV[0].to_i : 18
tn = ARGV.size > 1 ? ARGV[1].to_i.to_u32 : 51_u32
puts rend(hd, tn)
