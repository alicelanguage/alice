def make_text(n : Int32) : String
  sb = System.Text.StringBuilder.new
  seed : Int64 = 42
  for i in 0...n
    seed = (seed * 1103515245 + 12345) % 2147483648
    r = seed / 65536
    idx = (r % 100) * (r / 100 % 100)
    sb.Append("w")
    sb.Append(idx)
    sb.Append(" ")
  end
  sb.ToString().TrimEnd()
end

def kernel(text : String) : Int64
  counts = Dictionary(String, Int32).new
  for w in text.Split(" ")
    counts[w] = counts.GetValueOrDefault(w) + 1
  end
  top : Int64 = 0
  for k, v in counts
    top = v if v > top
  end
  top * 100000 + counts.Count
end

def measure(text : String, check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel(text)
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

text = make_text(1000000)
check = kernel(text)
# warm-up: >= 50 calls and >= 1 s so tier-1 + PGO code replaces tier-0/OSR
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 50 || warm.Elapsed.TotalMilliseconds < 1000.0
  kernel(text)
  wn += 1
end
times = Array(Float64).new(15)
for r in 0...15
  times[r] = measure(text, check)
end
System.Array.Sort(times)
puts "RESULT wordfreq #{times[7]} #{check}"
