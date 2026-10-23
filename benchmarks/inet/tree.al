# benchmarks/inet tree workload in Alice: build a complete binary tree of depth d, then sum its leaves.
# Both recursions fork (pure, recursive, two calls); the tree lives on the .NET GC heap.
enum Tree
  Leaf(x : Int64)
  Node(l : Tree, r : Tree)
end

def build(d : Int32) : Tree
  return Tree::Leaf(1_i64) if d == 0
  l, r = build(d - 1), build(d - 1)
  Tree::Node(l, r)
end

def total(t : Tree) : Int64
  case t
  when Tree::Leaf(x) then x
  when Tree::Node(l, r) then total(l) + total(r)
  end
end

d = ARGV.size > 0 ? ARGV[0].to_i : 22
best = 1.0e9
6.times do
  sw = System.Diagnostics.Stopwatch.start_new
  s = total(build(d))
  best = Math.min(best, sw.elapsed.total_milliseconds)
  raise "wrong" if s != (1_i64 << d)
end
puts "tree(#{d}) build+sum #{best.round(1)} ms"
