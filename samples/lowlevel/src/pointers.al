# Raw pointers. Every use of a `Pointer(T)` value sits inside `unsafe`: anywhere else it is error AL0216, so a
# reviewer can find every line where memory safety depends on the programmer by searching for `unsafe`.
module Pointers
  # Length of an environment variable's value, by walking its bytes up to the NUL, as C's strlen does.
  # -1 when the variable is not set (getenv returned NULL).
  unsafe def self.env_length(name : String) : Int64
    # A Pointer(T) has no NULL test yet (`p.nil?` is false even for NULL), so ask the `String?` binding first:
    # it reads C's environment and maps NULL to nil. (.NET's Environment.GetEnvironmentVariable is not enough:
    # Environment.SetEnvironmentVariable changes only .NET's copy, which C's getenv never sees.)
    return -1_i64 if LibC.getenv(name).nil?
    p = LibC.getenv_raw(name)
    n = 0_i64
    while p[n] != 0_u8
      n += 1
    end
    n
  end

  # Squares 0..n-1 in a malloc'd block, summed through the pointer, then freed. The GC never sees this memory:
  # forgetting `free` leaks it, and reading past `n` is undefined behaviour, which is why this is `unsafe`.
  unsafe def self.sum_of_squares(n : Int32) : Int64
    p = LibC.malloc((n * 4).to_u64)          # (a NULL result cannot be tested yet; see README)
    begin
      for i in 0...n
        p[i] = i * i
      end
      # a `while`, not `for`: the compiler would run a `for` reduction in parallel, and its outlined loop body
      # is not yet compiled as unsafe (see README, "Known gaps")
      total = 0_i64
      i = 0
      while i < n
        total += p[i]
        i += 1
      end
      total
    ensure
      LibC.free(p)
    end
  end
end
