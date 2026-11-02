# tree-bitonic: Bend 2 runtime suite (bench/runtime/tree-bitonic), 1:1 with main.c / main.bend.
# Sum-type (ADT) 1:1 port (Workstream A gate, ADR 0039); the fastest shape is idiomatic/tree-bitonic.al.
# Tree-shaped bitonic sorting network over 2^23 xorshift keys: bsort sorts the two
# halves in opposite directions, warp compare-and-swaps paired leaves of two subtrees
# and zips the results, flow recurses the merge; scan verifies order into a Stat.
# Every Bend parallel let (warp, flow, bsort, scan) is an Alice two-call fork.
# Idiomatic shape: sum type nodes on the GC heap. Big (23): 3787129428.
enum Tree
  Leaf(v : UInt32)
  Node(l : Tree, r : Tree)
end

record struct Stat(lo : UInt32, hi : UInt32, ok : UInt32, mx : UInt32)

def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def key(i : UInt32) : UInt32
  prng((i &+ 1_u32) &* 2654435761_u32)
end

# children are bound to locals: `Tree::Node(Tree::Leaf(y), ..)` does not parse (AL0101, NOTES-bench2.md)
def warp_leaf(x : UInt32, y : UInt32, s : Bool) : Tree
  lx = Tree::Leaf(x)
  ly = Tree::Leaf(y)
  s != (x > y) ? Tree::Node(ly, lx) : Tree::Node(lx, ly)
end

def warp_zip(wa : Tree, wb : Tree) : Tree
  case wa
  when Tree::Node(a0, a1)
    case wb
    when Tree::Node(b0, b1)
      l = Tree::Node(a0, b0)
      r = Tree::Node(a1, b1)
      Tree::Node(l, r)
    when Tree::Leaf(bv) then Tree::Leaf(0_u32)
    end
  when Tree::Leaf(av) then Tree::Leaf(0_u32)
  end
end

def warp(a : Tree, b : Tree, s : Bool) : Tree
  case a
  when Tree::Leaf(av)
    case b
    when Tree::Leaf(bv) then warp_leaf(av, bv, s)
    when Tree::Node(ba, bb) then Tree::Leaf(0_u32)
    end
  when Tree::Node(aa, ab)
    case b
    when Tree::Leaf(bv) then Tree::Leaf(0_u32)
    when Tree::Node(ba, bb)
      wa, wb = warp(aa, ba, s), warp(ab, bb, s)
      warp_zip(wa, wb)
    end
  end
end

def warp_node(t : Tree, s : Bool) : Tree
  case t
  when Tree::Leaf(v) then t
  when Tree::Node(a, b) then warp(a, b, s)
  end
end

def flow(d : Int32, s : Bool, w : Tree) : Tree
  return w if d == 0
  case w
  when Tree::Leaf(v) then w
  when Tree::Node(wa, wb)
    fa, fb = flow(d - 1, s, warp_node(wa, s)), flow(d - 1, s, warp_node(wb, s))
    Tree::Node(fa, fb)
  end
end

def bsort(d : Int32, s : Bool, x : UInt32) : Tree
  return Tree::Leaf(key(x)) if d == 0
  sa, sb = bsort(d - 1, false, x &* 2_u32 &+ 1_u32), bsort(d - 1, true, x &* 2_u32)
  flow(d - 1, s, warp(sa, sb, s))
end

def scan(t : Tree) : Stat
  case t
  when Tree::Leaf(v) then Stat.new(v, v, 1_u32, v)
  when Tree::Node(a, b)
    l, r = scan(a), scan(b)
    Stat.new(l.lo, r.hi, l.hi <= r.lo ? (l.ok & r.ok) : 0_u32, l.mx &* 2654435761_u32 &+ r.mx)
  end
end

def stat_out(s : Stat) : UInt32
  ((s.mx &* 2654435761_u32) ^ (s.hi &+ s.lo &* 340573321_u32)) &+ s.ok &* 2246822519_u32
end

n = ARGV.size > 0 ? ARGV[0].to_i : 23
puts stat_out(scan(bsort(n, false, 0_u32)))
