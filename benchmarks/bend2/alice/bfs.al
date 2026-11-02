# bfs: breadth-first search over 2^DEPTH independent 32x32 grid mazes (port of bend/bench/runtime/bfs).
# Same seed, wall hash, flat UInt32 grid/queue/distance arrays, pop loop and fold as main.c;
# the batch tree forks like Bend's `a b = batch(p, i) batch(p, i + 2^p)`.
def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def maze(m : UInt32) : UInt32
  g = Array(UInt32).new(1024)
  q = Array(UInt32).new(1024)
  d = Array(UInt32).new(1024)
  s = (m &+ 1_u32) &* 2654435761_u32
  c = 0
  while c < 1024
    cu = c.to_u32
    h = prng(s ^ ((cu &+ 1_u32) &* 340573321_u32))
    g[c] = (c == 0 || h % 100_u32 >= 30_u32) ? 1_u32 : 0_u32
    d[c] = 4294967295_u32
    c += 1
  end
  d[0] = 0_u32
  head = 0
  tail = 1
  while head < tail
    cell = q[head].to_i32
    v = d[cell] &+ 1_u32
    x = cell & 31
    y = cell >> 5
    head += 1
    if x > 0 && g[cell - 1] != 0_u32 && d[cell - 1] == 4294967295_u32
      d[cell - 1] = v
      q[tail] = (cell - 1).to_u32
      tail += 1
    end
    if x < 31 && g[cell + 1] != 0_u32 && d[cell + 1] == 4294967295_u32
      d[cell + 1] = v
      q[tail] = (cell + 1).to_u32
      tail += 1
    end
    if y > 0 && g[cell - 32] != 0_u32 && d[cell - 32] == 4294967295_u32
      d[cell - 32] = v
      q[tail] = (cell - 32).to_u32
      tail += 1
    end
    if y < 31 && g[cell + 32] != 0_u32 && d[cell + 32] == 4294967295_u32
      d[cell + 32] = v
      q[tail] = (cell + 32).to_u32
      tail += 1
    end
  end
  acc = 0_u32
  cnt = 0_u32
  c = 0
  while c < 1024
    dv = d[c]
    if dv != 4294967295_u32
      acc = (acc &* 2654435761_u32) ^ (dv &* (c.to_u32 &+ 1_u32))
      cnt = cnt &+ 1_u32
    else
      acc = acc &* 2654435761_u32
    end
    c += 1
  end
  acc ^ (cnt &* 2246822519_u32)
end

def batch(k : Int32, i : UInt32) : UInt32
  return maze(i) if k == 0
  a, b = batch(k - 1, i), batch(k - 1, i &+ (1_u32 << (k - 1)))
  a &+ b
end

k = ARGV.size > 0 ? ARGV[0].to_i : 19
puts batch(k, 0_u32)
