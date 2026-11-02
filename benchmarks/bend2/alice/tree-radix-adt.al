# tree-radix: Bend 2 runtime suite (bench/runtime/tree-radix), 1:1 with main.c / main.bend.
# Sum-type (ADT) shape, kept for comparison; the measured fastest shape is tree-radix.al.
# gen builds a 2^22-leaf Concat tree of 24-bit keys; to_map turns each key into a
# 24-level radix trie path and merges the tries pairwise up the tree; to_arr walks the
# trie back into a sorted Concat tree; chk verifies order and folds a checksum.
# Every Bend parallel let (gen, merge, to_map, to_arr, chk) is an Alice two-call fork.
# Idiomatic shape: sum types (GC nodes); nullary variants are shared.
# Big (22): 1998173798.
enum Arr
  Emp
  Single(v : UInt32)
  Concat(l : Arr, r : Arr)
end

enum Map
  Free
  Busy
  Node(l : Map, r : Map)
end

record struct Chk(nil : Bool, lo : UInt32, hi : UInt32, ok : UInt32, cnt : UInt32, sum : UInt32)

def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def key(i : UInt32) : UInt32
  prng((i &+ 1_u32) &* 2654435761_u32) & 16777215_u32
end

def merge(a : Map, b : Map) : Map
  case a
  when Map::Free then b
  when Map::Busy then Map::Busy
  when Map::Node(al, ar)
    case b
    when Map::Free then a
    when Map::Busy then Map::Busy
    when Map::Node(bl, br)
      l, r = merge(al, bl), merge(ar, br)
      Map::Node(l, r)
    end
  end
end

def gen(n : Int32, x : UInt32) : Arr
  return Arr::Single(key(x)) if n == 0
  l, r = gen(n - 1, x &* 2_u32), gen(n - 1, x &* 2_u32 &+ 1_u32)
  Arr::Concat(l, r)
end

# 24-bit key -> trie path: bit 0 wrapped first, so bit 23 sits at the root
def radix(n : UInt32) : Map
  r : Map = Map::Busy   # annotated: inferred as the variant type Busy otherwise (AL0304)
  free : Map = Map::Free
  k = 1_u32
  i = 0
  while i < 24
    # free is a local: `Map::Node(Map::Free, r)` does not parse (AL0101, see NOTES-bench2.md)
    r = (n & k) == 0_u32 ? Map::Node(r, free) : Map::Node(free, r)
    k = k &* 2_u32
    i += 1
  end
  r
end

def to_map(a : Arr) : Map
  case a
  when Arr::Emp then Map::Free
  when Arr::Single(v) then radix(v)
  when Arr::Concat(l, r)
    ml, mr = to_map(l), to_map(r)
    merge(ml, mr)
  end
end

def to_arr(m : Map, k : UInt32) : Arr
  case m
  when Map::Free then Arr::Emp
  when Map::Busy then Arr::Single(k)
  when Map::Node(l, r)
    la, ra = to_arr(l, k &* 2_u32), to_arr(r, k &* 2_u32 &+ 1_u32)
    Arr::Concat(la, ra)
  end
end

def chk_join(a : Chk, b : Chk) : Chk
  return b if a.nil
  return a if b.nil
  Chk.new(false, a.lo, b.hi, (a.ok & b.ok) & (a.hi < b.lo ? 1_u32 : 0_u32), a.cnt &+ b.cnt, a.sum &+ b.sum)
end

def chk(a : Arr) : Chk
  case a
  when Arr::Emp then Chk.new(true, 0_u32, 0_u32, 0_u32, 0_u32, 0_u32)
  when Arr::Single(w) then Chk.new(false, w, w, 1_u32, 1_u32, w)
  when Arr::Concat(l, r)
    cl, cr = chk(l), chk(r)
    chk_join(cl, cr)
  end
end

def chk_out(c : Chk) : UInt32
  return 0_u32 if c.nil
  ((c.sum &+ c.cnt &* 2654435761_u32) ^ (c.hi &+ c.lo &* 340573321_u32)) &+ c.ok &* 2246822519_u32
end

n = ARGV.size > 0 ? ARGV[0].to_i : 22
puts chk_out(chk(to_arr(to_map(gen(n, 0_u32)), 0_u32)))
