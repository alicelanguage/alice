# tree-bitonic, flat shape: the same network, recursion and fork tree as main.bend, with
# each (always perfect) subtree stored as the array of its leaves in order. warp of two
# subtrees is then [firsts of the compare-swapped leaf pairs, seconds], exactly the leaf
# order of Bend's zipped Node{Node{a0,b0},Node{a1,b1}}. Every method returns a fresh
# array, so all of them stay pure and fork; the GC sees a few large arrays instead of
# billions of nodes. Big (23): 3787129428.
# Sum-type (ADT) 1:1 port of this workload: alice/tree-bitonic.al.
record struct Stat(lo : UInt32, hi : UInt32, ok : UInt32, mx : UInt32)

def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def key(i : UInt32) : UInt32
  prng((i &+ 1_u32) &* 2654435761_u32)
end

# Below 18 levels (2^18 leaves, measured best of 10..18) a subtree is sorted or merged in place inside one method (a fresh local
# array, so the method stays pure): the same compare-swaps as the recursion, level by level.


def cswap_first(x : UInt32, y : UInt32, s : Bool) : UInt32
  s != (x > y) ? y : x
end

# warp(a, b): fresh array [firsts of the compare-swapped pairs (a[i], b[i]), seconds]
def warp(a : Array(UInt32), b : Array(UInt32), s : Bool) : Array(UInt32)
  n = a.size
  w = Array(UInt32).new(2 * n)
  for j in 0...(2 * n)
    i = j < n ? j : j - n
    x = a[i]
    y = b[i]
    sw = s != (x > y)
    w[j] = (j < n) == sw ? y : x
  end
  w
end

# warp_node of the subtree t[o, o + 2q): fresh array of 2q leaves
def warp_half(t : Array(UInt32), o : Int32, q : Int32, s : Bool) : Array(UInt32)
  w = Array(UInt32).new(2 * q)
  for j in 0...(2 * q)
    i = j < q ? j : j - q
    x = t[o + i]
    y = t[o + q + i]
    sw = s != (x > y)
    w[j] = (j < q) == sw ? y : x
  end
  w
end

# flow below 18 levels: the remaining merge levels of w, in place on a copy
def flow_small(d : Int32, s : Bool, w : Array(UInt32)) : Array(UInt32)
  n = w.size
  r = Array(UInt32).new(n)
  for i in 0...n
    r[i] = w[i]
  end
  half = n / 4
  while half >= 1
    base = 0
    while base < n
      i = base
      while i < base + half
        x = r[i]
        y = r[i + half]
        if s != (x > y)
          r[i] = y
          r[i + half] = x
        end
        i += 1
      end
      base += 2 * half
    end
    half = half / 2
  end
  r
end

# flow(d, s, w): w has 2^(d+1) leaves; both halves are warped, then flowed (forked)
def flow(d : Int32, s : Bool, w : Array(UInt32)) : Array(UInt32)
  return w if d == 0
  return flow_small(d, s, w) if d < 18
  h = w.size / 2
  q = h / 2
  wa = warp_half(w, 0, q, s)
  wb = warp_half(w, h, q, s)
  fa, fb = flow(d - 1, s, wa), flow(d - 1, s, wb)
  r = Array(UInt32).new(2 * h)
  for j in 0...(2 * h)
    r[j] = j < h ? fa[j] : fb[j - h]
  end
  r
end

# bsort below 18 levels: the whole subtree in place. Leaf p's key index follows bsort's path
# (left child 2x+1, right child 2x); a block is sorted with s = false as a left child,
# true as a right child, and the subtree's own s at the top.
def bsort_small(d : Int32, s : Bool, x : UInt32) : Array(UInt32)
  n = 1 << d
  r = Array(UInt32).new(n)
  for p in 0...n
    xi = x
    k = d - 1
    while k >= 0
      xi = ((p >> k) & 1) == 0 ? xi &* 2_u32 &+ 1_u32 : xi &* 2_u32
      k -= 1
    end
    r[p] = key(xi)
  end
  lv = 1
  while lv <= d
    size = 1 << lv
    base = 0
    while base < n
      dir = lv == d ? s : ((base >> lv) & 1) == 1
      half = size / 2
      while half >= 1
        sub = base
        while sub < base + size
          i = sub
          while i < sub + half
            a = r[i]
            b = r[i + half]
            if dir != (a > b)
              r[i] = b
              r[i + half] = a
            end
            i += 1
          end
          sub += 2 * half
        end
        half = half / 2
      end
      base += size
    end
    lv += 1
  end
  r
end

def bsort(d : Int32, s : Bool, x : UInt32) : Array(UInt32)
  return bsort_small(d, s, x) if d <= 18
  sa, sb = bsort(d - 1, false, x &* 2_u32 &+ 1_u32), bsort(d - 1, true, x &* 2_u32)
  flow(d - 1, s, warp(sa, sb, s))
end

def scan(t : Array(UInt32), o : Int32, n : Int32) : Stat
  if n == 1
    v = t[o]
    return Stat.new(v, v, 1_u32, v)
  end
  h = n / 2
  l, r = scan(t, o, h), scan(t, o + h, h)
  Stat.new(l.lo, r.hi, l.hi <= r.lo ? (l.ok & r.ok) : 0_u32, l.mx &* 2654435761_u32 &+ r.mx)
end

def stat_out(s : Stat) : UInt32
  ((s.mx &* 2654435761_u32) ^ (s.hi &+ s.lo &* 340573321_u32)) &+ s.ok &* 2246822519_u32
end

n = ARGV.size > 0 ? ARGV[0].to_i : 23
t = bsort(n, false, 0_u32)
puts stat_out(scan(t, 0, t.size))
