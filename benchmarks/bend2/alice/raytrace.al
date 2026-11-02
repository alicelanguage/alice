# raytrace: Bend 2 runtime suite (bench/runtime/raytrace), 1:1 with main.c / main.bend.
# 9 spheres + one point light, 2x2 supersampled pixels, nearest hit (spheres folded
# 8 down to 0 with strict <), Lambertian shading with a shadow probe, mirror bounce on
# a countdown depth of 4; all f32. Fork tree as Bend: rowf forks 2^12 rows, each row
# leaf runs colf, which forks 2^14 permuted columns (x < width renders a pixel).
# @[StrictMath]: no a*b+c contraction, so the f32 bits equal Bend's (C built with
# -ffp-contract=off). The sphere table is a flat Float32 array (x, y, z, r, kr) * 9
# passed down the fork (a read-only parameter keeps the methods pure).
# Big (ROWS=12, WIDTH=6000): 1924309504.

# MathF.sqrt (unqualified) is a trusted pure static: `x.to_f64.sqrt.to_f32` cost 1.9x on nbody,
# and `System.MathF.Sqrt` is not trusted (the forks disappear). See NOTES-bench2.md.
def fsqrt(x : Float32) : Float32
  MathF.sqrt(x)
end

# ray-sphere: t of the near root, or 1e9 for a miss (disc < 0 or t < eps)
@[StrictMath]
def sphere_isect(sp : Array(Float32), i : Int32, ox : Float32, oy : Float32, oz : Float32, dx : Float32, dy : Float32, dz : Float32) : Float32
  k = i * 5
  px = ox - sp[k]
  py = oy - sp[k + 1]
  pz = oz - sp[k + 2]
  r = sp[k + 3]
  b = (px * dx + py * dy) + pz * dz
  c = ((px * px + py * py) + pz * pz) - r * r
  disc = b * b - c
  return 1000000000.0_f32 if disc < 0.0_f32
  t = (0.0_f32 - b) - fsqrt(disc)
  return 1000000000.0_f32 if t < 0.001_f32
  t
end

@[StrictMath]
def ray_trace(sp : Array(Float32), dep0 : UInt32, ox0 : Float32, oy0 : Float32, oz0 : Float32, dx0 : Float32, dy0 : Float32, dz0 : Float32) : Float32
  dep = dep0
  ox = ox0
  oy = oy0
  oz = oz0
  dx = dx0
  dy = dy0
  dz = dz0
  acc = 0.0_f32
  w = 1.0_f32
  while true
    # scene_nearest
    bt = 1000000000.0_f32
    bi = 9
    i = 9
    while i > 0
      t = sphere_isect(sp, i - 1, ox, oy, oz, dx, dy, dz)
      if t < bt
        bt = t
        bi = i - 1
      end
      i -= 1
    end
    return acc if bi == 9
    k = bi * 5
    sx = sp[k]
    sy = sp[k + 1]
    sz = sp[k + 2]
    sr = sp[k + 3]
    skr = sp[k + 4]
    hx = ox + bt * dx
    hy = oy + bt * dy
    hz = oz + bt * dz
    nx = (hx - sx) / sr
    ny = (hy - sy) / sr
    nz = (hz - sz) / sr
    lvx = -3.0_f32 - hx
    lvy = 8.0_f32 - hy
    lvz = 1.0_f32 - hz
    ll = fsqrt((lvx * lvx + lvy * lvy) + lvz * lvz)
    lx = lvx / ll
    ly = lvy / ll
    lz = lvz / ll
    dot = (nx * lx + ny * ly) + nz * lz
    df = dot < 0.0_f32 ? 0.0_f32 : dot
    sox = hx + 0.001_f32 * nx
    soy = hy + 0.001_f32 * ny
    soz = hz + 0.001_f32 * nz
    # scene_nearest_t: shadow probe
    st = 1000000000.0_f32
    j = 9
    while j > 0
      t2 = sphere_isect(sp, j - 1, sox, soy, soz, lx, ly, lz)
      st = t2 if t2 < st
      j -= 1
    end
    lum = 0.1_f32 + 0.85_f32 * (st < ll ? 0.0_f32 : df)
    return acc + w * lum if dep == 0_u32
    k2 = 2.0_f32 * ((dx * nx + dy * ny) + dz * nz)
    ndx = dx - k2 * nx
    ndy = dy - k2 * ny
    ndz = dz - k2 * nz
    acc = acc + (w * lum) * (1.0_f32 - skr)
    w = w * skr
    ox = sox
    oy = soy
    oz = soz
    dx = ndx
    dy = ndy
    dz = ndz
    dep = dep &- 1_u32
  end
  acc
