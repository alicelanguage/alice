# Dataflow acceptance benchmark in Alice (ADR 0038, P3.3): generate lines -> tokenize -> count over ~1 GiB of
# synthetic text, the same pipeline as ../Program.cs (same text, same FNV-1a token hashes, same checksum).
#
#   alice build dataflow.al -o out/dataflow.dll
#   dotnet out/dataflow.dll [GiB=1] [variants=serial,dataflow,dataflow-par,channels]
#
# `dataflow` and `dataflow-par` are `dataflow do` networks (static stage methods over SPSC rings, rings of 16384 and
# batches of 512 as in ../Program.cs); `channels` is
# the same program written with Channel(T) + spawn (the ADR 0005 lowering: a lock and a Task.Run per stage).
record struct Line(start : Int32, length : Int32)

BINS = 4096

# lowercase words of 1-10 letters, 4-16 words per line, deterministic (xorshift64, the C# generator)
def generate(size : Int32, lines : List(Line)) : Array(UInt8)
  buf = Array(UInt8).new(size)
  x = 0x9E3779B97F4A7C15_u64
  p = 0
  while true
    x ^= x << 13
    x ^= x >> 7
    x ^= x << 17
    start = p
    words = 4 + (x % 13_u64).to_i32
    break if p + words * 11 + 1 > size
    w = 0
    while w < words
      if w > 0
        buf[p] = 32_u8
        p += 1
      end
      x ^= x << 13
      x ^= x >> 7
      x ^= x << 17
      len = 1 + (x % 10_u64).to_i32
      i = 0
      while i < len
        x ^= x << 13
        x ^= x >> 7
        x ^= x << 17
        buf[p] = (97_u64 + x % 26_u64).to_u8
        p += 1
        i += 1
      end
      w += 1
    end
    lines.Add(Line.new(start, p - start))
    buf[p] = 10_u8
    p += 1
  end
  buf.AsSpan(0, p).ToArray
end

def finish(bins : Array(Int64), sum : UInt64) : UInt64
  s = sum
  for i in 0...BINS
    s = s &* 31_u64 &+ bins[i].to_u64
  end
  s
end

# one loop, no handoff: the work itself
def serial(text : Array(UInt8), lines : Array(Line), passes : Int32) : String
  bins = Array(Int64).new(BINS)
  tokens = 0_i64
  sum = 0_u64
  for p in 0...passes
    for k in 0...lines.size
      l = lines[k]
      h = 2166136261_u32
      for b in text.AsSpan(l.start, l.length)
        if b == 32_u8
          bins[(h & 4095_u32).to_i32] += 1
          sum = sum &+ h.to_u64
          tokens += 1
          h = 2166136261_u32
        else
          h = (h ^ b.to_u32) &* 16777619_u32
        end
      end
      bins[(h & 4095_u32).to_i32] += 1
      sum = sum &+ h.to_u64
      tokens += 1
    end
  end
  "#{tokens} #{finish(bins, sum).ToString("x16")}"
end

# source -> stage (tokenize, 1:n) -> sink
def flow(text : Array(UInt8), lines : Array(Line), passes : Int32) : String
  bins = Array(Int64).new(BINS)
  tokens = 0_i64
  sum = 0_u64
  dataflow(capacity: 16384, batch: 512) do
    ls = source(state: lines) { |all, out|
      for p in 0...passes
        for k in 0...all.size
          out << all[k]
        end
      end
    }
    hs = ls.stage(state: text) { |t, l, out|
      h = 2166136261_u32
      for b in t.AsSpan(l.start, l.length)
        if b == 32_u8
          out << h
          h = 2166136261_u32
        else
          h = (h ^ b.to_u32) &* 16777619_u32
        end
      end
      out << h
    }
    hs.sink { |h|
      bins[(h & 4095_u32).to_i32] += 1
      sum = sum &+ h.to_u64
      tokens += 1
    }
  end
  "#{tokens} #{finish(bins, sum).ToString("x16")}"
end

