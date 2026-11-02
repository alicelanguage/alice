# mandelbrot: histogram-equalized escape-time render, 4096x4096, signed 8.8 fixed point in UInt32,
# ITERS branchless iterations per pixel (port of bend/bench/runtime/mandelbrot). Same arithmetic,
# 64-pixel histogram chunks, CDF LUT and position-weighted recolor sum as main.c. Two fork trees, as
# in Bend: hfold over 2^HD chunks (histograms merged pairwise), rcol over every pixel (2^(HD+6) leaves).
struct Hs
  getter h0 : UInt32
  getter h1 : UInt32
  getter h2 : UInt32
  getter h3 : UInt32
  getter h4 : UInt32
  getter h5 : UInt32
  getter h6 : UInt32
  getter h7 : UInt32

  def initialize(@h0 : UInt32, @h1 : UInt32, @h2 : UInt32, @h3 : UInt32,
                 @h4 : UInt32, @h5 : UInt32, @h6 : UInt32, @h7 : UInt32)
  end
end

# arithmetic shift right by 8 over two's-complement UInt32
def asr8(v : UInt32) : UInt32
  (v.to_i32_b >> 8).to_u32_b
end

# ITERS branchless iterations: z steps while esc == 0 and freezes after, it counts pre-escape steps
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

# one pixel: viewport [-2, 1) x [-1.5, 1.5) over the 4096x4096 grid
def pix(id : UInt32, tn : UInt32) : UInt32
  cr = (id & 4095_u32) &* 768_u32 / 4096_u32 &- 512_u32
  ci = (id >> 12) &* 768_u32 / 4096_u32 &- 384_u32
  mit(tn, cr, ci)
end

# escape time -> one of 8 buckets: (it * 8) / ITERS clamped
def bkt(it : UInt32, tn : UInt32) : UInt32
  bq = it &* 8_u32 / tn
  bq > 7_u32 ? 7_u32 : bq
end

# pass-1 leaf: 64 pixels bucketed into 8 scalar counters, pixel index descending as in main.c
def hchunk(i : UInt32, tn : UInt32) : Hs
  h0 = 0_u32
  h1 = 0_u32
  h2 = 0_u32
  h3 = 0_u32
  h4 = 0_u32
  h5 = 0_u32
  h6 = 0_u32
  h7 = 0_u32
  j = 64_u32
  while j != 0_u32
    p = j &- 1_u32
    bk = bkt(pix(i &+ p, tn), tn)
    h0 = h0 &+ (bk == 0_u32 ? 1_u32 : 0_u32)
    h1 = h1 &+ (bk == 1_u32 ? 1_u32 : 0_u32)
    h2 = h2 &+ (bk == 2_u32 ? 1_u32 : 0_u32)
    h3 = h3 &+ (bk == 3_u32 ? 1_u32 : 0_u32)
    h4 = h4 &+ (bk == 4_u32 ? 1_u32 : 0_u32)
    h5 = h5 &+ (bk == 5_u32 ? 1_u32 : 0_u32)
    h6 = h6 &+ (bk == 6_u32 ? 1_u32 : 0_u32)
    h7 = h7 &+ (bk == 7_u32 ? 1_u32 : 0_u32)
    j = p
  end
  Hs.new(h0, h1, h2, h3, h4, h5, h6, h7)
end

def hfold(d : Int32, i : UInt32, tn : UInt32) : Hs
  return hchunk(i, tn) if d == 0
  a, b = hfold(d - 1, i, tn), hfold(d - 1, i &+ (64_u32 << (d - 1)), tn)
  Hs.new(a.h0 &+ b.h0, a.h1 &+ b.h1, a.h2 &+ b.h2, a.h3 &+ b.h3,
         a.h4 &+ b.h4, a.h5 &+ b.h5, a.h6 &+ b.h6, a.h7 &+ b.h7)
end

# pass-2 leaf: recompute the pixel, recolor through the LUT (masked pick), weight by a position hash
def rpix(i : UInt32, tn : UInt32, k0 : UInt32, k1 : UInt32, k2 : UInt32, k3 : UInt32,
         k4 : UInt32, k5 : UInt32, k6 : UInt32, k7 : UInt32) : UInt32
  it = pix(i, tn)
  bk = bkt(it, tn)
  col = (bk == 0_u32 ? k0 : 0_u32) &+ (bk == 1_u32 ? k1 : 0_u32) &+ (bk == 2_u32 ? k2 : 0_u32) &+
        (bk == 3_u32 ? k3 : 0_u32) &+ (bk == 4_u32 ? k4 : 0_u32) &+ (bk == 5_u32 ? k5 : 0_u32) &+
        (bk == 6_u32 ? k6 : 0_u32) &+ (bk == 7_u32 ? k7 : 0_u32)
  col &* (i &* 2654435761_u32 &+ 1_u32) &+ it
end

def rcol(d : Int32, i : UInt32, tn : UInt32, k0 : UInt32, k1 : UInt32, k2 : UInt32, k3 : UInt32,
         k4 : UInt32, k5 : UInt32, k6 : UInt32, k7 : UInt32) : UInt32
  return rpix(i, tn, k0, k1, k2, k3, k4, k5, k6, k7) if d == 0
  a, b = rcol(d - 1, i, tn, k0, k1, k2, k3, k4, k5, k6, k7), rcol(d - 1, i &+ (1_u32 << (d - 1)), tn, k0, k1, k2, k3, k4, k5, k6, k7)
  a &+ b
end

def rend(hd : Int32, tn : UInt32) : UInt32
  h = hfold(hd, 0_u32, tn)
  # histogram equalization: LUT k = cdf(k) scaled to 0..255
  c0 = h.h0
  c1 = c0 &+ h.h1
  c2 = c1 &+ h.h2
  c3 = c2 &+ h.h3
  c4 = c3 &+ h.h4
  c5 = c4 &+ h.h5
  c6 = c5 &+ h.h6
  c7 = c6 &+ h.h7
  q0 = c0 &* 255_u32 / c7
  q1 = c1 &* 255_u32 / c7
  q2 = c2 &* 255_u32 / c7
  q3 = c3 &* 255_u32 / c7
  q4 = c4 &* 255_u32 / c7
  q5 = c5 &* 255_u32 / c7
  q6 = c6 &* 255_u32 / c7
  q7 = c7 &* 255_u32 / c7
  m1 = q0 &* 2654435761_u32 &+ q1
  m2 = m1 &* 2654435761_u32 &+ q2
  m3 = m2 &* 2654435761_u32 &+ q3
  m4 = m3 &* 2654435761_u32 &+ q4
  m5 = m4 &* 2654435761_u32 &+ q5
  m6 = m5 &* 2654435761_u32 &+ q6
  m7 = m6 &* 2654435761_u32 &+ q7
  r = rcol(hd + 6, 0_u32, tn, q0, q1, q2, q3, q4, q5, q6, q7)
  m7 &* 2654435761_u32 &+ r
end

hd = ARGV.size > 0 ? ARGV[0].to_i : 18
tn = ARGV.size > 1 ? ARGV[1].to_i.to_u32 : 51_u32
puts rend(hd, tn)
