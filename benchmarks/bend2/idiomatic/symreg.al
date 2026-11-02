# symreg: Bend 2 runtime suite (bench/runtime/symreg), 1:1 with main.c / main.bend.
# Each of 2^18 candidates materializes a depth-5 expression AST (63 nodes) from the
# xorshift32 stream, the recursive interpreter evaluates it on PTS points, a size
# penalty is added, the tournament folds up the fork tree and the winner hill-climbs.
# Flat shape: the full depth-5 AST lives in a 63-slot array. Big (18, 110): 2383953211.
# Sum-type (ADT) 1:1 port of this workload: alice/symreg.al.
record struct Sel(fit : UInt32, seed : UInt32, sum : UInt32)

def word_prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

# The candidate AST is always a full depth-5 tree, so it lives in a fresh 63-slot array in
# heap order (children of k at 2k+1, 2k+2): slots 0..30 hold the operator (h % 4), slots
# 31..62 a leaf (256 = Var, else the literal). Generation fills it in that order from the
# same hash stream; eval and size are the same recursive interpreters over slot indices.
def expr_eval(e : Array(UInt32), k : Int32, x : UInt32) : UInt32
  v = e[k]
  return v == 256_u32 ? x : v if k >= 31
  a = expr_eval(e, 2 * k + 1, x)
  b = expr_eval(e, 2 * k + 2, x)
  case v
  when 0_u32 then a &+ b
  when 1_u32 then a &- b
  when 2_u32 then a &* b
  else a ^ b
  end
end

def expr_size(e : Array(UInt32), k : Int32) : UInt32
  return 1_u32 if k >= 31
  1_u32 &+ (expr_size(e, 2 * k + 1) &+ expr_size(e, 2 * k + 2))
end

def candidate_evaluate(s : UInt32, pts : UInt32) : Sel
  e = Array(UInt32).new(63)
  hs = Array(UInt32).new(63)
  hs[0] = word_prng(s)
  for k in 0...63
    h = hs[k]
    if k < 31
      e[k] = h % 4_u32
      hs[2 * k + 1] = word_prng(h ^ 2654435761_u32)
      hs[2 * k + 2] = word_prng(h &+ 340573321_u32)
    else
      e[k] = ((h >> 8) & 1_u32) == 0_u32 ? 256_u32 : h & 255_u32
    end
  end
  acc = 0_u32
  j = pts
  while j != 0_u32
    x = j &- 1_u32
    p = expr_eval(e, 0, x)
    t = x &* x &+ (3_u32 &* x &+ 7_u32)
    acc = acc &+ (p < t ? t &- p : p &- t)
    j = x
  end
  f = acc &+ expr_size(e, 0) &* 8_u32
  Sel.new(f, s, f ^ (s &* 2654435761_u32))
end

def batch_run(d : UInt32, s : UInt32, pts : UInt32) : Sel
  return candidate_evaluate(word_prng(s), pts) if d == 0_u32
  a, b = batch_run(d &- 1_u32, s &* 1664525_u32 &+ 1_u32, pts), batch_run(d &- 1_u32, s &* 214013_u32 &+ 3_u32, pts)
  w = a.fit < b.fit
  Sel.new(w ? a.fit : b.fit, w ? a.seed : b.seed, a.sum &+ b.sum)
end

def seed_climb(r0 : UInt32, bs0 : UInt32, bf0 : UInt32, pts : UInt32) : UInt32
  r = r0
  bs = bs0
  bf = bf0
  while r != 0_u32
    cn = candidate_evaluate(word_prng(bs ^ (r &* 40503_u32)), pts)
    if cn.fit < bf
      bs = cn.seed
      bf = cn.fit
    end
    r = r &- 1_u32
  end
  bf ^ (bs &* 2654435761_u32)
end

d = ARGV.size > 0 ? ARGV[0].to_i.to_u32 : 18_u32
pts = 110_u32
t = batch_run(d, 42_u32, pts)
puts seed_climb(32_u32, t.seed, t.fit, pts) &+ t.sum
