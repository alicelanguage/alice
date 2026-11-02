# nbody: Bend 2 runtime suite (bench/runtime/nbody), 1:1 with main.c / main.bend.
# 2^(SY+3) independent seeded three-body systems, f32, Plummer-softened symplectic
# Euler (dt = 0.001), ST steps each; each fork leaf digests 8 systems into an 8-bucket
# bound-energy histogram + position hash, histograms merge pairwise up the fork.
# @[StrictMath]: no a*b+c contraction, so the f32 bits equal Bend's (and C built with
# -ffp-contract=off). Big (SY=17, ST=300): 3516450380.
record struct Hs(h0 : UInt32, h1 : UInt32, h2 : UInt32, h3 : UInt32, h4 : UInt32, h5 : UInt32, h6 : UInt32, h7 : UInt32, cs : UInt32)
record struct V2(cs : UInt32, eb : UInt32)

def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

# F32.to_u32: truncate toward zero; NaN, negative, < 1 or >= 2^32 -> 0
def f2u(f : Float32) : UInt32
  return 0_u32 unless f >= 1.0_f32
  return 0_u32 if f >= 4294967296.0_f32
  f.to_u32
end

# MathF.sqrt (unqualified) is a trusted pure static: `x.to_f64.sqrt.to_f32` cost 1.9x on nbody,
# and `System.MathF.Sqrt` is not trusted (the forks disappear). See NOTES-bench2.md.
def fsqrt(x : Float32) : Float32
  MathF.sqrt(x)
end

def b2u(b : Bool) : UInt32
  b ? 1_u32 : 0_u32
end

