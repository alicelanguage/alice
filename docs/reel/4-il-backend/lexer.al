# lexer: generate 2^DEPTH lines of source from a template and tokenize them (port of
# bend/bench/runtime/lexer). Same salted xorshift generator, same tokenizer (identifier by FNV-1a,
# number in decimal, operator or punctuation by its code, spaces skipped) and checksum as main.c
# (tokens folded in order per line, lines summed). Like main.bend, the lines fork as a balanced
# batch tree; main.c runs them in one loop (same sum).
def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def mix(acc : UInt32, kind : UInt32, x : UInt32) : UInt32
  (acc &* 2654435761_u32) ^ (kind &* 40503_u32 &+ x)
end

# one line: expand the template into a byte buffer, then tokenize it
def line(i : UInt32, tpl : Array(Int32)) : UInt32
  buf = Array(UInt8).new(128)
  s = prng((i &+ 1_u32) &* 2654435761_u32)
  n = 0
  k = 0
  while k < tpl.size
    t = prng(s ^ (k.to_u32 &* 2654435761_u32))
    ch = tpl[k].to_u8
    if ch == 105_u8 # 'i'
      j = 1_u32 &+ (t & 7_u32)
      while j != 0_u32
        t = prng(t)
        buf[n] = (97_u32 &+ t % 26_u32).to_u8
        n += 1
        j = j &- 1_u32
      end
    elsif ch == 110_u8 # 'n'
      j = 1_u32 &+ t % 6_u32
      while j != 0_u32
        t = prng(t)
        buf[n] = (48_u32 &+ t % 10_u32).to_u8
        n += 1
        j = j &- 1_u32
      end
    elsif ch == 111_u8 # 'o': "+-*/"
      o = t & 3_u32
      buf[n] = o == 0_u32 ? 43_u8 : (o == 1_u32 ? 45_u8 : (o == 2_u32 ? 42_u8 : 47_u8))
      n += 1
    else
      buf[n] = ch
      n += 1
    end
    k += 1
  end
  acc = 0_u32
  p = 0
  while p < n
    c = buf[p]
    if c >= 97_u8 && c <= 122_u8
      # typed: a UInt32 local first assigned inside an if body fails with AL0304 (README, compiler issues)
      h : UInt32 = 2166136261_u32
      while p < n && buf[p] >= 97_u8 && buf[p] <= 122_u8
        h = (h ^ buf[p].to_u32) &* 16777619_u32
        p += 1
      end
      acc = mix(acc, 1_u32, h)
    elsif c >= 48_u8 && c <= 57_u8
      v : UInt32 = 0_u32
      while p < n && buf[p] >= 48_u8 && buf[p] <= 57_u8
        v = v &* 10_u32 &+ (buf[p].to_u32 &- 48_u32)
        p += 1
      end
      acc = mix(acc, 2_u32, v)
    elsif c == 32_u8
      p += 1
    else
      acc = mix(acc, 3_u32, c.to_u32)
      p += 1
    end
  end
  acc
end

def batch(d : Int32, i : UInt32, tpl : Array(Int32)) : UInt32
  return line(i, tpl) if d == 0
  a, b = batch(d - 1, i, tpl), batch(d - 1, i &+ (1_u32 << (d - 1)), tpl)
  a &+ b
end

tpl = "i = ( n o i ) o ( n o i ) o ( n o i ) ;".bytes
depth = ARGV.size > 0 ? ARGV[0].to_i : 23
puts batch(depth, 0_u32, tpl)
