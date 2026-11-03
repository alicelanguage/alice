# tree-radix, CM shape (ADR 0039; not the same algorithm as alice/tree-radix.al): the same 2^N prng keys
# (24 bits) and the same checksum over the sorted distinct keys, with Bend's trie sort replaced by the CM
# counting sort: `keys.sort_by_key(vals)` (stable LSD radix, per-block histograms + exclusive scan + scatter),
# then a compaction drops repeats (the trie's Busy absorbs a duplicate key). Big (22): 1998173798.

def prng(x : UInt32) : UInt32
  b = x ^ (x << 13)
  d = b ^ (b >> 17)
  d ^ (d << 5)
end

def key(i : UInt32) : UInt32
  prng((i &+ 1_u32) &* 2654435761_u32) & 16777215_u32
end

def gen(n : Int32) : Array(UInt32)
  keys = Array(UInt32).new(1 << n)
  for p in 0...(1 << n)
    keys[p] = key(p.to_u32)
  end
  keys
end

# the sorted distinct keys: a compaction (keep the first of every run)
def distinct(s : Array(UInt32)) : Array(UInt32)
  out = Array(UInt32).new(s.size)
  m = 0
  for i in 0...s.size
    if i == 0 || s[i] != s[i == 0 ? 0 : i - 1]
      out[m] = s[i]
      m += 1
    end
  end
  out[0...m]
end

def chk_out(a : Array(UInt32)) : UInt32
  return 0_u32 if a.size == 0
  ok = 1_u32
  sum = 0_u32
  for i in 0...a.size
    ok = ok & (i == 0 || a[i - 1] < a[i] ? 1_u32 : 0_u32)
    sum = sum &+ a[i]
  end
  cnt = a.size.to_u32
  lo = a[0]
  hi = a[a.size - 1]
  ((sum &+ cnt &* 2654435761_u32) ^ (hi &+ lo &* 340573321_u32)) &+ ok &* 2246822519_u32
end

n = ARGV.size > 0 ? ARGV[0].to_i : 22
keys = gen(n)
sorted = keys.sort_by_key(keys).keys
puts chk_out(distinct(sorted))
