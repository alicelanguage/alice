# tree-radix, flat shape: the same trie sort as main.bend (24-level radix chain per key,
# tries merged pairwise up the 2^22-leaf gen tree, trie walked back into a Concat tree,
# chk fold), with every trie stored as its preorder token stream (0 Free, 1 Busy,
# 2 Node) in a fresh UInt8 array. merge walks both streams once with a pending-subtree
# counter (no stack): Free takes the other side, Busy absorbs it, Node/Node descends.
# The gen tree is perfect, so it is the array of its leaf keys; to_map forks over it
# like Bend's to_map down to 65536-key subtrees (best of 2^12..2^16), which run C's arena algorithm in one
# method (to_map_local). to_arr and chk are single passes over the streams (they do not
# fork here); chk's join is associative, so a left fold equals the tree fold.
# Big (22): 1998173798.
# Sum-type (ADT) 1:1 port of this workload: alice/tree-radix.al.

def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def key(i : UInt32) : UInt32
  prng((i &+ 1_u32) &* 2654435761_u32) & 16777215_u32
end

def merge(a : Array(UInt8), b : Array(UInt8)) : Array(UInt8)
  out = Array(UInt8).new(a.size + b.size)
  i = 0
  j = 0
  o = 0
  pending = 1
  while pending > 0
    ta = a[i]
    tb = b[j]
    if ta == 2_u8 && tb == 2_u8
      out[o] = 2_u8
      o += 1
      i += 1
      j += 1
      pending += 1
    elsif ta == 0_u8 || tb == 0_u8
      # Free on one side: the other subtree is the result (copied)
      if ta == 0_u8
        i += 1
        need = 1
        while need > 0
          t = b[j]
          j += 1
          out[o] = t
          o += 1
          need += t == 2_u8 ? 1 : -1
        end
      else
        j += 1
        need = 1
        while need > 0
          t = a[i]
          i += 1
          out[o] = t
          o += 1
          need += t == 2_u8 ? 1 : -1
        end
      end
      pending -= 1
    else
      # Busy on one side: Busy, the other subtree is dropped
      out[o] = 1_u8
      o += 1
      if ta == 1_u8
        i += 1
        need = 1
        while need > 0
          need += b[j] == 2_u8 ? 1 : -1
          j += 1
        end
      else
        j += 1
        need = 1
        while need > 0
          need += a[i] == 2_u8 ? 1 : -1
          i += 1
        end
      end
      pending -= 1
    end
  end
  r = Array(UInt8).new(o)
  for k in 0...o
    r[k] = out[k]
  end
  r
end

