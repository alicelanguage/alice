# gameoflife: Conway soup census over 64 * 2^SIZE random 4x4-toroidal soups packed in one UInt32
# (port of bend/bench/runtime/gameoflife). Same bit-packed step, SWAR popcount, two-probe
# classification, 64-soup leaf accumulators and census merge as main.c; the batch tree forks like
# Bend's `a b = batch(p, i, g) batch(p, i + 64 * 2^p, g)`.
struct Census
  getter pop : UInt32
  getter still : UInt32
  getter osc : UInt32
  getter mix : UInt32

  def initialize(@pop : UInt32, @still : UInt32, @osc : UInt32, @mix : UInt32)
  end
end

# r, c and pos are Int32 (shift counts are Int32 in Alice); & 3 on a two's-complement -1 gives 3,
# the same as the UInt32 wrap in main.c
inline def cell_get(board : UInt32, r : Int32, c : Int32) : UInt32
  (board >> (((r & 3) * 4 + (c & 3)) & 31)) & 1_u32
end

inline def step_cell(b : UInt32, pos : Int32) : UInt32
  r = pos >> 2
  c = pos & 3
  alive = cell_get(b, r, c)
  n = cell_get(b, r - 1, c - 1) &+ cell_get(b, r - 1, c) &+ cell_get(b, r - 1, c + 1) &+
      cell_get(b, r, c - 1) &+ cell_get(b, r, c + 1) &+
      cell_get(b, r + 1, c - 1) &+ cell_get(b, r + 1, c) &+ cell_get(b, r + 1, c + 1)
  nxt = alive == 0_u32 ? (n == 3_u32 ? 1_u32 : 0_u32) : ((n == 2_u32 || n == 3_u32) ? 1_u32 : 0_u32)
  nxt << (pos & 31)
end

inline def board_step(b : UInt32) : UInt32
  step_cell(b, 0) | step_cell(b, 1) | step_cell(b, 2) | step_cell(b, 3) |
    step_cell(b, 4) | step_cell(b, 5) | step_cell(b, 6) | step_cell(b, 7) |
    step_cell(b, 8) | step_cell(b, 9) | step_cell(b, 10) | step_cell(b, 11) |
    step_cell(b, 12) | step_cell(b, 13) | step_cell(b, 14) | step_cell(b, 15)
end

# 16-bit SWAR population count
def popcount(b : UInt32) : UInt32
  a = b &- ((b >> 1) & 21845_u32)
  c = (a & 13107_u32) &+ ((a >> 2) & 13107_u32)
  d = (c &+ (c >> 4)) & 3855_u32
  ((d &* 257_u32) >> 8) & 31_u32
end

# class code of the settled board by two probe steps: still 0, osc 1, chaos 2
def classify(b : UInt32) : UInt32
  n1 = board_step(b)
  return 0_u32 if n1 == b
  board_step(n1) == b ? 1_u32 : 2_u32
end

# leaf chunk: 64 soups folded into scalar accumulators, soup index descending as in main.c
def chunk(i : UInt32, g : UInt32) : Census
  pa = 0_u32
  sa = 0_u32
  oa = 0_u32
  mx = 0_u32
  j = 64_u32
  while j != 0_u32
    p = j &- 1_u32
    bd = ((i &+ p) &* 2654435761_u32) & 65535_u32
    n = g
    while n != 0_u32
      bd = board_step(bd)
      n = n &- 1_u32
    end
    cd = classify(bd)
    pa = pa &+ popcount(bd)
    sa = sa &+ (cd == 0_u32 ? 1_u32 : 0_u32)
    oa = oa &+ (cd == 1_u32 ? 1_u32 : 0_u32)
    mx = (mx &* 2654435761_u32) ^ bd
    j = p
  end
  Census.new(pa, sa, oa, mx)
end

def batch(d : Int32, i : UInt32, g : UInt32) : Census
  return chunk(i, g) if d == 0
  a, b = batch(d - 1, i, g), batch(d - 1, i &+ (64_u32 << (d - 1)), g)
  Census.new(a.pop &+ b.pop, a.still &+ b.still, a.osc &+ b.osc, a.mix &* 2654435761_u32 &+ b.mix)
end

size = ARGV.size > 0 ? ARGV[0].to_i : 18
gens = ARGV.size > 1 ? ARGV[1].to_i.to_u32 : 32_u32
c = batch(size, 0_u32, gens)
puts ((c.pop &* 2654435761_u32 &+ c.still) &* 2654435761_u32 &+ c.osc) &* 2654435761_u32 &+ c.mix
