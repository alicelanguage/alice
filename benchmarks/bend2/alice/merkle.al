# merkle: Merkle tree over a Speck32/64-encrypted counter stream, with audit and proof phases
# (port of bend/bench/runtime/merkle). Same leaf stream (30 blocks x 22 rounds), join hash, tree
# build, audit re-fold, scalar-prover proof extraction and verification as main.c. The tree and the
# proof path are enums (GC objects), like Bend's `Mt` and `Path` data types. Forks as in Bend: build,
# audit, htree, and pgen's `sib rest = htree(...) pgen(...)`.
enum Mt
  Ml(h : UInt32)
  Mn(h : UInt32, l : Mt, r : Mt)
end

enum Path
  Pn
  Pc(side : UInt32, sib : UInt32, rest : Path)
end

# 22 Speck rounds over the 16-bit halves x, y. rk is the expanded key, passed down from main: an
# `Array(UInt32)` constant (`RK = [256_u32, ...]`) is emitted as `int[]` and does not compile
# (README, compiler issues). main.c clamps the key index at 21; it never exceeds 21 here.
def speck(rk : Array(UInt32), x0 : UInt32, y0 : UInt32) : UInt32
  x = x0
  y = y0
  i = 0
  while i < 22
    nx = (((((x >> 7) | (x << 9)) & 65535_u32) &+ y) & 65535_u32) ^ rk[i]
    nx = nx & 65535_u32
    y = ((((y << 2) | (y >> 14)) & 65535_u32) ^ nx) & 65535_u32
    x = nx
    i += 1
  end
  x | (y << 16)
end

# one leaf: encrypt 30 counter blocks, chain the ciphertexts
def leafh(rk : Array(UInt32), b : UInt32) : UInt32
  block = b &* 30_u32
  acc = b &+ 1_u32
  n = 0
  while n < 30
    acc = acc &* 2654435761_u32 &+ speck(rk, block & 65535_u32, (block >> 16) & 65535_u32)
    block = block &+ 1_u32
    n += 1
  end
  acc
end

# Merkle join: ARX mix of the two child hashes
def mrk(a : UInt32, b : UInt32) : UInt32
  hh = (a &* 2654435761_u32) ^ b
  h2 = ((hh &+ 2246822519_u32) &* 2246822519_u32) ^ (hh >> 13)
  h2 ^ (h2 >> 16)
end

# the scalar prover: recompute a subtree hash without materializing it
def htree(rk : Array(UInt32), d : Int32, b : UInt32) : UInt32
  return leafh(rk, b) if d == 0
  a, c = htree(rk, d - 1, b), htree(rk, d - 1, b &+ (1_u32 << (d - 1)))
  mrk(a, c)
end

def hash_of(t : Mt) : UInt32
  case t
  when Mt::Ml(h) then h
  when Mt::Mn(h, l, r) then h
  end
end

# build the Merkle tree over blocks b .. b + 2^d - 1; a node stores the join of its children's hashes
def build(rk : Array(UInt32), d : Int32, b : UInt32) : Mt
  return Mt::Ml(leafh(rk, b)) if d == 0
  l, r = build(rk, d - 1, b), build(rk, d - 1, b &+ (1_u32 << (d - 1)))
  Mt::Mn(mrk(hash_of(l), hash_of(r)), l, r)
end

# audit: re-fold the tree; any stored-hash mismatch perturbs the result
def audit(t : Mt) : UInt32
  case t
  when Mt::Ml(h) then h
  when Mt::Mn(h, l, r)
    a, b = audit(l), audit(r)
    m = mrk(a, b)
    m &+ (m ^ h)
  end
end

# Merkle proof for leaf k: per level, the side bit and the sibling hash (forked with the rest)
def pgen(rk : Array(UInt32), d : Int32, k : UInt32, b : UInt32) : Path
  return Path::Pn if d == 0
  bit = (k >> (d - 1)) & 1_u32
  half = 1_u32 << (d - 1)
  sb = bit == 0_u32 ? b &+ half : b
  rb = bit == 0_u32 ? b : b &+ half
  sib, rest = htree(rk, d - 1, sb), pgen(rk, d - 1, k, rb)
  Path::Pc(bit, sib, rest)
end

# fold the proof back up from the leaf hash to a root hash
def pverify(p : Path, lh : UInt32) : UInt32
  case p
  when Path::Pn then lh
  when Path::Pc(side, sib, rest)
    hh = pverify(rest, lh)
    side == 0_u32 ? mrk(hh, sib) : mrk(sib, hh)
  end
end

keys = [256, 5394, 24957, 5208, 26905, 30690, 3209, 52443, 61418, 20019, 30452,
        22902, 61067, 56068, 17943, 62334, 34740, 36554, 60827, 14930, 33321, 60772]
rk = Array(UInt32).new(22)
i = 0
while i < 22
  rk[i] = keys[i].to_u32
  i += 1
end
size = ARGV.size > 0 ? ARGV[0].to_i : 22
probe = ARGV.size > 1 ? ARGV[1].to_i.to_u32 : 1337_u32
t = build(rk, size, 0_u32)
root = audit(t)
puts mrk(root, pverify(pgen(rk, size, probe, 0_u32), leafh(rk, probe)))