# the same network with the tokenizer replicated over the pool (pure stage, ordered sink); 16 workers = cores / 2
# on the 32-core forge, as in ../Program.cs
def flow_par(text : Array(UInt8), lines : Array(Line), passes : Int32) : String
  bins = Array(Int64).new(BINS)
  tokens = 0_i64
  sum = 0_u64
  dataflow(capacity: 16384, batch: 512) do
    ls = source(state: lines) { |all, out|
      for p in 0...passes
        for k in 0...all.size
          out << all[k]
        end
      end
    }
    hs = ls.stage(state: text, par: true, workers: 16, batch: 1024) { |t, l, out|
      h = 2166136261_u32
      for b in t.AsSpan(l.start, l.length)
        if b == 32_u8
          out << h
          h = 2166136261_u32
        else
          h = (h ^ b.to_u32) &* 16777619_u32
        end
      end
      out << h
    }
    hs.sink { |h|
      bins[(h & 4095_u32).to_i32] += 1
      sum = sum &+ h.to_u64
      tokens += 1
    }
  end
  "#{tokens} #{finish(bins, sum).ToString("x16")}"
end

# Channel(T) + spawn: the ADR 0005 form of the same pipeline
def channels(text : Array(UInt8), lines : Array(Line), passes : Int32) : String
  c1 = Channel(Line).new(16384)
  c2 = Channel(UInt32).new(16384)
  gen = spawn do
    for p in 0...passes
      for k in 0...lines.size
        c1.send(lines[k])
      end
    end
    c1.close
  end
  tok = spawn do
    c1.each { |l|
      h = 2166136261_u32
      for i in l.start...(l.start + l.length)
        b = text[i]
        if b == 32_u8
          c2.send(h)
          h = 2166136261_u32
        else
          h = (h ^ b.to_u32) &* 16777619_u32
        end
      end
      c2.send(h)
    }
    c2.close
  end
  bins = Array(Int64).new(BINS)
  tokens = 0_i64
  sum = 0_u64
  c2.each { |h|
    bins[(h & 4095_u32).to_i32] += 1
    sum = sum &+ h.to_u64
    tokens += 1
  }
  gen.wait
  tok.wait
  "#{tokens} #{finish(bins, sum).ToString("x16")}"
end

def run(v : String, text : Array(UInt8), lines : Array(Line), passes : Int32) : String
  case v
  when "serial" then serial(text, lines, passes)
  when "dataflow" then flow(text, lines, passes)
  when "dataflow-par" then flow_par(text, lines, passes)
  when "channels" then channels(text, lines, passes)
  else raise "unknown variant #{v}"
  end
end

gib = ARGV.size > 0 ? ARGV[0].to_f : 1.0
variants = (ARGV.size > 1 ? ARGV[1] : "serial,dataflow,dataflow-par,channels").split(",")
idx = List(Line).new(1 << 21)
text = generate(64 << 20, idx)
lines = idx.ToArray
target = (gib * (1 << 30).to_f).to_i64
passes = Math.Max(1_i64, (target + text.size.to_i64 - 1_i64) / text.size.to_i64).to_i32
total_lines = passes.to_i64 * lines.size.to_i64
total_bytes = passes.to_i64 * text.size.to_i64
puts "text: #{text.size >> 20} MiB x #{passes} passes = #{(total_bytes.to_f / (1 << 30).to_f).round(2)} GiB, #{(total_lines.to_f / 1e6).round(1)} M lines, #{Environment.ProcessorCount} cores"
reference = ""
variants.each { |v|
  run(v, text, lines, 1)   # warm-up: JIT, tiering
  GC.Collect
  a0 = GC.GetTotalAllocatedBytes(true)
  sw = System.Diagnostics.Stopwatch.StartNew
  r = run(v, text, lines, passes)
  s = sw.Elapsed.TotalSeconds
  alloc = GC.GetTotalAllocatedBytes(true) - a0
  reference = r if reference == ""
  tokens = r.split(" ")[0].to_i64
  ok = r == reference ? "ok" : "MISMATCH"
  puts "RESULT #{v} #{(s * 1000.0).round(1)} ms  #{(tokens.to_f / s / 1e6).round(1)} M tokens/s  #{((tokens + total_lines).to_f / s / 1e6).round(1)} M items/s  alloc #{(alloc.to_f / 1024.0).round(1)} KiB  #{r} #{ok}"
}
