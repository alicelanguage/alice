# symreg: Bend 2 runtime suite (bench/runtime/symreg), 1:1 with main.c / main.bend.
# Sum-type (ADT) 1:1 port (Workstream A gate, ADR 0039); the fastest shape is idiomatic/symreg.al.
# Each of 2^18 candidates materializes a depth-5 expression AST (63 nodes) from the
# xorshift32 stream, the recursive interpreter evaluates it on PTS points, a size
# penalty is added, the tournament folds up the fork tree and the winner hill-climbs.
# Expr is a sum type (boxed nodes, as Bend's Expr). Big (18, 110): 2383953211.
enum Expr
  Var
  Lit(v : UInt32)
  Add(a : Expr, b : Expr)
  Sub(a : Expr, b : Expr)
  Mul(a : Expr, b : Expr)
  Xor(a : Expr, b : Expr)
end

record struct Sel(fit : UInt32, seed : UInt32, sum : UInt32)

def word_prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def expr_node(o : UInt32, a : Expr, b : Expr) : Expr
  case o
  when 0_u32 then Expr::Add(a, b)
  when 1_u32 then Expr::Sub(a, b)
  when 2_u32 then Expr::Mul(a, b)
  else Expr::Xor(a, b)
  end
end

def expr_gen(d : UInt32, h : UInt32) : Expr
  if d == 0_u32
    return ((h >> 8) & 1_u32) == 0_u32 ? Expr::Var : Expr::Lit(h & 255_u32)
  end
  a = expr_gen(d &- 1_u32, word_prng(h ^ 2654435761_u32))
  b = expr_gen(d &- 1_u32, word_prng(h &+ 340573321_u32))
  expr_node(h % 4_u32, a, b)
end

def expr_eval(e : Expr, x : UInt32) : UInt32
  case e
  when Expr::Var then x
  when Expr::Lit(v) then v
  when Expr::Add(a, b) then expr_eval(a, x) &+ expr_eval(b, x)
  when Expr::Sub(a, b) then expr_eval(a, x) &- expr_eval(b, x)
  when Expr::Mul(a, b) then expr_eval(a, x) &* expr_eval(b, x)
  when Expr::Xor(a, b) then expr_eval(a, x) ^ expr_eval(b, x)
  end
end

def expr_size(e : Expr) : UInt32
  case e
  when Expr::Var then 1_u32
  when Expr::Lit(v) then 1_u32
  when Expr::Add(a, b) then 1_u32 &+ (expr_size(a) &+ expr_size(b))
  when Expr::Sub(a, b) then 1_u32 &+ (expr_size(a) &+ expr_size(b))
  when Expr::Mul(a, b) then 1_u32 &+ (expr_size(a) &+ expr_size(b))
  when Expr::Xor(a, b) then 1_u32 &+ (expr_size(a) &+ expr_size(b))
  end
end

def fitness_loop(j0 : UInt32, e : Expr, acc0 : UInt32) : UInt32
  j = j0
  acc = acc0
  while j != 0_u32
    x = j &- 1_u32
    p = expr_eval(e, x)
    t = x &* x &+ (3_u32 &* x &+ 7_u32)
    acc = acc &+ (p < t ? t &- p : p &- t)
    j = x
  end
  acc &+ expr_size(e) &* 8_u32
end

def candidate_evaluate(s : UInt32, pts : UInt32) : Sel
  t = expr_gen(5_u32, word_prng(s))
  f = fitness_loop(pts, t, 0_u32)
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
