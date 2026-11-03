# C's standard library, declared once. Each `fun` is a direct call: numbers cross as themselves, strings as
# NUL-terminated UTF-8, arrays as a pointer to their pinned elements (no copy), `Pointer(T)` as a raw pointer.
@[Link("libc.so.6")]
lib LibC
  fun getpid : Int32
  fun strlen(s : String) : UInt64
  fun getenv(name : String) : String?                    # decoded copy of C's value; NULL is nil
  fun getenv_raw = getenv(name : String) : Pointer(UInt8) # the same C function, raw: walked in pointers.al
  fun malloc(size : UInt64) : Pointer(Int32)
  fun free(p : Pointer(Int32)) : Void
  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void
end
