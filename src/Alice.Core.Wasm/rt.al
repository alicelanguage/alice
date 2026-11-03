# Alice thin runtime for runtime = "alice" (ADR 0034): the parts written in Alice and compiled by the wasm backend
# itself. Number formatting here; the allocator, collector, strings and I/O are in rt.wat.
#
# fmt_float prints the shortest decimal that reads back as the same Float64 (or Float32), the digits .NET's "R"
# formatting gives: Burger & Dybvig's free-format algorithm on exact big integers (40 x 32-bit limbs hold every
# scaled Float64), then the Alice layout (Prelude.FormatFloat: `1.0`, `1.0e+16`) or the C# one (`1`, `1E+16`).
module WasmRt
  def self.bset(a : Array(UInt32), v : UInt64)
    i = 0
    while i < 40
      a[i] = 0_u32
      i += 1
    end
    a[0] = (v & 4294967295_u64).to_u32
    a[1] = (v >> 32).to_u32
  end

  def self.bcopy(d : Array(UInt32), a : Array(UInt32))
    i = 0
    while i < 40
      d[i] = a[i]
      i += 1
    end
  end

  def self.bmul(a : Array(UInt32), m : UInt32)
    carry = 0_u64
    i = 0
    while i < 40
      t = a[i].to_u64 &* m.to_u64 &+ carry
      a[i] = (t & 4294967295_u64).to_u32
      carry = t >> 32
      i += 1
    end
  end

  def self.bshl(a : Array(UInt32), n : Int32)
    w = n / 32
    b = n % 32
    i = 39
    while i >= 0
      v = 0_u32
      src = i - w
      if src >= 0
        v = a[src] << b
        if b > 0 && src >= 1
          v = v | (a[src - 1] >> (32 - b))
        end
      end
      a[i] = v
      i -= 1
    end
  end

  def self.bcmp(a : Array(UInt32), b : Array(UInt32)) : Int32
    i = 39
    while i >= 0
      return 1 if a[i] > b[i]
      return -1 if a[i] < b[i]
      i -= 1
    end
    0
  end

  def self.badd(c : Array(UInt32), a : Array(UInt32), b : Array(UInt32))
    carry = 0_u64
    i = 0
    while i < 40
      t = a[i].to_u64 &+ b[i].to_u64 &+ carry
      c[i] = (t & 4294967295_u64).to_u32
      carry = t >> 32
      i += 1
    end
  end

  def self.bsub(a : Array(UInt32), b : Array(UInt32))
    borrow = 0_u64
    i = 0
    while i < 40
      t = a[i].to_u64 &- b[i].to_u64 &- borrow
      a[i] = (t & 4294967295_u64).to_u32
      borrow = t >> 63
      i += 1
    end
  end

  def self.zeros(n : Int32) : String
    s = ""
    i = 0
    while i < n
      s = s + "0"
      i += 1
    end
    s
  end

  def self.exp2(x : Int32) : String
    a = x < 0 ? 0 - x : x
    a < 10 ? "0" + a.to_s : a.to_s
  end

  # shortest round-trip digits of a finite positive value; returns the digits, `point` gets the decimal exponent
  # (value = 0.DIGITS x 10^point) in pt[0]
  def self.shortest(v : Float64, single : Bool, pt : Array(Int32)) : String
    f = 0_u64
    e = 0
    p = 53
    minexp = 0
    if single
      fb = BitConverter.SingleToInt32Bits(v.to_f32)
      be = (fb >> 23) & 255
      f = (fb & 8388607).to_u64
      p = 24
      minexp = 1 - 127 - 23
      if be == 0
        e = minexp
      else
        f = f | 8388608_u64
        e = be - 127 - 23
      end
    else
      bits = BitConverter.DoubleToInt64Bits(v)
      be = ((bits >> 52) & 2047_i64).to_i32
      f = (bits & 4503599627370495_i64).to_u64
      minexp = 1 - 1023 - 52
      if be == 0
        e = minexp
      else
        f = f | 4503599627370496_u64
        e = be - 1023 - 52
      end
    end
    r = Array(UInt32).new(40)
    s = Array(UInt32).new(40)
    mp = Array(UInt32).new(40)
    mm = Array(UInt32).new(40)
    tmp = Array(UInt32).new(40)
    hidden = 1_u64 << (p - 1)
    unequal = f == hidden && e > minexp
    if e >= 0
      bset(r, f)
      bset(mp, 1_u64)
      bset(mm, 1_u64)
      bshl(mm, e)
      if unequal
        bshl(r, e + 2)
        bset(s, 4_u64)
        bshl(mp, e + 1)
      else
        bshl(r, e + 1)
        bset(s, 2_u64)
        bshl(mp, e)
      end
    else
      bset(r, f)
      bset(s, 1_u64)
      bset(mm, 1_u64)
      if unequal
        bshl(r, 2)
        bshl(s, 2 - e)
        bset(mp, 2_u64)
      else
        bshl(r, 1)
        bshl(s, 1 - e)
        bset(mp, 1_u64)
      end
    end
    even = (f & 1_u64) == 0_u64
    # k ~ floor(log10 v) + 1 from the binary exponent, corrected exactly below
    bl = 0
    ff = f
    while ff != 0_u64
      bl += 1
      ff = ff >> 1
    end
    k = (((e + bl - 1) * 1233) >> 12) + 1
    if k >= 0
      i = 0
      while i < k
        bmul(s, 10_u32)
        i += 1
      end
    else
      i = 0
      while i < 0 - k
        bmul(r, 10_u32)
        bmul(mp, 10_u32)
        bmul(mm, 10_u32)
        i += 1
      end
    end
    # too low: r + m+ reaches s
    badd(tmp, r, mp)
    c = bcmp(tmp, s)
    while (even && c >= 0) || (!even && c > 0)
      bmul(s, 10_u32)
      k += 1
      badd(tmp, r, mp)
      c = bcmp(tmp, s)
    end
    # too high: 10 (r + m+) stays below s
    bmul(tmp, 10_u32)
    c = bcmp(tmp, s)
    while (even && c < 0) || (!even && c <= 0)
      bmul(r, 10_u32)
      bmul(mp, 10_u32)
      bmul(mm, 10_u32)
      k -= 1
      badd(tmp, r, mp)
      bmul(tmp, 10_u32)
      c = bcmp(tmp, s)
    end
    digits = ""
    done = false
    while !done
      bmul(r, 10_u32)
      bmul(mp, 10_u32)
      bmul(mm, 10_u32)
      d = 0
      while bcmp(r, s) >= 0
        bsub(r, s)
        d += 1
      end
      c1 = bcmp(r, mm)
      tc1 = even ? c1 <= 0 : c1 < 0
      badd(tmp, r, mp)
      c2 = bcmp(tmp, s)
      tc2 = even ? c2 >= 0 : c2 > 0
      if tc1 && tc2
        badd(tmp, r, r)
        d += 1 if bcmp(tmp, s) >= 0
      elsif tc2
        d += 1
      end
      digits = digits + "0123456789"[d].to_s
      done = tc1 || tc2
    end
    pt[0] = k
    digits
  end

  def self.fmt_float(v : Float64, single : Bool, alice : Bool) : String
    return "NaN" if v != v
    neg = BitConverter.DoubleToInt64Bits(v) < 0_i64
    sign = neg ? "-" : ""
    if v - v != 0.0
      return sign + "Infinity"
    end
    if v == 0.0
      return alice ? sign + "0.0" : sign + "0"
    end
    pt = Array(Int32).new(1)
    d = shortest(neg ? 0.0 - v : v, single, pt)
    point = pt[0]
    n = d.size
    x = point - 1
    if alice
      if x < -4 || x >= 16
        return sign + d[0].to_s + "." + (n > 1 ? d.Substring(1) : "0") + "e" + (x < 0 ? "-" : "+") + exp2(x)
      end
      return sign + "0." + zeros(0 - point) + d if point <= 0
      return sign + d + zeros(point - n) + ".0" if point >= n
      return sign + d.Substring(0, point) + "." + d.Substring(point)
    end
    if x < -4 || x >= (single ? 7 : 15)
      return sign + d[0].to_s + (n > 1 ? "." + d.Substring(1) : "") + "E" + (x < 0 ? "-" : "+") + exp2(x)
    end
    return sign + "0." + zeros(0 - point) + d if point <= 0
    return sign + d + zeros(point - n) if point >= n
    sign + d.Substring(0, point) + "." + d.Substring(point)
  end

  # Prelude.fma (Math.FusedMultiplyAdd): wasm core has no fused multiply-add. The product is split exactly (Veltkamp
  # and Dekker), so a * b = p + e, and the sum is formed with error-free two-sums; the result can differ from a single
  # rounding in the last ulp in rare double-rounding cases (ADR 0034: documented float differences).
  def self.split(a : Float64, hl : Array(Float64))
    t = 134217729.0 * a
    hi = t - (t - a)
    hl[0] = hi
    hl[1] = a - hi
  end

  def self.fma(a : Float64, b : Float64, c : Float64) : Float64
    p = a * b
    return p + c if p - p != 0.0 || c - c != 0.0 || p == 0.0
    sa = Array(Float64).new(2)
    sb = Array(Float64).new(2)
    split(a, sa)
    split(b, sb)
    e = ((sa[0] * sb[0] - p) + sa[0] * sb[1] + sa[1] * sb[0]) + sa[1] * sb[1]
    s = p + c
    bb = s - p
    t = (p - (s - bb)) + (c - bb)
    s + (t + e)
  end
  # System.Array.Sort(Array(Int32)): in-place heapsort (equal keys are indistinguishable, so stability does not show)
  def self.sort_i32(a : Array(Int32))
    n = a.size
    i = (n >> 1) - 1
    while i >= 0
      sift_i32(a, i, n)
      i -= 1
    end
    e = n - 1
    while e > 0
      t = a[0]
      a[0] = a[e]
      a[e] = t
      sift_i32(a, 0, e)
      e -= 1
    end
  end

  def self.sift_i32(a : Array(Int32), root : Int32, n : Int32)
    r = root
    while true
      c = 2 * r + 1
      break if c >= n
      c += 1 if c + 1 < n && a[c] < a[c + 1]
      break unless a[r] < a[c]
      t = a[r]
      a[r] = a[c]
      a[c] = t
      r = c
    end
  end

  # System.Array.Sort(Array(UInt32)): in-place heapsort (equal keys are indistinguishable, so stability does not show)
  def self.sort_u32(a : Array(UInt32))
    n = a.size
    i = (n >> 1) - 1
    while i >= 0
      sift_u32(a, i, n)
      i -= 1
    end
    e = n - 1
    while e > 0
      t = a[0]
      a[0] = a[e]
      a[e] = t
      sift_u32(a, 0, e)
      e -= 1
    end
  end

  def self.sift_u32(a : Array(UInt32), root : Int32, n : Int32)
    r = root
    while true
      c = 2 * r + 1
      break if c >= n
      c += 1 if c + 1 < n && a[c] < a[c + 1]
      break unless a[r] < a[c]
      t = a[r]
      a[r] = a[c]
      a[c] = t
      r = c
    end
  end

  # System.Array.Sort(Array(Int64)): in-place heapsort (equal keys are indistinguishable, so stability does not show)
  def self.sort_i64(a : Array(Int64))
    n = a.size
    i = (n >> 1) - 1
    while i >= 0
      sift_i64(a, i, n)
      i -= 1
    end
    e = n - 1
    while e > 0
      t = a[0]
      a[0] = a[e]
      a[e] = t
      sift_i64(a, 0, e)
      e -= 1
    end
  end

  def self.sift_i64(a : Array(Int64), root : Int32, n : Int32)
    r = root
    while true
      c = 2 * r + 1
      break if c >= n
      c += 1 if c + 1 < n && a[c] < a[c + 1]
      break unless a[r] < a[c]
      t = a[r]
      a[r] = a[c]
      a[c] = t
      r = c
    end
  end

  # System.Array.Sort(Array(UInt64)): in-place heapsort (equal keys are indistinguishable, so stability does not show)
  def self.sort_u64(a : Array(UInt64))
    n = a.size
    i = (n >> 1) - 1
    while i >= 0
      sift_u64(a, i, n)
      i -= 1
    end
    e = n - 1
    while e > 0
      t = a[0]
      a[0] = a[e]
      a[e] = t
      sift_u64(a, 0, e)
      e -= 1
    end
  end

  def self.sift_u64(a : Array(UInt64), root : Int32, n : Int32)
    r = root
    while true
      c = 2 * r + 1
      break if c >= n
      c += 1 if c + 1 < n && a[c] < a[c + 1]
      break unless a[r] < a[c]
      t = a[r]
      a[r] = a[c]
      a[c] = t
      r = c
    end
  end

  # System.Array.Sort(Array(Float64)): in-place heapsort (equal keys are indistinguishable, so stability does not show)
  def self.sort_f64(a : Array(Float64))
    n = a.size
    i = (n >> 1) - 1
    while i >= 0
      sift_f64(a, i, n)
      i -= 1
    end
    e = n - 1
    while e > 0
      t = a[0]
      a[0] = a[e]
      a[e] = t
      sift_f64(a, 0, e)
      e -= 1
    end
  end

  def self.sift_f64(a : Array(Float64), root : Int32, n : Int32)
    r = root
    while true
      c = 2 * r + 1
      break if c >= n
      c += 1 if c + 1 < n && a[c] < a[c + 1]
      break unless a[r] < a[c]
      t = a[r]
      a[r] = a[c]
      a[c] = t
      r = c
    end
  end

  # System.Array.Sort(Array(Float32)): in-place heapsort (equal keys are indistinguishable, so stability does not show)
  def self.sort_f32(a : Array(Float32))
    n = a.size
    i = (n >> 1) - 1
    while i >= 0
      sift_f32(a, i, n)
      i -= 1
    end
    e = n - 1
    while e > 0
      t = a[0]
      a[0] = a[e]
      a[e] = t
      sift_f32(a, 0, e)
      e -= 1
    end
  end

  def self.sift_f32(a : Array(Float32), root : Int32, n : Int32)
    r = root
    while true
      c = 2 * r + 1
      break if c >= n
      c += 1 if c + 1 < n && a[c] < a[c + 1]
      break unless a[r] < a[c]
      t = a[r]
      a[r] = a[c]
      a[c] = t
      r = c
    end
  end
  # Float64#round: half away from zero (Prelude.round). round(n) scales by 10^n; .NET rounds the shortest decimal
  # text instead, so a tie that is not exact in binary (2.675) can differ in the last digit.
  def self.round_away(x : Float64) : Float64
    t = x < 0.0 ? -((-x).floor) : x.floor
    d = x - t
    return t + 1.0 if d >= 0.5
    return t - 1.0 if d <= -0.5
    t
  end

  def self.round_digits(v : Float64, n : Int32) : Float64
    return v if v - v != 0.0 || n > 15
    return round_away(v) if n <= 0
    p = 1.0
    i = 0
    while i < n
      p *= 10.0
      i += 1
    end
    s = v * p
    return v if s - s != 0.0 || s.abs >= 4503599627370496.0
    r = round_away(s) / p
    r == 0.0 && v < 0.0 ? -0.0 : r
  end
end
