# hashmap: separate-chaining hash table over 2^TABLES independent tables of KEYS inserts and KEYS
# lookups (port of bend/bench/runtime/hashmap). Same key draw, 12-bit buckets, head insert, hit count
# and chain-length fold as main.c, with the same index-linked node pool (head / key / next arrays,
# node links as Int32 with -1 for NIL, one set per table, allocated by the table so the leaf stays pure and forkable); the batch tree forks
# like Bend's `a b = batch(p, t, d) batch(p, t + 2^p, d)`.
def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

# a key: the hash of a seed and a draw index, masked to 20 bits
def draw_key(s : UInt32, j : UInt32) : UInt32
  prng(((j &+ 1_u32) &* 2654435761_u32) ^ (s &* 340573321_u32)) & 1048575_u32
end

# the chain walk: 1 when key k is in bucket b, stopping at it
inline def has(head : Array(Int32), key : Array(UInt32), nxt : Array(Int32), b : Int32, k : UInt32) : UInt32
  n = head[b]
  while n != -1
    return 1_u32 if key[n] == k
    n = nxt[n]
  end
  0_u32
end

def table(ti : UInt32, d : Int32) : UInt32
  head = Array(Int32).new(4096, -1)
  key = Array(UInt32).new(d)
  nxt = Array(Int32).new(d)
  used = 0
  s = (ti &+ 1_u32) &* 2654435761_u32
  j = 0
  while j < d
    k = draw_key(s, j.to_u32)
    b = (k & 4095_u32).to_i32
    if has(head, key, nxt, b, k) == 0_u32
      key[used] = k
      nxt[used] = head[b]
      head[b] = used
      used += 1
    end
    j += 1
  end
  s2 = prng(s)
  hits = 0_u32
  j = 0
  while j < d
    k = draw_key(s2, j.to_u32)
    hits = hits &+ has(head, key, nxt, (k & 4095_u32).to_i32, k)
    j += 1
  end
  cnt = 0_u32
  acc = 0_u32
  i = 0
  while i < 4096
    l = 0_u32
    n = head[i]
    while n != -1
      l = l &+ 1_u32
      n = nxt[n]
    end
    cnt = cnt &+ l
    acc = (acc &* 2654435761_u32) ^ (l &* (i.to_u32 &+ 3_u32))
    i += 1
  end
  (acc ^ (hits &* 2654435761_u32)) &+ cnt &* 340573321_u32
end

def batch(p : Int32, t : UInt32, d : Int32) : UInt32
  return table(t, d) if p == 0
  a, b = batch(p - 1, t, d), batch(p - 1, t &+ (1_u32 << (p - 1)), d)
  a &+ b
end

tables = ARGV.size > 0 ? ARGV[0].to_i : 11
keys = ARGV.size > 1 ? ARGV[1].to_i : 16384
puts batch(tables, 0_u32, keys)
