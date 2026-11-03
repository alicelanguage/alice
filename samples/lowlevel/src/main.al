# Low-level Alice: C functions, a native kernel, raw pointers and an index-based tree. Linux (libc.so.6).
#   alice run                 the demo
#   alice run -- --measure    also print the allocations of the index tree and of an object tree
require "./libc"
require "./pointers"
require "./kernel"
require "./tree"

# The object-per-node tree the index tree replaces (for the allocation comparison only).
class Node
  property left : Node? = nil
  property right : Node? = nil
  getter key : Int32
  def initialize(@key : Int32)
  end
end

def object_tree(keys : Array(Int32)) : Node
  root = Node.new(keys[0])
  for i in 1...keys.size
    at = root
    placed = false
    while !placed
      if keys[i] < at.key
        if at.left.nil?
          at.left = Node.new(keys[i])
          placed = true
        else
          at = at.left.not_nil!
        end
      else
        if at.right.nil?
          at.right = Node.new(keys[i])
          placed = true
        else
          at = at.right.not_nil!
        end
      end
    end
  end
  root
end

# 1. C functions
puts "pid > 0: #{LibC.getpid > 0}"
puts "strlen(\"wonderland\") = #{LibC.strlen("wonderland")}"
xs = [42, 7, 19, 3, 25]
LibC.qsort(xs, 5_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })
puts "qsort: #{xs.join(" ")}"

# 2. raw pointers, inside unsafe
unsafe
  puts "env_length(HOME) == strlen: #{Pointers.env_length("HOME") == LibC.strlen(LibC.getenv("HOME") || "").to_i64}"
  puts "env_length(unset) = #{Pointers.env_length("ALICE_LOWLEVEL_UNSET")}"
  puts "malloc'd squares 0..999: #{Pointers.sum_of_squares(1000)}"
end

# 3. a native kernel
n = 100000
a = Array(Float64).new(n)
b = Array(Float64).new(n)
for i in 0...n
  a[i] = (i % 7).to_f
  b[i] = (i % 5).to_f
end
puts "dot = #{Kernel.dot(a, b)} (managed #{Kernel.dot_managed(a, b)}), native library loaded: #{Kernel.native?}"

# 4. an index tree
keys = Lcg.keys(100000, 42_i64)
tree = IndexTree.new(keys.size)
for k in keys
  tree.insert(k)
end
sorted = tree.in_order
ordered = true
for i in 1...sorted.size
  ordered = false if sorted[i - 1] > sorted[i]
end
puts "index tree: #{tree.size} nodes, height #{tree.height}, in order: #{ordered}, has #{keys[500]}: #{tree.includes?(keys[500])}, has -1: #{tree.includes?(-1)}"

if ARGV.includes?("--measure")
  before = GC.GetAllocatedBytesForCurrentThread()
  t = IndexTree.new(keys.size)
  for k in keys
    t.insert(k)
  end
  arena = GC.GetAllocatedBytesForCurrentThread() - before
  before = GC.GetAllocatedBytesForCurrentThread()
  root = object_tree(keys)
  objects = GC.GetAllocatedBytesForCurrentThread() - before
  puts "allocated for #{keys.size} nodes: index tree #{arena} bytes, object tree #{objects} bytes (root #{root.key == keys[0]})"
end
