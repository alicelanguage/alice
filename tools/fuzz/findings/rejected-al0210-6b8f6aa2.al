# fuzz finding rejected-al0210-6b8f6aa2
# category: rejected:AL0210
# main.al:1:1: `k` is not pure: it writes state it did not allocate, does I/O, or calls code not proven pure

pure def k(n : Int32) : Int32
  case n
  when 1..44
  end
end
