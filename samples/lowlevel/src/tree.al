# An arena-friendly binary search tree: nodes are indices into three Int32 arrays allocated once, not objects.
# Building N nodes costs three allocations instead of N, the GC has nothing to trace inside, and the whole tree
# is released at once. Links are plain indices (-1 = none), so a stale index can at worst find a wrong node,
# never freed memory: bounds checks still apply.
class IndexTree
  getter size : Int32 = 0

  def initialize(capacity : Int32)
    @keys = Array(Int32).new(capacity)
    @left = Array(Int32).new(capacity, -1)
    @right = Array(Int32).new(capacity, -1)
  end

  # Inserts `key` (duplicates go right). No recursion: a loop follows the links.
  def insert(key : Int32)
    raise "tree is full" if @size == @keys.size
    node = @size
    @keys[node] = key
    @size += 1
    return if node == 0
    at = 0
    while true
      if key < @keys[at]
        if @left[at] < 0
          @left[at] = node
          return
        end
        at = @left[at]
      else
        if @right[at] < 0
          @right[at] = node
          return
        end
        at = @right[at]
      end
    end
  end

  def includes?(key : Int32) : Bool
    at = @size > 0 ? 0 : -1
    while at >= 0
      return true if @keys[at] == key
      at = key < @keys[at] ? @left[at] : @right[at]
    end
    false
  end

  # Height and in-order checks with an explicit stack in one more array: deep trees cannot overflow the call stack.
  def height : Int32
    return 0 if @size == 0
    nodes = Array(Int32).new(@size)
    depths = Array(Int32).new(@size)
    top = 0
    nodes[0] = 0
    depths[0] = 1
    top = 1
    best = 0
    while top > 0
      top -= 1
      n = nodes[top]
      d = depths[top]
      best = d if d > best
      if @left[n] >= 0
        nodes[top] = @left[n]
        depths[top] = d + 1
        top += 1
      end
      if @right[n] >= 0
        nodes[top] = @right[n]
        depths[top] = d + 1
        top += 1
      end
    end
    best
  end

  # Keys in order; for a search tree they come out sorted.
  def in_order : Array(Int32)
    out = Array(Int32).new(@size)
    stack = Array(Int32).new(@size)
    top = 0
    count = 0
    at = @size > 0 ? 0 : -1
    while at >= 0 || top > 0
      while at >= 0
        stack[top] = at
        top += 1
        at = @left[at]
      end
      top -= 1
      at = stack[top]
      out[count] = @keys[at]
      count += 1
      at = @right[at]
    end
    out
  end
end

module Lcg
  # Deterministic pseudo-random keys: the same tree on every run and machine.
  def self.keys(n : Int32, seed : Int64) : Array(Int32)
    keys = Array(Int32).new(n)
    x = seed
    for i in 0...n
      x = (x * 1103515245_i64 + 12345_i64) % 2147483648_i64
      keys[i] = (x % 1000000_i64).to_i
    end
    keys
  end
end
