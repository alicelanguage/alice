# queens: Bend 2 runtime suite (bench/runtime/queens), 1:1 with main.c / main.bend.
# Rows 0-3 are a prefix decoded from a permuted fork index; the classic bitmask
# backtracker solves the remaining rows threading Stats{sols, nodes}. The fork tree
# over the 2^17 prefix index space (Bend's `a b = batch(..) batch(..)`) is the
# Alice two-call fork; Stats merge up the tree.
# Checksum = (sols * 2654435761) ^ nodes. Big (17, 17, 11730): 2063750025.
record struct Stats(sols : UInt32, nodes : UInt32)

def solve(cand0 : UInt32, cols : UInt32, ld : UInt32, rd : UInt32, full : UInt32, sols0 : UInt32, nodes0 : UInt32) : Stats
  cand = cand0
  sols = sols0
  nodes = nodes0
  while cand != 0_u32
    bit = cand & (0_u32 &- cand)
    nc = cols | bit
    if nc == full
      sols = sols &+ 1_u32
      nodes = nodes &+ 1_u32
    else
      nl = (ld | bit) << 1
      nr = (rd | bit) >> 1
      w = solve(full & ~(nc | nl | nr), nc, nl, nr, full, sols, nodes &+ 1_u32)
      sols = w.sols
      nodes = w.nodes
    end
    cand = cand &- bit
  end
  Stats.new(sols, nodes)
end

def pfx(j : UInt32, mask : UInt32, nn : UInt32, full : UInt32, bound : UInt32) : Stats
  i = (j &* 2654435761_u32) & mask
  return Stats.new(0_u32, 0_u32) unless i < bound
  nn2 = nn &* nn
  b0 = 1_u32 << (i / (nn2 &* nn)).to_i32
  b1 = 1_u32 << ((i / nn2) % nn).to_i32
  return Stats.new(0_u32, 0_u32) if (b1 & (b0 | (b0 << 1) | (b0 >> 1))) != 0_u32
  b2 = 1_u32 << ((i / nn) % nn).to_i32
  c2 = b0 | b1
  l2 = ((b0 << 1) | b1) << 1
  r2 = ((b0 >> 1) | b1) >> 1
  return Stats.new(0_u32, 0_u32) if (b2 & (c2 | l2 | r2)) != 0_u32
  b3 = 1_u32 << (i % nn).to_i32
  c3 = c2 | b2
  l3 = (l2 | b2) << 1
  r3 = (r2 | b2) >> 1
  return Stats.new(0_u32, 0_u32) if (b3 & (c3 | l3 | r3)) != 0_u32
  c4 = c3 | b3
  l4 = (l3 | b3) << 1
  r4 = (r3 | b3) >> 1
  solve(full & ~(c4 | l4 | r4), c4, l4, r4, full, 0_u32, 0_u32)
end

def batch(d : Int32, i : UInt32, mask : UInt32, nn : UInt32, full : UInt32, bound : UInt32) : Stats
  return pfx(i, mask, nn, full, bound) if d == 0
  a, b = batch(d - 1, i, mask, nn, full, bound), batch(d - 1, i &+ (1_u32 << (d - 1)), mask, nn, full, bound)
  Stats.new(a.sols &+ b.sols, a.nodes &+ b.nodes)
end

def run(d : Int32, n : UInt32, lim : UInt32) : UInt32
  full = (1_u32 << n.to_i32) &- 1_u32
  nnnn = n &* n &* n &* n
  bound = lim < nnnn ? lim : nnnn
  st = batch(d, 0_u32, (1_u32 << d) &- 1_u32, n, full, bound)
  (st.sols &* 2654435761_u32) ^ st.nodes
end

d = ARGV.size > 0 ? ARGV[0].to_i : 17
puts run(d, 17_u32, 11730_u32)
