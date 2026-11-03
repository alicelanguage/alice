# terrain, CM shape (ADR 0039): alice/terrain.al with the per-tile histogram as a `cm for` combine (the
# tile's 64-bin histogram), split from the position-weighted fold (a non-associative recurrence, so it stays
# a serial loop). Same tiles, sweeps and checksum. Big (16, 5): 2572468224.

def word_prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def lattice_hash(gx : UInt32, gz : UInt32) : UInt32
  word_prng((gx &* 2654435761_u32) ^ (gz &* 340573321_u32)) & 255_u32
end

def terrain_ground(x : UInt32, z : UInt32) : UInt32
  gx = (x >> 8) & 63_u32
  gz = (z >> 8) & 63_u32
  fx = x & 255_u32
  fz = z & 255_u32
  gx1 = (gx &+ 1_u32) & 63_u32
  gz1 = (gz &+ 1_u32) & 63_u32
  h00 = lattice_hash(gx, gz)
  h10 = lattice_hash(gx1, gz)
  h01 = lattice_hash(gx, gz1)
  h11 = lattice_hash(gx1, gz1)
  t0 = (h00 &* (256_u32 &- fx) &+ h10 &* fx) >> 8
  t1 = (h01 &* (256_u32 &- fx) &+ h11 &* fx) >> 8
  (t0 &* (256_u32 &- fz) &+ t1 &* fz) >> 8
end

def tile_run(t : UInt32, e : UInt32) : UInt32
  h = Array(UInt32).new(4096)
  g = Array(UInt32).new(64)
  ox = (t & 255_u32) << 6
  oz = (t >> 8) << 6
  i = 0
  while i < 4096
    iu = i.to_u32
    h[i] = terrain_ground((ox &+ (iu & 63_u32)) << 6, (oz &+ (iu >> 6)) << 6)
    i += 1
  end
  p = e
  while p != 0_u32
    salt = p &* 2654435761_u32
    for zc in 0...64
      up = zc > 0 ? -64 : 0
      dn = zc < 63 ? 64 : 0
      for xc in 0...64
        i = zc * 64 + xc
        hl = h[xc > 0 ? i - 1 : i]
        hr = h[xc < 63 ? i + 1 : i]
        rough = word_prng(i.to_u32 ^ salt) & 1_u32
        h[i] = ((h[i] &* 4_u32 &+ hl &+ hr &+ h[i + up] &+ h[i + dn]) >> 3) &+ rough
      end
    end
    p = p &- 1_u32
  end
  # tile_hist: a combine
  cm for j in 0...4096
    b = ((h[j] >> 2) & 63_u32).to_i32
    g[b] = g[b] &+ 1_u32
  end
  # hist_fold: the position-weighted recurrence
  acc = t &+ 1_u32
  i = 0
  while i < 4096
    acc = acc &* 2654435761_u32 &+ h[i] &* (i.to_u32 &+ 1_u32)
    i += 1
  end
  k = 0
  while k < 64
    acc = (acc &* 2654435761_u32) ^ (g[k] &* (k.to_u32 &+ 3_u32))
    k += 1
  end
  acc
end

def batch(d : Int32, t : UInt32, e : UInt32) : UInt32
  return tile_run(t, e) if d == 0
  a, b = batch(d - 1, t, e), batch(d - 1, t &+ (1_u32 << (d - 1)), e)
  a &+ b
end

d = ARGV.size > 0 ? ARGV[0].to_i : 16
puts batch(d, 0_u32, 5_u32)
