require "../src/libc"
require "../src/pointers"
require "../src/kernel"
require "../src/tree"

def test_c_functions
  assert_equal 10_u64, LibC.strlen("wonderland")
  assert_equal 6_u64, LibC.strlen("héllo")          # UTF-8 bytes
  xs = [5, 3, 9, 1]
  LibC.qsort(xs, 4_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })
  assert_equal "1 3 5 9", xs.join(" ")
end

def test_pointer_walks
  path = LibC.getenv("PATH")
  assert !path.nil?
  unsafe
    assert_equal LibC.strlen(path.not_nil!).to_i64, Pointers.env_length("PATH")
    assert_equal -1_i64, Pointers.env_length("ALICE_LOWLEVEL_NOT_SET")
    assert_equal 14_i64, Pointers.sum_of_squares(4)
  end
end

def test_kernel_matches_the_managed_loop
  a = [1.0, 2.0, 3.0, 4.0]
  b = [4.0, 3.0, 2.0, 1.0]
  assert_equal 20.0, Kernel.dot(a, b)
  assert_equal Kernel.dot_managed(a, b), Kernel.dot(a, b)
end

def test_index_tree
  t = IndexTree.new(7)
  for k in [50, 30, 70, 20, 40, 60, 80]
    t.insert(k)
  end
  assert_equal 7, t.size
  assert_equal 3, t.height
  assert_equal "20 30 40 50 60 70 80", t.in_order.join(" ")
  assert t.includes?(60)
  assert !t.includes?(65)
  begin
    t.insert(1)
    assert false, "expected the full tree to refuse"
  rescue e : Exception
    assert_equal "tree is full", e.Message
  end
end

def test_sorted_input_makes_a_deep_tree_without_recursion
  t = IndexTree.new(20000)
  for k in 0...20000
    t.insert(k)                                     # a 20000-deep chain: no recursion, so no stack overflow
  end
  assert_equal 20000, t.height
end

def test_lcg_is_deterministic
  assert_equal Lcg.keys(5, 7_i64).join(","), Lcg.keys(5, 7_i64).join(",")
end