end

@[StrictMath]
def ray_sub(sp : Array(Float32), fx : Float32, fy : Float32) : Float32
  dl = fsqrt((fx * fx + fy * fy) + 1.0_f32)
  ray_trace(sp, 4_u32, 0.0_f32, 0.0_f32, 0.0_f32, fx / dl, fy / dl, 1.0_f32 / dl)
end

# 2x2 supersampled pixel
def pixel_render(sp : Array(Float32), x : UInt32, y : UInt32, hw : Float32, hh : Float32) : UInt32
  xf = x.to_f32
  yf = y.to_f32
  l1 = ray_sub(sp, ((xf + 0.25_f32) - hw) / hw, (hh - (yf + 0.25_f32)) / hw)
  l2 = ray_sub(sp, ((xf + 0.75_f32) - hw) / hw, (hh - (yf + 0.25_f32)) / hw)
  l3 = ray_sub(sp, ((xf + 0.25_f32) - hw) / hw, (hh - (yf + 0.75_f32)) / hw)
  l4 = ray_sub(sp, ((xf + 0.75_f32) - hw) / hw, (hh - (yf + 0.75_f32)) / hw)
  v = ((l1 + l2) + (l3 + l4)) * 0.25_f32
  c = v < 0.0_f32 ? 0.0_f32 : (1.0_f32 < v ? 1.0_f32 : v)
  (255.0_f32 * c).to_u32
end

def colf(sp : Array(Float32), d : Int32, x : UInt32, y : UInt32, w : UInt32, hw : Float32, hh : Float32) : UInt32
  if d == 0
    xx = (x &* 2654435761_u32) & 16383_u32
    return xx < w ? pixel_render(sp, xx, y, hw, hh) : 0_u32
  end
  a, b = colf(sp, d - 1, x, y, w, hw, hh), colf(sp, d - 1, x &+ (1_u32 << (d - 1)), y, w, hw, hh)
  a &+ b
end

def rowf(sp : Array(Float32), d : Int32, y : UInt32, m : UInt32, w : UInt32, hw : Float32, hh : Float32) : UInt32
  return colf(sp, 14, 0_u32, (y &* 1588635697_u32) & m, w, hw, hh) if d == 0
  a, b = rowf(sp, d - 1, y, m, w, hw, hh), rowf(sp, d - 1, y &+ (1_u32 << (d - 1)), m, w, hw, hh)
  a &+ b
end

sp = [0.0_f32, -10001.0_f32, 5.0_f32, 10000.0_f32, 0.3_f32,
      0.0_f32, 0.0_f32, 5.0_f32, 1.0_f32, 0.7_f32,
      2.0_f32, 0.5_f32, 6.0_f32, 1.0_f32, 0.4_f32,
      -2.0_f32, 0.5_f32, 6.0_f32, 1.0_f32, 0.4_f32,
      1.0_f32, -0.6_f32, 3.5_f32, 0.4_f32, 0.9_f32,
      -1.0_f32, -0.6_f32, 3.5_f32, 0.4_f32, 0.9_f32,
      0.0_f32, 1.6_f32, 7.0_f32, 1.2_f32, 0.1_f32,
      3.5_f32, 0.2_f32, 8.0_f32, 1.0_f32, 0.6_f32,
      -3.5_f32, 0.2_f32, 8.0_f32, 1.0_f32, 0.6_f32]
rr = ARGV.size > 0 ? ARGV[0].to_i : 12
w = ARGV.size > 1 ? ARGV[1].to_i.to_u32 : 6000_u32
h = 1_u32 << rr
puts rowf(sp, rr, 0_u32, h &- 1_u32, w, (w / 2_u32).to_f32, (h / 2_u32).to_f32)
