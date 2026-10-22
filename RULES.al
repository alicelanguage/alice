# RULES.al: properties Alice itself must never break. `alice prove RULES.al` checks every case of every domain
# and writes PROOF.al; `dotnet test` fails if a rule stops holding or PROOF.al is stale (ProveTests).
# These pin the numeric semantics of ADR 0013 against the compiler, the optimizer passes and the runtime.

# Floored division: a == (a / b) * b + a % b, and the remainder takes the sign of the divisor
rule floored_div_mod(a in -300..300, b in -300..300)
  return true if b == 0
  q = a / b
  r = a % b
  a == q * b + r && (b > 0 ? r >= 0 && r < b : r <= 0 && r > b)
end

# divmod agrees with / and %
rule divmod_matches(a in -200..200, b in -50..50)
  return true if b == 0
  q, r = a.divmod(b)
  q == a / b && r == a % b
end

# Wrapping arithmetic on Int8 covers every value: &+ then &- is the identity
rule wrapping_roundtrip(a in -128..127, b in -128..127)
  x = a.to_i8
  y = b.to_i8
  (x &+ y) &- y == x
end

# min / max / clamp are consistent
rule min_max_clamp(x in -100..100, lo in -20..20, w in 0..20)
  hi = lo + w
  c = Math.Min(Math.Max(x, lo), hi)
  c >= lo && c <= hi && (x < lo || x > hi || c == x)
end

# Absolute value of the floored remainder is below the divisor's
rule remainder_is_small(a in -500..500, b in 1..60)
  (a % b).abs < b && (-a % b).abs < b
end

# Bit tricks the runtime relies on: x & (x - 1) clears the lowest set bit
rule lowest_bit(x in 1..65535)
  y = x & (x - 1)
  y < x && (x ^ y) & ((x ^ y) - 1) == 0
end
