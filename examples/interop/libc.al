# Calling C with no glue code (ADR 0018): UTF-8 strings, borrowed string returns, out-params, C callbacks.
# Linux libc; on macOS use "/usr/lib/libSystem.B.dylib".
struct Timespec
  getter sec : Int64
  getter nsec : Int64
  def initialize(@sec : Int64, @nsec : Int64)
  end
end

@[Link("libc.so.6")]
lib LibC
  fun strlen(s : String) : UInt64                      # String crosses as UTF-8 on a stack buffer, no allocation
  fun getenv(name : String) : String?                  # borrowed char*: decoded, never freed; NULL is nil
  fun clock_gettime(clock : Int32, out ts : Timespec) : Int32
  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void
end

puts "strlen(\"héllo\") = #{LibC.strlen("héllo")}"        # 6 bytes in UTF-8
puts "has PATH: #{!LibC.getenv("PATH").nil?}"
LibC.clock_gettime(0, out now)
puts "clock ok: #{now.sec > 0}"

xs = [42, 7, 19, 3, 25]
LibC.qsort(xs, 5_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })   # a static C function, no delegate
puts "sorted by C: #{xs.join(", ")}"

begin
  LibC.strlen("safe.txt\0../etc")                     # C would see "safe.txt": rejected instead
rescue e : ArgumentException
  puts "embedded NUL rejected"
end