# to_map of the gen subtree keys[lo, lo + n) for small n, inside one method so it stays
# pure: C's arena algorithm. Nodes are (l, r) pairs in a fresh UInt32 arena (refs 0 Free,
# 1 Busy, >= 2 node index); every key becomes its 24-node chain, tries merge pairwise in
# the gen tree's order with an explicit stack, merge reuses the left node in place.
# The result leaves as a preorder token stream for the forked merges above it.
def to_map_local(keys : Array(UInt32), lo : Int32, n : Int32) : Array(UInt8)
  nodes = Array(UInt32).new(2 * (n * 24 + 2))
  nxt = 2
  roots = Array(UInt32).new(n)
  for t in 0...n
    v = keys[lo + t]
    r = 1_u32
    k = 1_u32
    for lv in 0...24
      idx = nxt
      nxt += 1
      if (v & k) == 0_u32
        nodes[2 * idx] = r
        nodes[2 * idx + 1] = 0_u32
      else
        nodes[2 * idx] = 0_u32
        nodes[2 * idx + 1] = r
      end
      r = idx.to_u32
      k = k &* 2_u32
    end
    roots[t] = r
  end
  sx = Array(UInt32).new(128)
  sy = Array(UInt32).new(128)
  sd = Array(Int32).new(128)
  width = n
  while width > 1
    for t in 0...(width / 2)
      res = 0_u32
      sp = 1
      sx[0] = roots[2 * t]
      sy[0] = roots[2 * t + 1]
      sd[0] = -1
      while sp > 0
        sp -= 1
        x = sx[sp]
        y = sy[sp]
        dst = sd[sp]
        v = 0_u32
        if x == 0_u32
          v = y
        elsif x == 1_u32 || y == 1_u32
          v = 1_u32
        elsif y == 0_u32
          v = x
        else
          xi = x.to_i32
          yi = y.to_i32
          sx[sp] = nodes[2 * xi + 1]
          sy[sp] = nodes[2 * yi + 1]
          sd[sp] = 2 * xi + 1
          sx[sp + 1] = nodes[2 * xi]
          sy[sp + 1] = nodes[2 * yi]
          sd[sp + 1] = 2 * xi
          sp += 2
          v = x
        end
        if dst < 0
          res = v
        else
          nodes[dst] = v
        end
      end
      roots[t] = res
    end
    width = width / 2
  end
  # preorder token stream of the merged trie
  tmp = Array(UInt8).new(2 * nxt + 1)
  st = Array(UInt32).new(64)
  o = 0
  sp = 1
  st[0] = roots[0]
  while sp > 0
    sp -= 1
    r = st[sp]
    if r < 2_u32
      tmp[o] = r.to_u8
      o += 1
    else
      tmp[o] = 2_u8
      o += 1
      ri = r.to_i32
      st[sp] = nodes[2 * ri + 1]
      st[sp + 1] = nodes[2 * ri]
      sp += 2
    end
  end
  out = Array(UInt8).new(o)
  for k in 0...o
    out[k] = tmp[k]
  end
  out
end

def to_map(keys : Array(UInt32), lo : Int32, n : Int32) : Array(UInt8)
  return to_map_local(keys, lo, n) if n <= 65536
  h = n / 2
  l, r = to_map(keys, lo, h), to_map(keys, lo + h, h)
  merge(l, r)
end

# to_arr: Free -> Emp (0xFFFFFFFF), Busy at path k -> Single(k) (k), Node -> Concat (0xFFFFFFFE)
def to_arr(m : Array(UInt8)) : Array(UInt32)
  out = Array(UInt32).new(m.size)
  ks = Array(UInt32).new(26)
  sp = 0
  k = 0_u32
  for i in 0...m.size
    t = m[i]
    if t == 2_u8
      out[i] = 4294967294_u32
      ks[sp] = k &* 2_u32 &+ 1_u32
      sp += 1
      k = k &* 2_u32
    else
      out[i] = t == 1_u8 ? k : 4294967295_u32
      if sp > 0
        sp -= 1
        k = ks[sp]
      end
    end
  end
  out
end

def chk_out(a : Array(UInt32)) : UInt32
  empty = true
  lo = 0_u32
  hi = 0_u32
  ok = 0_u32
  cnt = 0_u32
  sum = 0_u32
  for t in a
    if t < 16777216_u32
      if empty
        empty = false
        lo = t
        hi = t
        ok = 1_u32
        cnt = 1_u32
        sum = t
      else
        ok = ok & (hi < t ? 1_u32 : 0_u32)
        hi = t
        cnt = cnt &+ 1_u32
        sum = sum &+ t
      end
    end
  end
  return 0_u32 if empty
  ((sum &+ cnt &* 2654435761_u32) ^ (hi &+ lo &* 340573321_u32)) &+ ok &* 2246822519_u32
end

def gen(n : Int32) : Array(UInt32)
  keys = Array(UInt32).new(1 << n)
  for p in 0...(1 << n)
    keys[p] = key(p.to_u32)
  end
  keys
end

n = ARGV.size > 0 ? ARGV[0].to_i : 22
keys = gen(n)
puts chk_out(to_arr(to_map(keys, 0, keys.size)))
