# Sum types built and folded in parallel: both recursions fork (pure, recursive), and the build picks server GC
# automatically ([runtime] gc = "auto") because the program runs in parallel.
enum Tree
  Leaf(x : Int64)
  Node(l : Tree, r : Tree)
end

def build(depth : Int32, at : Int64) : Tree
  return Tree::Leaf(at) if depth == 0
  l, r = build(depth - 1, at * 2), build(depth - 1, at * 2 + 1)
  Tree::Node(l, r)
end

def total(t : Tree) : Int64
  case t
  when Tree::Leaf(x) then x
  when Tree::Node(l, r) then total(l) + total(r)
  end
end

def depth_of(t : Tree) : Int32
  case t
  when Tree::Leaf(_) then 0
  when Tree::Node(l, _) then 1 + depth_of(l)
  end
end

t = build(16, 0_i64)
puts "depth #{depth_of(t)}, leaves sum #{total(t)}"
