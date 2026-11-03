# A numeric kernel compiled twice: to C (clang -O3 -march=native, loaded from lib<package>_native.so) and to
# .NET. The C version keeps Alice's rules: every array access is bounds-checked and integer overflow traps
# (a violation aborts the process, since C has no exception to raise). Without clang, or when the library is
# missing, the .NET version runs: same results.
module Kernel
  @[Native]
  def self.dot(a : Array(Float64), b : Array(Float64)) : Float64
    s = 0.0
    for i in 0...a.size
      s += a[i] * b[i]
    end
    s
  end

  # The same loop as plain Alice, to compare with.
  def self.dot_managed(a : Array(Float64), b : Array(Float64)) : Float64
    s = 0.0
    for i in 0...a.size
      s += a[i] * b[i]
    end
    s
  end

  # True when the C library was found and its functions are used.
  def self.native? : Bool
    Alice.Runtime.NativeLibs.IsLoaded("lowlevel_native")
  end
end