# loop3: ST integration steps over the 21 f32 state registers, then the digest
@[StrictMath]
def loop3(s0 : UInt32, ix0 : Float32, iy0 : Float32, iz0 : Float32, ix1 : Float32, iy1 : Float32, iz1 : Float32,
          ix2 : Float32, iy2 : Float32, iz2 : Float32, ivx0 : Float32, ivy0 : Float32, ivz0 : Float32,
          ivx1 : Float32, ivy1 : Float32, ivz1 : Float32, ivx2 : Float32, ivy2 : Float32, ivz2 : Float32,
          m0 : Float32, m1 : Float32, m2 : Float32) : V2
  s = s0
  x0 = ix0
  y0 = iy0
  z0 = iz0
  x1 = ix1
  y1 = iy1
  z1 = iz1
  x2 = ix2
  y2 = iy2
  z2 = iz2
  vx0 = ivx0
  vy0 = ivy0
  vz0 = ivz0
  vx1 = ivx1
  vy1 = ivy1
  vz1 = ivz1
  vx2 = ivx2
  vy2 = ivy2
  vz2 = ivz2
  while s != 0_u32
    ax = x1 - x0
    ay = y1 - y0
    az = z1 - z0
    da = (ax * ax + (ay * ay + az * az)) + 0.05_f32
    ia = 1.0_f32 / fsqrt(da)
    i3a = (ia * ia) * ia
    qax = ax * i3a
    qay = ay * i3a
    qaz = az * i3a
    bx = x2 - x0
    by = y2 - y0
    bz = z2 - z0
    db = (bx * bx + (by * by + bz * bz)) + 0.05_f32
    ib = 1.0_f32 / fsqrt(db)
    i3b = (ib * ib) * ib
    qbx = bx * i3b
    qby = by * i3b
    qbz = bz * i3b
    cx = x2 - x1
    cy = y2 - y1
    cz = z2 - z1
    dc = (cx * cx + (cy * cy + cz * cz)) + 0.05_f32
    ic = 1.0_f32 / fsqrt(dc)
    i3c = (ic * ic) * ic
    qcx = cx * i3c
    qcy = cy * i3c
    qcz = cz * i3c
    vx0 = vx0 + (qax * m1 + qbx * m2) * 0.001_f32
    vy0 = vy0 + (qay * m1 + qby * m2) * 0.001_f32
    vz0 = vz0 + (qaz * m1 + qbz * m2) * 0.001_f32
    vx1 = vx1 + (qcx * m2 - qax * m0) * 0.001_f32
    vy1 = vy1 + (qcy * m2 - qay * m0) * 0.001_f32
    vz1 = vz1 + (qcz * m2 - qaz * m0) * 0.001_f32
    vx2 = vx2 - (qbx * m0 + qcx * m1) * 0.001_f32
    vy2 = vy2 - (qby * m0 + qcy * m1) * 0.001_f32
    vz2 = vz2 - (qbz * m0 + qcz * m1) * 0.001_f32
    x0 = x0 + vx0 * 0.001_f32
    y0 = y0 + vy0 * 0.001_f32
    z0 = z0 + vz0 * 0.001_f32
    x1 = x1 + vx1 * 0.001_f32
    y1 = y1 + vy1 * 0.001_f32
    z1 = z1 + vz1 * 0.001_f32
    x2 = x2 + vx2 * 0.001_f32
    y2 = y2 + vy2 * 0.001_f32
    z2 = z2 + vz2 * 0.001_f32
    s = s &- 1_u32
  end
  s0k = vx0 * vx0 + (vy0 * vy0 + vz0 * vz0)
  s1k = vx1 * vx1 + (vy1 * vy1 + vz1 * vz1)
  s2k = vx2 * vx2 + (vy2 * vy2 + vz2 * vz2)
  k0 = (0.5_f32 * m0) * s0k
  k1 = (0.5_f32 * m1) * s1k
  k2 = (0.5_f32 * m2) * s2k
  ke = k0 + (k1 + k2)
  ex = x1 - x0
  ey = y1 - y0
  ez = z1 - z0
  ea = (ex * ex + (ey * ey + ez * ez)) + 0.05_f32
  fx = x2 - x0
  fy = y2 - y0
  fz = z2 - z0
  eb2 = (fx * fx + (fy * fy + fz * fz)) + 0.05_f32
  gx = x2 - x1
  gy = y2 - y1
  gz = z2 - z1
  ec = (gx * gx + (gy * gy + gz * gz)) + 0.05_f32
  pa = (m0 * m1) / fsqrt(ea)
  pb = (m0 * m2) / fsqrt(eb2)
  pc = (m1 * m2) / fsqrt(ec)
  pe = pa + (pb + pc)
  e = ke - pe
  nb = f2u(0.0_f32 - e)
  eb = nb > 7_u32 ? 7_u32 : nb
  w0 = f2u((x0 + 8.0_f32) * 65536.0_f32) &* 31_u32 &+ f2u((y0 + 8.0_f32) * 65536.0_f32)
  w1 = f2u((x1 + 8.0_f32) * 65536.0_f32) &* 31_u32 &+ f2u((y1 + 8.0_f32) * 65536.0_f32)
  w2 = f2u((x2 + 8.0_f32) * 65536.0_f32) &* 31_u32 &+ f2u((y2 + 8.0_f32) * 65536.0_f32)
  w3 = f2u((z0 + 8.0_f32) * 65536.0_f32) &* 31_u32 &+ f2u((z1 + 8.0_f32) * 65536.0_f32)
  w4 = w0 &* 2654435761_u32 &+ w1
  w5 = w4 &* 2654435761_u32 &+ w2
  w6 = w5 &* 2654435761_u32 &+ w3
  V2.new(w6 &* 2654435761_u32 &+ f2u((z2 + 8.0_f32) * 65536.0_f32), eb)
end

def coord(a : UInt32) : Float32
  (a & 65535_u32).to_f32 / 32768.0_f32 - 1.0_f32
end

def mass(a : UInt32) : Float32
  (a & 65535_u32).to_f32 / 65536.0_f32 + 0.5_f32
end

def seed(sd : UInt32, k : UInt32) : UInt32
  prng((sd &* 12_u32 &+ k) &* 2654435761_u32)
end

# one random system: three seeded bodies, ST steps, digest
@[StrictMath]
def sim(sd : UInt32, st : UInt32) : V2
  x0 = coord(seed(sd, 1_u32))
  y0 = coord(seed(sd, 2_u32))
  z0 = coord(seed(sd, 3_u32))
  m0 = mass(seed(sd, 4_u32))
  x1 = coord(seed(sd, 5_u32))
  y1 = coord(seed(sd, 6_u32))
  z1 = coord(seed(sd, 7_u32))
  m1 = mass(seed(sd, 8_u32))
  x2 = coord(seed(sd, 9_u32))
  y2 = coord(seed(sd, 10_u32))
  z2 = coord(seed(sd, 11_u32))
  m2 = mass(seed(sd, 12_u32))
  loop3(st, x0, y0, z0, x1, y1, z1, x2, y2, z2,
        y0 * 0.1_f32, 0.0_f32 - x0 * 0.1_f32, 0.0_f32,
        y1 * 0.1_f32, 0.0_f32 - x1 * 0.1_f32, 0.0_f32,
        y2 * 0.1_f32, 0.0_f32 - x2 * 0.1_f32, 0.0_f32, m0, m1, m2)
end

# fork leaf: 8 systems (j = 8 .. 1, sy = i + j - 1), histogram + weighted digest
def chunk(i : UInt32, st : UInt32) : Hs
  h0 = 0_u32
  h1 = 0_u32
  h2 = 0_u32
  h3 = 0_u32
  h4 = 0_u32
  h5 = 0_u32
  h6 = 0_u32
  h7 = 0_u32
  cs = 0_u32
  j = 8_u32
  while j != 0_u32
    p = j &- 1_u32
    sy = i &+ p
    w = sim(sy, st)
    bb = w.eb
    h0 = h0 &+ b2u(bb == 0_u32)
    h1 = h1 &+ b2u(bb == 1_u32)
    h2 = h2 &+ b2u(bb == 2_u32)
    h3 = h3 &+ b2u(bb == 3_u32)
    h4 = h4 &+ b2u(bb == 4_u32)
    h5 = h5 &+ b2u(bb == 5_u32)
    h6 = h6 &+ b2u(bb == 6_u32)
    h7 = h7 &+ b2u(bb == 7_u32)
    cs = cs &+ w.cs &* (sy &* 2654435761_u32 &+ 1_u32)
    j = p
  end
  Hs.new(h0, h1, h2, h3, h4, h5, h6, h7, cs)
end

def batch(d : Int32, i : UInt32, st : UInt32) : Hs
  return chunk(i, st) if d == 0
  a, b = batch(d - 1, i, st), batch(d - 1, i &+ (8_u32 << (d - 1)), st)
  Hs.new(a.h0 &+ b.h0, a.h1 &+ b.h1, a.h2 &+ b.h2, a.h3 &+ b.h3, a.h4 &+ b.h4, a.h5 &+ b.h5, a.h6 &+ b.h6, a.h7 &+ b.h7, a.cs &+ b.cs)
end

def run_fin(t : Hs) : UInt32
  g1 = t.h0 &* 2654435761_u32 &+ t.h1
  g2 = g1 &* 2654435761_u32 &+ t.h2
  g3 = g2 &* 2654435761_u32 &+ t.h3
  g4 = g3 &* 2654435761_u32 &+ t.h4
  g5 = g4 &* 2654435761_u32 &+ t.h5
  g6 = g5 &* 2654435761_u32 &+ t.h6
  g7 = g6 &* 2654435761_u32 &+ t.h7
  g7 &* 2654435761_u32 &+ t.cs
end

sy = ARGV.size > 0 ? ARGV[0].to_i : 17
puts run_fin(batch(sy, 0_u32, 300_u32))
